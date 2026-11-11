using System;
using System.Drawing;
using System.Globalization;

namespace CaptionRelay.UI
{
    /// <summary>Pure formatting/placement rules of the indicator (unit tested).</summary>
    public static class IndicatorText
    {
        public const string DefaultProvider = "Gemini";

        /// <summary>"gemini" → "Gemini", "chatgpt" → "ChatGPT"; unknown names are capitalized; none → last known or Gemini.</summary>
        public static string ProviderLabel(string? provider, string? lastKnown = null)
        {
            string? p = string.IsNullOrWhiteSpace(provider) ? lastKnown : provider;
            if (string.IsNullOrWhiteSpace(p))
            {
                return DefaultProvider;
            }

            p = p.Trim();
            switch (p.ToLowerInvariant())
            {
                case "gemini": return "Gemini";
                case "chatgpt": return "ChatGPT";
                case "claude": return "Claude";
                case "openai": return "OpenAI";
            }

            return char.ToUpper(p[0], CultureInfo.InvariantCulture) + p.Substring(1);
        }

        public static string Words(int count) => count == 1 ? "1 word pending" : $"{count} words pending";

        /// <summary>The line read by screen readers / UI Automation (the window title).</summary>
        public static string StatusLine(bool captionsOk, bool extensionOk, string provider, int pendingWords, string? flash)
        {
            string c = captionsOk ? "Captions on" : "Captions off";
            string e = extensionOk ? provider + " connected" : provider + " not connected";
            return $"{c} | {e} | {flash ?? Words(pendingWords)}";
        }

        public static string Tooltip(bool captionsOk, bool extensionOk, string provider, int pendingWords, string? error)
        {
            string text = $"CaptionRelay - captions {(captionsOk ? "on" : "off")}, {provider} {(extensionOk ? "connected" : "not connected")}, {Words(pendingWords)}";
            if (!string.IsNullOrEmpty(error))
            {
                text += " - " + error;
            }

            return text.Length <= 127 ? text : text.Substring(0, 126) + "…"; // NotifyIcon limit
        }

        /// <summary>
        /// Where the pill goes: the saved spot if at least half of it is on some screen, otherwise bottom-right of the
        /// primary working area.
        /// </summary>
        public static Point Place(Point? saved, Size pill, Rectangle[] workingAreas, Rectangle primary, int margin)
        {
            if (saved is Point s)
            {
                var r = new Rectangle(s, pill);
                foreach (Rectangle area in workingAreas)
                {
                    Rectangle overlap = Rectangle.Intersect(area, r);
                    if (overlap.Width * overlap.Height * 2 >= pill.Width * pill.Height)
                    {
                        return s;
                    }
                }
            }

            return new Point(primary.Right - pill.Width - margin, primary.Bottom - pill.Height - margin);
        }
    }
}
