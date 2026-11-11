using System;

namespace CaptionRelay.Input
{
    public enum HotkeyAction
    {
        None,
        Send,
        Skip,
    }

    /// <summary>
    /// Decides, per key event, whether a hotkey fired (logic from LiveCaptionsGemini's KeyboardHook, tested with 12
    /// simulated scenarios there). Ctrl+Shift fires on release when both were held and no other key was pressed in
    /// between, once per press; another key while a modifier is held (Ctrl+Shift+T) cancels until both are released.
    /// A single-key hotkey (F9) fires on key down with no modifier held, ignoring auto-repeat.
    ///
    /// <paramref name="isPressed"/> in <see cref="OnKey"/> must report the state BEFORE the current event (that is what
    /// GetAsyncKeyState returns inside a low-level hook); modifier state is derived from it plus the event itself, so
    /// no state can get stuck. Not thread-safe: used on the hook thread only.
    /// </summary>
    public sealed class HotkeyStateMachine
    {
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_LCONTROL = 0xA2;
        public const int VK_RCONTROL = 0xA3;

        private readonly Hotkey _send;
        private readonly Hotkey _skip;

        private bool _armed;   // both were down together with nothing else
        private bool _spoiled; // another key was pressed while Ctrl or Shift was held
        private bool _fired;   // already fired for this press

        public HotkeyStateMachine(Hotkey send, Hotkey skip)
        {
            _send = send;
            _skip = skip;
        }

        public HotkeyAction OnKey(int vk, bool isDown, Func<int, bool> isPressed)
        {
            bool isCtrl = vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;
            bool isShift = vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT;
            bool ctrl = isCtrl ? isDown || isPressed(Opposite(vk)) : isPressed(VK_CONTROL);
            bool shift = isShift ? isDown || isPressed(Opposite(vk)) : isPressed(VK_SHIFT);
            bool altOrWin = isPressed(VK_MENU) || isPressed(VK_LWIN) || isPressed(VK_RWIN);

            if (isCtrl || isShift)
            {
                HotkeyAction action = HotkeyAction.None;
                if (isDown)
                {
                    if (ctrl && shift && !_spoiled && !altOrWin)
                    {
                        _armed = true;
                    }
                }
                else
                {
                    if (_armed && !_spoiled && !_fired)
                    {
                        _fired = true;
                        if (_send.Kind == HotkeyKind.CtrlShift)
                        {
                            action = HotkeyAction.Send;
                        }
                        else if (_skip.Kind == HotkeyKind.CtrlShift)
                        {
                            action = HotkeyAction.Skip;
                        }
                    }

                    if (!ctrl && !shift)
                    {
                        _armed = _spoiled = _fired = false;
                    }
                }

                return action;
            }

            if (!isDown)
            {
                return HotkeyAction.None;
            }

            // Any other key (Alt and Win included) while Ctrl or Shift is held: e.g. Ctrl+Shift+T.
            if (ctrl || shift)
            {
                _spoiled = true;
            }

            if (isPressed(vk))
            {
                return HotkeyAction.None; // auto-repeat
            }

            if (ctrl || shift || altOrWin)
            {
                return HotkeyAction.None;
            }

            var key = (System.Windows.Forms.Keys)vk;
            if (_send.Kind == HotkeyKind.Key && key == _send.Key)
            {
                return HotkeyAction.Send;
            }

            if (_skip.Kind == HotkeyKind.Key && key == _skip.Key)
            {
                return HotkeyAction.Skip;
            }

            return HotkeyAction.None;
        }

        private static int Opposite(int vk) => vk switch
        {
            VK_LCONTROL => VK_RCONTROL,
            VK_RCONTROL => VK_LCONTROL,
            VK_LSHIFT => VK_RSHIFT,
            VK_RSHIFT => VK_LSHIFT,
            _ => vk,
        };
    }
}
