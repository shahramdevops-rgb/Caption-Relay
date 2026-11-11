using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CaptionRelay.Captions
{
    /// <summary>
    /// Keeps the Live Captions window out of sight while it keeps working (from LiveCaptionsGemini). Tested there:
    /// moved off-screen it still produces captions and UI Automation still reads them; minimizing is ignored by Live
    /// Captions, and a hidden (SW_HIDE) window disappears from UI Automation — so off-screen is the only option. The
    /// window is put back when hiding stops or the app closes.
    /// </summary>
    public sealed class LiveCaptionsWindow : IDisposable
    {
        private const string WindowClass = "LiveCaptionsDesktopWindow";
        private const int OffScreen = -32000;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

        private readonly object _lock = new object();
        private Timer? _timer;
        private Point? _original; // where it was before we moved it

        public bool IsHiding => _timer != null;

        /// <summary>Moves Live Captions off-screen now and keeps it there (it may restart or be reopened).</summary>
        public void StartHiding()
        {
            HideNow();
            _timer ??= new Timer(_ => HideNow(), null, CheckInterval, CheckInterval);
        }

        /// <summary>Stops hiding and puts the window back where it was.</summary>
        public void StopHiding()
        {
            _timer?.Dispose();
            _timer = null;
            lock (_lock)
            {
                IntPtr hwnd = Find();
                if (hwnd != IntPtr.Zero && _original is Point p)
                {
                    Move(hwnd, p.X, p.Y);
                }

                _original = null;
            }
        }

        /// <summary>When hiding is off: if a previous run crashed and left Live Captions off-screen, bring it back.</summary>
        public static void RescueIfOffScreen()
        {
            IntPtr hwnd = Find();
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r) || r.Left > OffScreen / 2)
            {
                return;
            }

            Point p = DefaultPosition();
            Move(hwnd, p.X, p.Y);
        }

        public void Dispose() => StopHiding();

        // Top of the primary screen, where Live Captions floats by default.
        private static Point DefaultPosition()
        {
            Rectangle area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            return new Point(area.Left + 48, area.Top + 12);
        }

        private void HideNow()
        {
            lock (_lock)
            {
                IntPtr hwnd = Find();
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r))
                {
                    return;
                }

                if (r.Left <= OffScreen / 2)
                {
                    // Already off-screen (e.g. left there by a crashed run): put it somewhere sane on exit.
                    _original ??= DefaultPosition();
                    return;
                }

                _original ??= new Point(r.Left, r.Top);
                Move(hwnd, OffScreen, OffScreen);
            }
        }

        private static IntPtr Find()
        {
            foreach (Process process in Process.GetProcessesByName(CaptionsReader.ProcessName))
            {
                using (process)
                {
                    uint pid = (uint)process.Id;
                    IntPtr found = IntPtr.Zero;
                    EnumWindows(
                        (h, _) =>
                        {
                            GetWindowThreadProcessId(h, out uint p);
                            if (p != pid)
                            {
                                return true;
                            }

                            var cls = new StringBuilder(64);
                            GetClassName(h, cls, cls.Capacity);
                            if (cls.ToString() != WindowClass)
                            {
                                return true;
                            }

                            found = h;
                            return false;
                        },
                        IntPtr.Zero);
                    if (found != IntPtr.Zero)
                    {
                        return found;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private static void Move(IntPtr hwnd, int x, int y) =>
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
