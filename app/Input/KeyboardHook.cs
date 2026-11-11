using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace CaptionRelay.Input
{
    /// <summary>
    /// Global low-level keyboard hook (WH_KEYBOARD_LL), from LiveCaptionsGemini. Never swallows keys (always
    /// CallNextHookEx). Runs on its own thread with its own message loop, so a busy UI thread can't delay system-wide
    /// keyboard input (Windows silently removes hooks that keep timing out). The callback only runs the state machine;
    /// events are raised through <c>dispatch</c> (BeginInvoke to the UI thread).
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        private readonly HotkeyStateMachine _machine;
        private readonly Action<Action> _dispatch;

        // Kept in a field: the native hook only holds a function pointer, the GC must not collect the delegate.
        private readonly HookProc _proc;

        private Thread? _thread;
        private uint _threadId;

        public KeyboardHook(Hotkey send, Hotkey skip, Action<Action> dispatch)
        {
            _machine = new HotkeyStateMachine(send, skip);
            _dispatch = dispatch;
            _proc = OnHook;
        }

        public event Action? SendPressed;

        public event Action? SkipPressed;

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
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        action = _machine.OnKey(vk, isDown: true, IsPressed);
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        action = _machine.OnKey(vk, isDown: false, IsPressed);
                    }

                    if (action == HotkeyAction.Send)
                    {
                        Raise(SendPressed);
                    }
                    else if (action == HotkeyAction.Skip)
                    {
                        Raise(SkipPressed);
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
