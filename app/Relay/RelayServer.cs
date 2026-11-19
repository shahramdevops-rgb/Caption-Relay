using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptionRelay.Infrastructure;
using CaptionRelay.Sending;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CaptionRelay.Relay
{
    public sealed class RelayOptions
    {
        public int Port { get; set; } = 47321;

        public string Token { get; set; } = "";

        /// <summary>Empty = any chrome-extension:// origin (still needs the token).</summary>
        public string[] AllowedExtensionIds { get; set; } = new string[0];

        public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(3);

        public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(20);

        public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>A connection that sends nothing (not even pong) for this long is closed.</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(65);

        /// <summary>Send hotkey as shown to the user (e.g. "F8", "Ctrl+Shift"); sent in welcome for the extension popup.</summary>
        public string SendKey { get; set; } = "Num -";

        /// <summary>Skip hotkey as shown to the user (e.g. "F9", "None").</summary>
        public string SkipKey { get; set; } = "F9";

        /// <summary>The tap key and words per tap, for the extension popup's shortcut list.</summary>
        public string TapKey { get; set; } = "Num +";

        public int TapWords { get; set; } = 15;

        /// <summary>Whether the tap preview is on (welcome; the popup's switch shows and changes it).</summary>
        public bool TapPreview { get; set; } = true;
    }

    /// <summary>
    /// Loopback-only WebSocket server at ws://127.0.0.1:{port}/relay for the browser extension (see docs/protocol.md).
    /// Accepts only chrome-extension:// origins and a correct token in the first message (hello). The most recently
    /// connected extension receives sends; older connections stay open as fallbacks. Provider-neutral: it only moves
    /// text and acks. Thread-safe. Never logs transcript text.
    /// </summary>
    public sealed class RelayServer : IMessageSender, IDisposable
    {
        public const int ProtocolVersion = 1;
        public const string Path = "/relay";
        private const int MaxMessageBytes = 1 << 20;

        private readonly RelayOptions _options;
        private int _disposed;
        private readonly object _lock = new object();
        private readonly List<Connection> _connections = new List<Connection>();
        private readonly ConcurrentDictionary<string, PendingSend> _pending = new ConcurrentDictionary<string, PendingSend>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private WebApplication? _app;
        private int _nextId;

        public RelayServer(RelayOptions options)
        {
            _options = options;
        }

        /// <summary>Raised (any thread) when IsConnected may have changed.</summary>
        public event Action<bool>? ConnectionChanged;

        /// <inheritdoc />
        public event Action<string, SendOutcome>? DeliveryResult;

        /// <summary>The extension asked the app to shut down (popup switch turned off). Raised on a network thread.</summary>
        public event Action? QuitRequested;

        /// <summary>The extension popup turned the tap preview on or off. Raised on a network thread.</summary>
        public event Action<bool>? TapPreviewRequested;

        /// <summary>Tells every connected extension the current settings (after a change in the app).</summary>
        public void NotifySettings(bool tapPreview)
        {
            _options.TapPreview = tapPreview;
            List<Connection> open;
            lock (_lock)
            {
                open = _connections.ToList();
            }

            foreach (Connection c in open)
            {
                _ = SendQuietlyAsync(c, new { type = "settings", tapPreview });
            }
        }

        private static async Task SendQuietlyAsync(Connection c, object message)
        {
            try
            {
                await c.SendJsonAsync(message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                // Closing; the next welcome carries the setting.
            }
        }

        private void RaiseQuitRequested()
        {
            try
            {
                QuitRequested?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("QuitRequested handler failed", ex);
            }
        }

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _connections.Count > 0;
                }
            }
        }

        /// <summary>Name the active extension gave in its hello (e.g. "CaptionRelay extension 0.1.0").</summary>
        public string? ActiveClient
        {
            get
            {
                lock (_lock)
                {
                    return _connections.Count > 0 ? _connections[^1].Client : null;
                }
            }
        }

        /// <summary>The chat the active extension delivers to (hello "provider", e.g. "gemini"); null if not told.</summary>
        public string? ActiveProvider
        {
            get
            {
                lock (_lock)
                {
                    return _connections.Count > 0 ? _connections[^1].Provider : null;
                }
            }
        }

        /// <summary>Why the server could not start (e.g. port in use); null when running.</summary>
        public string? StartError { get; private set; }

        public async Task StartAsync()
        {
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = Array.Empty<string>(),
                    ContentRootPath = AppContext.BaseDirectory,
                });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, _options.Port));

                WebApplication app = builder.Build();
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
                app.Map(Path, (RequestDelegate)HandleAsync);
                await app.StartAsync().ConfigureAwait(false);
                _app = app;
                StartError = null;
                Log.Info($"Relay listening on ws://127.0.0.1:{_options.Port}{Path}.");
            }
            catch (Exception ex)
            {
                // Typically the port is in use (Kestrel throws an IOException subclass). The app keeps running.
                StartError = $"Port {_options.Port} is not available: {ex.Message}";
                Log.Error("Relay server could not start", ex);
            }
        }

        public async Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default, bool submit = false)
        {
            Connection? target;
            lock (_lock)
            {
                target = _connections.Count > 0 ? _connections[^1] : null;
            }

            if (target == null)
            {
                return new SendOutcome(SendStatus.NotConnected);
            }

            string id = Guid.NewGuid().ToString("N");
            var pending = new PendingSend(target);
            _pending[id] = pending;
            try
            {
                object message = submit ? new { type = "send", id, text, submit = true } : new { type = "send", id, text };
                await target.SendJsonAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                _pending.TryRemove(id, out _);
                Log.Warn($"Relay: sending to {target} failed: {ex.Message}");
                return new SendOutcome(SendStatus.NotConnected);
            }

            Task finished = await Task.WhenAny(pending.Completion.Task, Task.Delay(_options.AckTimeout, cancellationToken)).ConfigureAwait(false);
            if (finished != pending.Completion.Task)
            {
                _pending.TryRemove(id, out _);
                Log.Warn($"Relay: no ack from {target} within {_options.AckTimeout.TotalSeconds:0.#} s (send {id[..8]}).");
                return new SendOutcome(SendStatus.NoResponse);
            }

            SendOutcome outcome = await pending.Completion.Task.ConfigureAwait(false);
            return new SendOutcome(outcome.Status, outcome.Detail, outcome.Provider, id, outcome.Submitted, outcome.AlreadyInInput, outcome.FocusChat, outcome.ChatBounds);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _shutdown.Cancel();
            List<Connection> open;
            lock (_lock)
            {
                open = _connections.ToList();
            }

            foreach (Connection c in open)
            {
                c.Abort();
            }

            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                _app?.StopAsync(stop.Token).Wait(TimeSpan.FromSeconds(2));
                (_app as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Relay stop failed", ex);
            }
        }

        /// <summary>chrome-extension://&lt;id&gt; (Chrome and Edge), optionally restricted to the given ids.</summary>
        public static bool IsAllowedOrigin(string? origin, string[]? allowedIds)
        {
            const string scheme = "chrome-extension://";
            if (string.IsNullOrEmpty(origin) || !origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string id = origin.Substring(scheme.Length).TrimEnd('/');
            if (id.Length == 0 || id.Contains('/'))
            {
                return false;
            }

            return allowedIds == null || allowedIds.Length == 0 || allowedIds.Any(a => string.Equals(a?.Trim(), id, StringComparison.OrdinalIgnoreCase));
        }

        private async Task HandleAsync(HttpContext context)
        {
            string? origin = context.Request.Headers.Origin;
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!IsAllowedOrigin(origin, _options.AllowedExtensionIds))
            {
                Log.Warn($"Relay: rejected connection from origin '{origin ?? "(none)"}'.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var conn = new Connection(socket, Interlocked.Increment(ref _nextId), origin!);
            try
            {
                if (!await HandshakeAsync(conn).ConfigureAwait(false))
                {
                    return;
                }

                lock (_lock)
                {
                    _connections.Add(conn);
                }

                Log.Info($"Relay: {conn} connected ({conn.Client}, protocol {conn.Protocol}).");
                RaiseConnectionChanged();
                Task ping = PingLoopAsync(conn);
                await ReceiveLoopAsync(conn).ConfigureAwait(false);
                conn.Abort();
                await ping.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // Connection dropped.
            }
            catch (Exception ex)
            {
                Log.Error($"Relay: {conn} failed", ex);
            }
            finally
            {
                bool wasRegistered;
                lock (_lock)
                {
                    wasRegistered = _connections.Remove(conn);
                }

                foreach (KeyValuePair<string, PendingSend> p in _pending.ToArray())
                {
                    if (p.Value.Connection == conn && _pending.TryRemove(p.Key, out _))
                    {
                        p.Value.Completion.TrySetResult(new SendOutcome(SendStatus.NotConnected));
                    }
                }

                if (wasRegistered)
                {
                    Log.Info($"Relay: {conn} disconnected.");
                    RaiseConnectionChanged();
                }
            }
        }

        private async Task<bool> HandshakeAsync(Connection conn)
        {
            JsonElement? hello = await conn.ReceiveJsonAsync(_options.HelloTimeout, _shutdown.Token).ConfigureAwait(false);
            if (hello == null || Str(hello.Value, "type") != "hello")
            {
                Log.Warn($"Relay: {conn} sent no hello; closing.");
                await conn.CloseAsync(WebSocketCloseStatus.PolicyViolation, "expected hello").ConfigureAwait(false);
                return false;
            }

            if (!TokenMatches(Str(hello.Value, "token")))
            {
                Log.Warn($"Relay: {conn} sent a wrong token; closing.");
                await conn.SendJsonAsync(new { type = "error", reason = "bad token" }, CancellationToken.None).ConfigureAwait(false);
                await conn.CloseAsync(WebSocketCloseStatus.PolicyViolation, "bad token").ConfigureAwait(false);
                return false;
            }

            conn.Client = Str(hello.Value, "client") ?? "extension " + (Str(hello.Value, "version") ?? "?");
            conn.Provider = Str(hello.Value, "provider");
            conn.Protocol = hello.Value.TryGetProperty("protocol", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 1;
            await conn.SendJsonAsync(new { type = "welcome", app = AppInfo.Name, version = AppInfo.Version, protocol = ProtocolVersion, sendKey = _options.SendKey, skipKey = _options.SkipKey, tapKey = _options.TapKey, tapWords = _options.TapWords, tapPreview = _options.TapPreview }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        private async Task ReceiveLoopAsync(Connection conn)
        {
            while (!_shutdown.IsCancellationRequested)
            {
                JsonElement? message = await conn.ReceiveJsonAsync(_options.IdleTimeout, _shutdown.Token).ConfigureAwait(false);
                if (message == null)
                {
                    if (conn.TimedOut)
                    {
                        Log.Warn($"Relay: {conn} silent for {_options.IdleTimeout.TotalSeconds:0} s; closing.");
                    }

                    return;
                }

                JsonElement m = message.Value;
                switch (Str(m, "type"))
                {
                    case "pong":
                        break;
                    case "ack":
                        OnAck(conn, m);
                        break;
                    case "ping":
                        await conn.SendJsonAsync(new { type = "pong" }, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case "log":
                        OnExtensionLog(conn, m);
                        break;
                    case "result":
                        OnResult(conn, m);
                        break;
                    case "quit":
                        // The user turned CaptionRelay off in the extension popup (authenticated connection only).
                        Log.Info($"Quit requested by the extension ({conn.Client}).");
                        RaiseQuitRequested();
                        break;
                    case "setTapPreview":
                        if (m.TryGetProperty("value", out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                        {
                            bool on = v.ValueKind == JsonValueKind.True;
                            Log.Info($"Tap preview {(on ? "on" : "off")} requested by the extension ({conn.Client}).");
                            try
                            {
                                TapPreviewRequested?.Invoke(on);
                            }
                            catch (Exception ex)
                            {
                                Log.Error("TapPreviewRequested handler failed", ex);
                            }
                        }

                        break;
                    default:
                        break; // forward compatible: unknown types are ignored
                }
            }
        }

        private void OnAck(Connection conn, JsonElement m)
        {
            string id = Str(m, "id") ?? "";
            string status = Str(m, "status") ?? "";
            string? detail = Str(m, "detail");
            string? provider = Str(m, "provider");
            bool submitted = Submitted(m);
            if (!_pending.TryRemove(id, out PendingSend? pending))
            {
                Log.Warn($"Relay: late or unknown ack from {conn} (status {status}); ignored.");
                return;
            }

            SendOutcome outcome = status switch
            {
                "sent" => new SendOutcome(SendStatus.Sent, detail, provider, submitted: submitted, alreadyInInput: AlreadyInInput(m), focusChat: Focus(m, out System.Drawing.Rectangle? bounds), chatBounds: bounds),
                "queued" => new SendOutcome(SendStatus.Queued, detail, provider),
                "error" => new SendOutcome(SendStatus.Error, string.IsNullOrEmpty(detail) ? "unknown error" : detail, provider),
                _ => new SendOutcome(SendStatus.Error, $"unknown ack status '{status}'", provider),
            };
            pending.Completion.TrySetResult(outcome);
        }

        /// <summary>Final result of a send that was acked "queued".</summary>
        private void OnResult(Connection conn, JsonElement m)
        {
            string id = Str(m, "id") ?? "";
            string status = Str(m, "status") ?? "";
            SendOutcome outcome = status == "sent"
                ? new SendOutcome(SendStatus.Sent, Str(m, "detail"), Str(m, "provider"), id, Submitted(m))
                : new SendOutcome(SendStatus.Error, Str(m, "detail") ?? $"status '{status}'", Str(m, "provider"), id);
            Log.Info($"Relay: result from {conn} for queued send {(id.Length >= 8 ? id[..8] : id)}: {status}.");
            try
            {
                DeliveryResult?.Invoke(id, outcome);
            }
            catch (Exception ex)
            {
                Log.Error("DeliveryResult handler failed", ex);
            }
        }

        /// <summary>Diagnostics from the extension (page structure, timings; never chat text) go to the app log.</summary>
        private static void OnExtensionLog(Connection conn, JsonElement m)
        {
            string text = (Str(m, "text") ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (text.Length > 4000)
            {
                text = text.Substring(0, 4000) + "…";
            }

            string line = $"Extension ({conn}): {text}";
            switch (Str(m, "level"))
            {
                case "error":
                    Log.Error(line);
                    break;
                case "warn":
                    Log.Warn(line);
                    break;
                default:
                    Log.Info(line);
                    break;
            }
        }

        private async Task PingLoopAsync(Connection conn)
        {
            try
            {
                while (conn.IsOpen && !_shutdown.IsCancellationRequested)
                {
                    await Task.Delay(_options.PingInterval, conn.Aborted).ConfigureAwait(false);
                    await conn.SendJsonAsync(new { type = "ping" }, conn.Aborted).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is WebSocketException || ex is ObjectDisposedException)
            {
                // Connection closed.
            }
        }

        private bool TokenMatches(string? token)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_options.Token))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_options.Token));
        }

        private void RaiseConnectionChanged()
        {
            try
            {
                ConnectionChanged?.Invoke(IsConnected);
            }
            catch (Exception ex)
            {
                Log.Error("ConnectionChanged handler failed", ex);
            }
        }

        /// <summary>Optional "submitted": false = the text was only pasted into the chat's input. Anything else = true.</summary>
        private static bool Submitted(JsonElement e) =>
            !(e.ValueKind == JsonValueKind.Object && e.TryGetProperty("submitted", out JsonElement v) && v.ValueKind == JsonValueKind.False);

        /// <summary>
        /// Optional "focus": the extension focused the chat after a paste; an object {left, top, width, height} gives
        /// the browser window's bounds (screen DIPs), true means bounds unknown. Anything else: no focus request.
        /// </summary>
        private static bool Focus(JsonElement e, out System.Drawing.Rectangle? bounds)
        {
            bounds = null;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("focus", out JsonElement f))
            {
                return false;
            }

            if (f.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (f.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            static int N(JsonElement o, string name) => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? (int)Math.Round(d) : int.MinValue;
            int left = N(f, "left"), top = N(f, "top"), width = N(f, "width"), height = N(f, "height");
            if (left != int.MinValue && top != int.MinValue && width > 0 && height > 0)
            {
                bounds = new System.Drawing.Rectangle(left, top, width, height);
            }

            return true;
        }

        /// <summary>Optional "alreadyInInput": true = paste only, nothing added (the input already ended with the text).</summary>
        private static bool AlreadyInInput(JsonElement e) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty("alreadyInInput", out JsonElement v) && v.ValueKind == JsonValueKind.True;

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private sealed class PendingSend
        {
            public PendingSend(Connection connection)
            {
                Connection = connection;
            }

            public Connection Connection { get; }

            public TaskCompletionSource<SendOutcome> Completion { get; } = new TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Connection
        {
            private readonly WebSocket _socket;
            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
            private readonly CancellationTokenSource _abort = new CancellationTokenSource();

            public Connection(WebSocket socket, int id, string origin)
            {
                _socket = socket;
                Id = id;
                Origin = origin;
            }

            public int Id { get; }

            public string Origin { get; }

            public string Client { get; set; } = "?";

            public int Protocol { get; set; } = 1;

            public string? Provider { get; set; }

            public bool TimedOut { get; private set; }

            public bool IsOpen => _socket.State == WebSocketState.Open && !_abort.IsCancellationRequested;

            public CancellationToken Aborted => _abort.Token;

            public async Task SendJsonAsync(object message, CancellationToken cancellationToken)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            /// <summary>Next JSON message; null when the socket closed, timed out, or sent something that isn't JSON.</summary>
            public async Task<JsonElement?> ReceiveJsonAsync(TimeSpan timeout, CancellationToken shutdown)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _abort.Token);
                cts.CancelAfter(timeout);
                var buffer = new byte[16 * 1024];
                using var ms = new MemoryStream();
                try
                {
                    while (true)
                    {
                        WebSocketReceiveResult r = await _socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            // Complete the close handshake politely.
                            await CloseOutputAsync().ConfigureAwait(false);
                            return null;
                        }

                        ms.Write(buffer, 0, r.Count);
                        if (ms.Length > MaxMessageBytes)
                        {
                            Log.Warn($"Relay: {this} sent a message over {MaxMessageBytes} bytes; closing.");
                            return null;
                        }

                        if (r.EndOfMessage)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    TimedOut = !shutdown.IsCancellationRequested && !_abort.IsCancellationRequested;
                    return null;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(ms.ToArray());
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    Log.Warn($"Relay: {this} sent invalid JSON; ignored.");
                    return default(JsonElement); // Undefined kind: ignored by the caller's type switch
                }
            }

            public async Task CloseAsync(WebSocketCloseStatus status, string reason)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _socket.CloseAsync(status, reason, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    // Already gone.
                }
            }

            private async Task CloseOutputAsync()
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    // Already gone.
                }
            }

            public void Abort()
            {
                try
                {
                    _abort.Cancel();
                    _socket.Abort();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public override string ToString() => $"connection #{Id}";
        }
    }
}
