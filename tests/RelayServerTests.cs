using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptionRelay.Relay;
using CaptionRelay.Sending;
using Xunit;

namespace CaptionRelay.Tests
{
    /// <summary>Real Kestrel server on a free loopback port, real ClientWebSocket clients.</summary>
    public class RelayServerTests
    {
        private const string Token = "test-token-for-relay-tests-0123456789";

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static async Task<RelayServer> StartServer(Action<RelayOptions>? configure = null)
        {
            var options = new RelayOptions
            {
                Port = FreePort(),
                Token = Token,
                AckTimeout = TimeSpan.FromMilliseconds(500),
                HelloTimeout = TimeSpan.FromMilliseconds(500),
            };
            configure?.Invoke(options);
            var server = new RelayServer(options);
            await server.StartAsync();
            Assert.Null(server.StartError);
            Port = options.Port;
            return server;
        }

        private static int Port { get; set; }

        private sealed class Client : IDisposable
        {
            private readonly ClientWebSocket _ws = new ClientWebSocket();

            public static async Task<Client> Connect(int port, string origin = "chrome-extension://abcdefghijklmnop", string? token = Token, bool hello = true)
            {
                var c = new Client();
                if (origin.Length > 0)
                {
                    c._ws.Options.SetRequestHeader("Origin", origin);
                }

                await c._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/relay"), CancellationToken.None);
                if (hello)
                {
                    await c.Send(new { type = "hello", token, version = "0.1.0", protocol = 1, client = "test client" });
                }

                return c;
            }

            public WebSocketState State => _ws.State;

            public Task Send(object message) =>
                _ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

            /// <summary>Next message, or null if the socket closed or nothing arrived in time.</summary>
            public async Task<JsonElement?> Receive(int timeoutMs = 2000)
            {
                var buffer = new byte[64 * 1024];
                using var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    var sb = new StringBuilder();
                    while (true)
                    {
                        WebSocketReceiveResult r = await _ws.ReceiveAsync(buffer, cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            return null;
                        }

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                        if (r.EndOfMessage)
                        {
                            return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is WebSocketException)
                {
                    return null;
                }
            }

            /// <summary>Next message of the given type (skips pings unless asked for).</summary>
            public async Task<JsonElement?> ReceiveType(string type, int timeoutMs = 2000)
            {
                DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < end)
                {
                    JsonElement? m = await Receive((int)Math.Max(1, (end - DateTime.UtcNow).TotalMilliseconds));
                    if (m == null)
                    {
                        return null;
                    }

                    if (m.Value.GetProperty("type").GetString() == type)
                    {
                        return m;
                    }
                }

                return null;
            }

            public Task Close() => _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

            public void Dispose()
            {
                _ws.Abort();
                _ws.Dispose();
            }
        }

        private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < end)
            {
                await Task.Delay(20);
            }

            Assert.True(condition(), "condition not reached in time");
        }

        [Theory]
        [InlineData("chrome-extension://abcdef", null, true)]
        [InlineData("chrome-extension://abcdef/", null, true)]
        [InlineData("https://gemini.google.com", null, false)]
        [InlineData("http://evil.example", null, false)]
        [InlineData(null, null, false)]
        [InlineData("chrome-extension://", null, false)]
        [InlineData("chrome-extension://abcdef", "abcdef", true)]
        [InlineData("chrome-extension://other", "abcdef", false)]
        public void OriginPolicy(string? origin, string? allowed, bool expected)
        {
            Assert.Equal(expected, RelayServer.IsAllowedOrigin(origin, allowed == null ? new string[0] : new[] { allowed }));
        }

        [Fact]
        public async Task Hello_WithToken_GetsWelcome_AndConnects()
        {
            using RelayServer server = await StartServer();
            Assert.False(server.IsConnected);
            using Client c = await Client.Connect(Port);
            JsonElement? welcome = await c.Receive();
            Assert.Equal("welcome", welcome!.Value.GetProperty("type").GetString());
            Assert.Equal(1, welcome.Value.GetProperty("protocol").GetInt32());
            await WaitFor(() => server.IsConnected);
            Assert.Equal("test client", server.ActiveClient);
        }

        [Fact]
        public async Task Welcome_CarriesTheHotkeys()
        {
            using RelayServer server = await StartServer(o =>
            {
                o.SendKey = "Ctrl+Shift";
                o.SkipKey = "F8";
            });
            using Client c = await Client.Connect(Port);
            JsonElement? welcome = await c.Receive();
            Assert.Equal("Ctrl+Shift", welcome!.Value.GetProperty("sendKey").GetString());
            Assert.Equal("F8", welcome.Value.GetProperty("skipKey").GetString());
        }

        [Fact]
        public async Task WrongToken_GetsErrorAndIsClosed()
        {
            using RelayServer server = await StartServer();
            using Client c = await Client.Connect(Port, token: "wrong");
            JsonElement? error = await c.Receive();
            Assert.Equal("bad token", error!.Value.GetProperty("reason").GetString());
            Assert.Null(await c.Receive());
            Assert.False(server.IsConnected);
        }

        [Fact]
        public async Task NoHello_ClosedAfterTimeout()
        {
            using RelayServer server = await StartServer();
            using Client c = await Client.Connect(Port, hello: false);
            Assert.Null(await c.Receive(3000));
            Assert.False(server.IsConnected);
        }

        [Theory]
        [InlineData("https://gemini.google.com")]
        [InlineData("")]
        public async Task WebPageOrMissingOrigin_Rejected(string origin)
        {
            using RelayServer server = await StartServer();
            await Assert.ThrowsAsync<WebSocketException>(() => Client.Connect(Port, origin: origin));
        }

        [Fact]
        public async Task AllowedExtensionIds_RestrictOrigins()
        {
            using RelayServer server = await StartServer(o => o.AllowedExtensionIds = new[] { "goodid" });
            await Assert.ThrowsAsync<WebSocketException>(() => Client.Connect(Port, origin: "chrome-extension://otherid"));
            using Client c = await Client.Connect(Port, origin: "chrome-extension://goodid");
            Assert.Equal("welcome", (await c.Receive())!.Value.GetProperty("type").GetString());
        }

        [Fact]
        public async Task NotConnected_SendReturnsNotConnected()
        {
            using RelayServer server = await StartServer();
            Assert.Equal(SendStatus.NotConnected, (await server.SendAsync("hello")).Status);
        }

        [Theory]
        [InlineData("sent", null, SendStatus.Sent)]
        [InlineData("queued", null, SendStatus.Queued)]
        [InlineData("error", "input not found", SendStatus.Error)]
        [InlineData("weird", null, SendStatus.Error)]
        public async Task Send_ResultFollowsAck(string status, string? detail, SendStatus expected)
        {
            using RelayServer server = await StartServer();
            using Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            await WaitFor(() => server.IsConnected);

            Task<SendOutcome> sending = server.SendAsync("What is a delegate?");
            JsonElement send = (await c.ReceiveType("send"))!.Value;
            Assert.Equal("What is a delegate?", send.GetProperty("text").GetString());
            await c.Send(new { type = "ack", id = send.GetProperty("id").GetString(), status, detail, provider = "gemini" });

            SendOutcome outcome = await sending;
            Assert.Equal(expected, outcome.Status);
            Assert.Equal("gemini", outcome.Provider);
            if (detail != null)
            {
                Assert.Equal(detail, outcome.Detail);
            }
        }

        [Fact]
        public async Task QueuedAck_CarriesId_LaterResultRaisesDeliveryResult()
        {
            using RelayServer server = await StartServer();
            var results = new System.Collections.Concurrent.ConcurrentQueue<(string Id, SendOutcome Outcome)>();
            server.DeliveryResult += (id, o) => results.Enqueue((id, o));
            using Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            await WaitFor(() => server.IsConnected);

            Task<SendOutcome> sending = server.SendAsync("while answering");
            JsonElement send = (await c.ReceiveType("send"))!.Value;
            string id = send.GetProperty("id").GetString()!;
            await c.Send(new { type = "ack", id, status = "queued", provider = "gemini" });
            SendOutcome ack = await sending;
            Assert.Equal(SendStatus.Queued, ack.Status);
            Assert.Equal(id, ack.Id);

            await c.Send(new { type = "result", id, status = "sent", detail = "execCommand", provider = "gemini" });
            await WaitFor(() => results.Count == 1);
            Assert.True(results.TryDequeue(out var r));
            Assert.Equal(id, r.Id);
            Assert.Equal(SendStatus.Sent, r.Outcome.Status);

            await c.Send(new { type = "result", id = "other", status = "error", detail = "still answering after 3 min" });
            await WaitFor(() => results.Count == 1);
            Assert.True(results.TryDequeue(out r));
            Assert.Equal(SendStatus.Error, r.Outcome.Status);
            Assert.Equal("still answering after 3 min", r.Outcome.Detail);
        }

        [Fact]
        public async Task Quit_FromConnectedExtension_RaisesQuitRequested()
        {
            using RelayServer server = await StartServer();
            int quits = 0;
            server.QuitRequested += () => Interlocked.Increment(ref quits);
            using Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            await c.Send(new { type = "quit" });
            await WaitFor(() => Volatile.Read(ref quits) == 1);
        }

        [Fact]
        public async Task Quit_BeforeHello_IsRefusedAndIgnored()
        {
            using RelayServer server = await StartServer();
            int quits = 0;
            server.QuitRequested += () => Interlocked.Increment(ref quits);
            using Client c = await Client.Connect(Port, hello: false);
            try
            {
                await c.Send(new { type = "quit" }); // first message must be hello: the server refuses and closes
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException)
            {
                // Closed before the send completed: also fine.
            }

            await Task.Delay(300);
            Assert.Equal(0, Volatile.Read(ref quits));
        }

        [Fact]
        public void Dispose_Twice_IsSafe()
        {
            var server = new RelayServer(new RelayOptions { Port = FreePort(), Token = Token });
            server.Dispose();
            server.Dispose();
        }

        [Fact]
        public async Task NoAck_NoResponse_ThenLateAckIgnored_NextSendWorks()
        {
            using RelayServer server = await StartServer();
            using Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            await WaitFor(() => server.IsConnected);

            Task<SendOutcome> first = server.SendAsync("first");
            JsonElement send1 = (await c.ReceiveType("send"))!.Value;
            Assert.Equal(SendStatus.NoResponse, (await first).Status);

            await c.Send(new { type = "ack", id = send1.GetProperty("id").GetString(), status = "sent" }); // too late
            Task<SendOutcome> second = server.SendAsync("second");
            JsonElement send2 = (await c.ReceiveType("send"))!.Value;
            await c.Send(new { type = "ack", id = send2.GetProperty("id").GetString(), status = "sent" });
            Assert.Equal(SendStatus.Sent, (await second).Status);
        }

        [Fact]
        public async Task LatestConnectionReceives_FallsBackWhenItLeaves()
        {
            using RelayServer server = await StartServer();
            using Client older = await Client.Connect(Port);
            await older.ReceiveType("welcome");
            Client newer = await Client.Connect(Port);
            await newer.ReceiveType("welcome");
            await Task.Delay(100);

            Task<SendOutcome> s1 = server.SendAsync("to newer");
            JsonElement m1 = (await newer.ReceiveType("send"))!.Value;
            await newer.Send(new { type = "ack", id = m1.GetProperty("id").GetString(), status = "sent" });
            Assert.Equal(SendStatus.Sent, (await s1).Status);

            newer.Dispose();
            await Task.Delay(300);
            Assert.True(server.IsConnected);

            Task<SendOutcome> s2 = server.SendAsync("to older");
            JsonElement m2 = (await older.ReceiveType("send"))!.Value;
            Assert.Equal("to older", m2.GetProperty("text").GetString());
            await older.Send(new { type = "ack", id = m2.GetProperty("id").GetString(), status = "sent" });
            Assert.Equal(SendStatus.Sent, (await s2).Status);
        }

        [Fact]
        public async Task DisconnectWhileWaitingForAck_NotConnected()
        {
            using RelayServer server = await StartServer(o => o.AckTimeout = TimeSpan.FromSeconds(3));
            Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            await WaitFor(() => server.IsConnected);

            Task<SendOutcome> sending = server.SendAsync("text");
            await c.ReceiveType("send");
            c.Dispose();
            Assert.Equal(SendStatus.NotConnected, (await sending).Status);
            await WaitFor(() => !server.IsConnected);
        }

        [Fact]
        public async Task Ping_IsSentPeriodically_AndUnknownMessagesIgnored()
        {
            using RelayServer server = await StartServer(o => o.PingInterval = TimeSpan.FromMilliseconds(200));
            using Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            Assert.NotNull(await c.ReceiveType("ping", 1500));
            await c.Send(new { type = "pong" });
            await c.Send(new { type = "something-new", data = 1 });
            await c.Send(new { type = "log", level = "info", text = "diagnostics line\nwith newline" });
            await c.Send(new { type = "ping" });
            Assert.NotNull(await c.ReceiveType("pong", 1500));
            Assert.True(server.IsConnected);
        }

        [Fact]
        public async Task ClientClose_CompletesHandshake()
        {
            using RelayServer server = await StartServer();
            using Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            await WaitFor(() => server.IsConnected);
            await c.Close(); // throws if the server does not answer the close frame
            Assert.Equal(WebSocketState.Closed, c.State);
            await WaitFor(() => !server.IsConnected);
        }

        [Fact]
        public async Task SilentConnection_ClosedAfterIdleTimeout()
        {
            using RelayServer server = await StartServer(o => o.IdleTimeout = TimeSpan.FromMilliseconds(600));
            using Client c = await Client.Connect(Port);
            await c.ReceiveType("welcome");
            await WaitFor(() => server.IsConnected);
            await WaitFor(() => !server.IsConnected, 3000);
        }

        [Fact]
        public async Task PortInUse_StartErrorSet_NoThrow()
        {
            var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            try
            {
                int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
                using var server = new RelayServer(new RelayOptions { Port = port, Token = Token });
                await server.StartAsync();
                Assert.NotNull(server.StartError);
                Assert.Contains(port.ToString(), server.StartError);
                Assert.Equal(SendStatus.NotConnected, (await server.SendAsync("x")).Status);
            }
            finally
            {
                blocker.Stop();
            }
        }
    }
}
