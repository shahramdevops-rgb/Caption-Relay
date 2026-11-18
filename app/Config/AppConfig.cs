namespace CaptionRelay.Config
{
    /// <summary>Contents of config.json (camelCase on disk). Defaults are used for missing fields.</summary>
    public sealed class AppConfig
    {
        public const int DefaultPort = 47321;
        public const string DefaultSendKey = "NumMinus";
        public const string DefaultSkipKey = "F9";
        public const string DefaultTapKey = "NumPlus";
        public const int DefaultTapHoldSendMs = 400;
        public const int DefaultTapWords = 15;
        public const int DefaultTapPasteDelayMs = 400;
        public const double DefaultTapPreviewOpacity = 0.6;
        public const int DefaultMinSendChars = 3;
        public const int DefaultPollMs = 120;
        public const int DefaultSessionRetentionDays = 30;

        /// <summary>Loopback WebSocket port (ws://127.0.0.1:{port}/relay).</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>Shared secret the extension must send in its hello. Generated on first run.</summary>
        public string Token { get; set; } = "";

        /// <summary>"NumMinus" (numpad -), "NumPlus", "CtrlShift", any System.Windows.Forms.Keys name, or "None".</summary>
        public string SendKey { get; set; } = DefaultSendKey;

        /// <summary>Any System.Windows.Forms.Keys name, or "None".</summary>
        public string SkipKey { get; set; } = DefaultSkipKey;

        /// <summary>
        /// Tap key: each tap selects <see cref="TapWords"/> more of the latest words; when the taps stop (after
        /// <see cref="TapPasteDelayMs"/>) the selection is pasted. "CtrlShift", any Keys name, or "None".
        /// </summary>
        public string TapKey { get; set; } = DefaultTapKey;

        /// <summary>Words per tap of <see cref="TapKey"/>.</summary>
        public int TapWords { get; set; } = DefaultTapWords;

        /// <summary>How long after the last tap the selection is pasted.</summary>
        public int TapPasteDelayMs { get; set; } = DefaultTapPasteDelayMs;

        /// <summary>Holding the tap key at least this long pastes the selection and sends it on release.</summary>
        public int TapHoldSendMs { get; set; } = DefaultTapHoldSendMs;

        /// <summary>Show the see-through preview of the tap selection (click-through, never takes focus).</summary>
        public bool TapPreview { get; set; } = true;

        /// <summary>Opacity of the tap preview, 0.2-1.</summary>
        public double TapPreviewOpacity { get; set; } = DefaultTapPreviewOpacity;

        /// <summary>Text put before every sent message.</summary>
        public string Prefix { get; set; } = "";

        public int MinSendChars { get; set; } = DefaultMinSendChars;

        public int PollMs { get; set; } = DefaultPollMs;

        public bool StartLiveCaptions { get; set; } = true;

        /// <summary>Close Live Captions when CaptionRelay exits (menu Exit, closing its taskbar window, extension switch off).</summary>
        public bool CloseLiveCaptionsOnExit { get; set; } = true;

        /// <summary>Indicator position in screen pixels; null = default (bottom-left, 17 px from the left edge).</summary>
        public int? IndicatorX { get; set; }

        public int? IndicatorY { get; set; }

        public bool IndicatorVisible { get; set; } = true;

        /// <summary>Session transcripts in sessions/ older than this are deleted on startup; 0 = keep forever.</summary>
        public int SessionRetentionDays { get; set; } = DefaultSessionRetentionDays;

        /// <summary>Move the Live Captions window off-screen while the app runs (restored on exit).</summary>
        public bool HideLiveCaptions { get; set; }

        /// <summary>Start the app when the user signs in to Windows (set from the tray menu).</summary>
        public bool StartWithWindows { get; set; }

        /// <summary>
        /// Extension ids allowed to connect (from chrome://extensions). Empty = any Chrome/Edge extension that knows
        /// the token. Web pages (http/https origins) are always rejected.
        /// </summary>
        public string[] AllowedExtensionIds { get; set; } = new string[0];

        /// <summary>
        /// Set once a browser extension has connected. Until then the app opens the extension setup window at start.
        /// </summary>
        public bool ExtensionSetUp { get; set; }
    }
}
