namespace CaptionRelay.Config
{
    /// <summary>Contents of config.json (camelCase on disk). Defaults are used for missing fields.</summary>
    public sealed class AppConfig
    {
        public const int DefaultPort = 47321;
        public const string DefaultSendKey = "CtrlShift";
        public const string DefaultSkipKey = "F9";
        public const int DefaultMinSendChars = 3;
        public const int DefaultPollMs = 120;
        public const int DefaultSessionRetentionDays = 30;

        /// <summary>Loopback WebSocket port (ws://127.0.0.1:{port}/relay).</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>Shared secret the extension must send in its hello. Generated on first run.</summary>
        public string Token { get; set; } = "";

        /// <summary>"CtrlShift", any System.Windows.Forms.Keys name, or "None".</summary>
        public string SendKey { get; set; } = DefaultSendKey;

        /// <summary>Any System.Windows.Forms.Keys name, or "None".</summary>
        public string SkipKey { get; set; } = DefaultSkipKey;

        /// <summary>Text put before every sent message.</summary>
        public string Prefix { get; set; } = "";

        public int MinSendChars { get; set; } = DefaultMinSendChars;

        public int PollMs { get; set; } = DefaultPollMs;

        public bool Sound { get; set; } = true;

        public bool StartLiveCaptions { get; set; } = true;

        /// <summary>Indicator position in screen pixels; null = default (bottom-right).</summary>
        public int? IndicatorX { get; set; }

        public int? IndicatorY { get; set; }

        public bool IndicatorVisible { get; set; } = true;

        /// <summary>Session transcripts in sessions/ older than this are deleted on startup; 0 = keep forever.</summary>
        public int SessionRetentionDays { get; set; } = DefaultSessionRetentionDays;

        /// <summary>Move the Live Captions window off-screen while the app runs (restored on exit).</summary>
        public bool HideLiveCaptions { get; set; }

        /// <summary>
        /// Extension ids allowed to connect (from chrome://extensions). Empty = any Chrome/Edge extension that knows
        /// the token. Web pages (http/https origins) are always rejected.
        /// </summary>
        public string[] AllowedExtensionIds { get; set; } = new string[0];
    }
}
