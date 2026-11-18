using System;
using System.IO;
using System.Text;
using System.Text.Json;
using CaptionRelay.Config;

namespace CaptionRelay.Setup
{
    /// <summary>
    /// Chrome native messaging host: Chrome starts CaptionRelay.exe with the caller's origin
    /// ("chrome-extension://&lt;id&gt;/") as the first argument when the extension calls
    /// <c>chrome.runtime.sendNativeMessage</c>. The exe then answers ONE message with the relay port and token and
    /// exits, so the extension connects without the user copying the token. Chrome only lets the extension ids listed
    /// in the host manifest start it; <see cref="AppConfig.AllowedExtensionIds"/> is honoured on top of that.
    /// Wire format: 4-byte little-endian length + UTF-8 JSON, both directions.
    /// </summary>
    public static class NativeHost
    {
        public const string HostName = "com.captionrelay.host";

        private const int MaxMessageBytes = 64 * 1024;

        /// <summary>True when the process was started by Chrome/Edge as a native messaging host.</summary>
        public static bool IsNativeHostLaunch(string[] args) =>
            args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

        /// <summary>"chrome-extension://abc/" → "abc".</summary>
        public static string ExtensionIdFromOrigin(string origin) =>
            origin.Substring("chrome-extension://".Length).Trim('/');

        /// <summary>Added to the command line when the app is started by the extension ("turn on" in the popup).</summary>
        public const string LaunchedByExtensionArg = "--from-extension";

        /// <summary>
        /// Reads one request from <paramref name="input"/>, writes one reply. Returns the process exit code.
        /// Requests: <c>{"type":"getConfig"}</c> (default) → port + token; <c>{"type":"launch"}</c> → starts the app
        /// via <paramref name="launchApp"/> (returns e.g. "started" / "already running").
        /// </summary>
        public static int Run(Stream input, Stream output, string origin, Func<AppConfig> loadConfig, Func<string>? launchApp = null)
        {
            object reply;
            try
            {
                string? request = ReadMessage(input);
                if (request == null)
                {
                    return 1;
                }

                AppConfig config = loadConfig();
                string id = ExtensionIdFromOrigin(origin);
                if (config.AllowedExtensionIds.Length > 0 && Array.IndexOf(config.AllowedExtensionIds, id) < 0)
                {
                    reply = new { type = "error", reason = "extension not allowed" };
                }
                else if (RequestType(request) == "launch")
                {
                    reply = launchApp == null
                        ? new { type = "error", reason = "launch not supported" }
                        : (object)new { type = "launched", status = launchApp() };
                }
                else
                {
                    reply = new { type = "config", port = config.Port, token = config.Token };
                }
            }
            catch (Exception ex)
            {
                reply = new { type = "error", reason = ex.Message };
            }

            WriteMessage(output, JsonSerializer.Serialize(reply));
            return 0;
        }

        private static string RequestType(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out JsonElement t)
                    ? t.GetString() ?? ""
                    : "";
            }
            catch (JsonException)
            {
                return "";
            }
        }

        internal static string? ReadMessage(Stream input)
        {
            byte[] header = new byte[4];
            if (!ReadExactly(input, header))
            {
                return null;
            }

            int length = BitConverter.ToInt32(header, 0);
            if (length < 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException("message too long");
            }

            byte[] body = new byte[length];
            if (!ReadExactly(input, body))
            {
                return null;
            }

            return Encoding.UTF8.GetString(body);
        }

        internal static void WriteMessage(Stream output, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            output.Write(BitConverter.GetBytes(body.Length), 0, 4);
            output.Write(body, 0, body.Length);
            output.Flush();
        }

        private static bool ReadExactly(Stream input, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = input.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }
    }
}
