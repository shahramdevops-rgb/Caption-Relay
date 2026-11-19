using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace CaptionRelay.Input
{
    /// <summary>
    /// Global low-level keyboard hook (WH_KEYBOARD_LL), from LiveCaptionsGemini. A single-key hotkey (F7, F8, F9 …)
    /// is swallowed: its key-down, auto-repeats and key-up never reach the active app, so pressing it only does the
    /// CaptionRelay action (no Chrome caret-browsing prompt for F7, no Word "extend selection" for F8 …). Every other
    /// key, and the Ctrl+Shift combination, always goes on (CallNextHookEx). Runs on its own thread with its own
    /// message loop, so a busy UI thread can't delay system-wide keyboard input (Windows silently removes hooks that
    /// keep timing out). The callback only runs the state machine;
    /// events are raised through <c>dispatch</c> (BeginInvoke to the UI thread).
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        private readonly HotkeyStateMachine _machine;
        private readonly Action<Action> _dispatch;

        // Kept in a field: the native hook only holds a function pointer, the GC must not collect the delegate.
        private readonly HookProc _proc;

        // A key-down more than this after the last event of a swallowed key is a new press (its key-up was missed,
        // e.g. while the secure desktop had the keyboard), not an auto-repeat (those come every ~30 ms after <= 1 s).
        private const long MissedKeyUpMs = 1500;

        // Hotkey keys whose key-down was swallowed → time of their last event: their auto-repeats and key-up are
        // swallowed too. Hook thread only.
        private readonly System.Collections.Generic.Dictionary<int, long> _swallowed = new System.Collections.Generic.Dictionary<int, long>();

        // The tap key while it is held: its virtual-key code and when it went down (for "hold to send").
        private int _tapVk = -1;
        private long _tapDownAt;

        private Thread? _thread;
        private uint _threadId;

        public KeyboardHook(Hotkey send, Hotkey skip, Hotkey tap, Action<Action> dispatch)
        {
            _machine = new HotkeyStateMachine(send, skip, tap);
            _dispatch = dispatch;
            _proc = OnHook;
        }

        public event Action? SendPressed;

        public event Action? SkipPressed;

        public event Action? TapPressed;

        /// <summary>The tap key was released after being held this many milliseconds.</summary>
        public event Action<int>? TapReleased;

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            var ready = new ManualResetEventSlim();
            int error = 0;
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero)
                {
                    error = Marshal.GetLastWin32Error();
                }

                ready.Set();
                if (hook == IntPtr.Zero)
                {
                    return;
                }

                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                UnhookWindowsHookEx(hook);
            })
            {
                IsBackground = true,
                Name = "CaptionRelay keyboard hook",
            };
            _thread.Start();
            ready.Wait();
            ready.Dispose();

            if (error != 0)
            {
                _thread = null;
                throw new Win32Exception(error, "Could not install the keyboard hook.");
            }
        }

        public void Dispose()
        {
            if (_thread == null)
            {
                return;
            }

            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _thread = null;
        }

        private IntPtr OnHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = (int)wParam;
                    int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
                    HotkeyAction action = HotkeyAction.None;
                    bool swallow = false;
                    long now = Environment.TickCount64;
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        if (_swallowed.TryGetValue(vk, out long last) && now - last < MissedKeyUpMs)
                        {
                            _swallowed[vk] = now;
                            swallow = true; // auto-repeat of a swallowed hotkey (its key state never went down)
                        }
                        else
                        {
                            _swallowed.Remove(vk);
                            action = _machine.OnKey(vk, isDown: true, IsPressed);

                            // Actions on key-down only come from single-key hotkeys (Ctrl+Shift fires on key-up).
                            if (action != HotkeyAction.None)
                            {
                                _swallowed[vk] = now;
                                swallow = true;
                            }

                            if (action == HotkeyAction.Tap)
                            {
                                _tapVk = vk;
                                _tapDownAt = now;
                            }
                        }
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        action = _machine.OnKey(vk, isDown: false, IsPressed);
                        swallow = _swallowed.Remove(vk);
                        if (vk == _tapVk)
                        {
                            _tapVk = -1;
                            int held = (int)Math.Min(int.MaxValue, now - _tapDownAt);
                            Action<int>? released = TapReleased;
                            if (released != null)
                            {
                                Raise(() => released(held));
                            }
                        }
                    }

                    if (action == HotkeyAction.Send)
                    {
                        Raise(SendPressed);
                    }
                    else if (action == HotkeyAction.Skip)
                    {
                        Raise(SkipPressed);
                    }
                    else if (action == HotkeyAction.Tap)
                    {
                        Raise(TapPressed);
                    }

                    if (swallow)
                    {
                        return (IntPtr)1;
                    }
                }
                catch
                {
                    // Never let an exception escape into the native hook chain.
                }
            }

            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private void Raise(Action? handler)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                _dispatch(handler);
            }
            catch (InvalidOperationException)
            {
                // UI is closing.
            }
        }

        private static bool IsPressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
