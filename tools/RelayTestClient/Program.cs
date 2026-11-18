using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptionRelay.Config;

namespace CaptionRelay.Tools
{
    /// <summary>
    /// Fake extension for testing the app without a browser. The token is read through the app's own ConfigStore
    /// (never printed).
    ///
    /// Usage: RelayTestClient --config path\config.json [--script sent,noack,queued,error] [--ackDelayMs 50]
    ///                        [--origin chrome-extension://test] [--badToken] [--pasteOnly] [--seconds 60] [--exitAfterSends N]
    /// --pasteOnly answers "sent" acks like the extension's default paste mode (submitted: false).
    /// Each incoming "send" is answered with the next script entry (the last one repeats):
    ///   sent | queued | error | noack
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string? configPath = Arg(args, "--config");
            if (configPath == null)
            {
                Console.Error.WriteLine("Usage: RelayTestClient --config path\\config.json [--script sent,noack] [--ackDelayMs 50] [--badToken] [--quit] [--seconds 60] [--exitAfterSends N]");
                return 2;
            }

            AppConfig config = ConfigStore.Load(configPath).Config;
            string[] script = (Arg(args, "--script") ?? "sent").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int ackDelay = int.Parse(Arg(args, "--ackDelayMs") ?? "50");
            int seconds = int.Parse(Arg(args, "--seconds") ?? "60");
            int exitAfter = int.Parse(Arg(args, "--exitAfterSends") ?? "0");
            string origin = Arg(args, "--origin") ?? "chrome-extension://captionrelaytestclient";
            string token = Array.IndexOf(args, "--badToken") >= 0 ? "wrong-token" : config.Token;
            bool quit = Array.IndexOf(args, "--quit") >= 0;
            bool pasteOnly = Array.IndexOf(args, "--pasteOnly") >= 0;

            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", origin);
            var clock = Stopwatch.StartNew();
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{config.Port}/relay"), CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                Say(clock, $"connect failed: {ex.Message}");
                return 3;
            }

            Say(clock, $"connected to port {config.Port} as {origin}");
            await SendJson(ws, new { type = "hello", token, version = "0.0.1", protocol = 1, client = "RelayTestClient" });

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            int sends = 0;
            int pings = 0;
            var buffer = new byte[256 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, stop.Token);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            Say(clock, $"closed by app: {ws.CloseStatus} {ws.CloseStatusDescription}");
                            return 0;
                        }

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    }
                    while (!r.EndOfMessage);

                    using JsonDocument doc = JsonDocument.Parse(sb.ToString());
                    JsonElement m = doc.RootElement;
                    string type = m.GetProperty("type").GetString() ?? "";
                    switch (type)
                    {
                        case "welcome":
                            Say(clock, $"welcome: {m}");
                            if (quit)
                            {
                                // Like the extension popup's switch turned off.
                                await SendJson(ws, new { type = "quit" });
                                Say(clock, "sent quit");
                            }

                            break;
                        case "error":
                            Say(clock, $"error from app: {m}");
                            break;
                        case "ping":
                            pings++;
                            await SendJson(ws, new { type = "pong" });
                            break;
                        case "send":
                            string id = m.GetProperty("id").GetString() ?? "";
                            string text = m.GetProperty("text").GetString() ?? "";
                            string action = script[Math.Min(sends, script.Length - 1)];
                            sends++;
                            Say(clock, $"send #{sends}: {text.Length} chars{(m.TryGetProperty("submit", out JsonElement s) && s.ValueKind == JsonValueKind.True ? " [paste + SEND]" : "")} -> {action} | {text}");
                            if (action != "noack")
                            {
                                await Task.Delay(ackDelay);
                                bool forced = m.TryGetProperty("submit", out JsonElement f) && f.ValueKind == JsonValueKind.True;
                                bool? submitted = pasteOnly && action == "sent" && !forced ? false : null;
                                await SendJson(ws, new { type = "ack", id, status = action, detail = action == "error" ? "simulated failure" : null, provider = "test-client", submitted });
                            }

                            if (exitAfter > 0 && sends >= exitAfter)
                            {
                                await Task.Delay(300);
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                                Say(clock, $"done after {sends} sends, {pings} pings");
                                return 0;
                            }

                            break;
                        default:
                            Say(clock, $"other: {m}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Say(clock, $"time limit reached after {sends} sends, {pings} pings");
            }
            catch (WebSocketException ex)
            {
                Say(clock, $"connection lost: {ex.Message}");
            }

            return 0;
        }

        private static Task SendJson(ClientWebSocket ws, object message) =>
            ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

        private static void Say(Stopwatch clock, string line) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{clock.ElapsedMilliseconds,6} ms  {line}");

        private static string? Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
