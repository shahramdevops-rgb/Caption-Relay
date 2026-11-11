using System;
using System.Windows.Forms;

namespace CaptionRelay.Input
{
    public enum HotkeyKind
    {
        None,

        /// <summary>Ctrl+Shift pressed and released with no other key in between.</summary>
        CtrlShift,

        /// <summary>A single non-modifier key (e.g. F9), pressed with no modifiers held.</summary>
        Key,
    }

    /// <summary>A configured hotkey (from LiveCaptionsGemini, tested there).</summary>
    public readonly struct Hotkey : IEquatable<Hotkey>
    {
        public Hotkey(HotkeyKind kind, Keys key)
        {
            Kind = kind;
            Key = key;
        }

        public static Hotkey None => new Hotkey(HotkeyKind.None, Keys.None);

        public HotkeyKind Kind { get; }

        public Keys Key { get; }

        /// <summary>"None"/"" → disabled, "CtrlShift" → modifier-only combo, otherwise a Keys name like "F8".</summary>
        public static bool TryParse(string? text, out Hotkey hotkey)
        {
            hotkey = None;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.Replace("+", "").Equals("CtrlShift", StringComparison.OrdinalIgnoreCase))
            {
                hotkey = new Hotkey(HotkeyKind.CtrlShift, Keys.None);
                return true;
            }

            // Enum.TryParse also accepts numbers and combined flags ("F8, Control"); allow plain key names only.
            if (char.IsDigit(text[0]) || text.Contains(','))
            {
                return false;
            }

            if (!Enum.TryParse(text, ignoreCase: true, out Keys key))
            {
                return false;
            }

            if (key == Keys.None || (key & Keys.Modifiers) != 0 || IsModifierKey(key))
            {
                return false;
            }

            hotkey = new Hotkey(HotkeyKind.Key, key);
            return true;
        }

        public bool Equals(Hotkey other) => Kind == other.Kind && Key == other.Key;

        public override bool Equals(object? obj) => obj is Hotkey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Kind, Key);

        public override string ToString() => Kind switch
        {
            HotkeyKind.CtrlShift => "Ctrl+Shift",
            HotkeyKind.Key => Key.ToString(),
            _ => "None",
        };

        private static bool IsModifierKey(Keys key) =>
            key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
            key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
            key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu || key == Keys.LWin || key == Keys.RWin;
    }
}
