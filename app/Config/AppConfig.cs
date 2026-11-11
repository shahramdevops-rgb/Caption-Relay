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
    }
}
