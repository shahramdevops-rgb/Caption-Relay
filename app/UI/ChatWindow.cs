using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace CaptionRelay.UI
{
    /// <summary>
    /// Brings the browser window that holds the chat to the front after a paste, so its input (focused by the
    /// extension) receives the user's Enter. The extension asks Chrome first; Windows does not always let a background
    /// app take the foreground, so CaptionRelay, which just handled the user's key press, does it with the usual
    /// AttachThreadInput step. The window is found by the bounds the extension reported (screen DIPs), else the
    /// topmost browser window.
    /// </summary>
    internal static class ChatWindow
    {
        private const int SW_RESTORE = 9;
        private const int GA_ROOTOWNER = 3;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int Tolerance = 64; // DIPs, summed over x, y, width and height (invisible frame borders)

        private static readonly string[] BrowserProcesses = { "chrome", "msedge", "brave", "vivaldi", "opera" };

        /// <summary>Brings the chat's browser window to the front. Returns what happened, for the log.</summary>
        public static string Activate(Rectangle? dipBounds)
        {
            IntPtr target = Find(dipBounds);
            if (target == IntPtr.Zero)
            {
                return "no browser window found";
            }

            if (IsIconic(target))
            {
                ShowWindow(target, SW_RESTORE);
            }

            IntPtr foreground = GetForegroundWindow();
            if (foreground == target || GetAncestor(foreground, GA_ROOTOWNER) == target)
            {
                return "already in front";
            }

            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint me = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != me && AttachThreadInput(me, foregroundThread, true);
            try
            {
                BringWindowToTop(target);
                SetForegroundWindow(target);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(me, foregroundThread, false);
                }
            }

            return GetForegroundWindow() == target ? "brought to the front" : "Windows did not allow it";
        }

        /// <summary>
        /// Index of the window to use: the one closest to <paramref name="want"/> (within the tolerance), else the
        /// first (topmost) one; -1 when there is none.
        /// </summary>
        internal static int Pick(IReadOnlyList<Rectangle> dipRects, Rectangle? want)
        {
            if (dipRects.Count == 0)
            {
                return -1;
            }

            if (want is Rectangle w)
            {
                int best = -1;
                int bestDistance = int.MaxValue;
                for (int i = 0; i < dipRects.Count; i++)
                {
                    Rectangle r = dipRects[i];
                    int d = Math.Abs(r.X - w.X) + Math.Abs(r.Y - w.Y) + Math.Abs(r.Width - w.Width) + Math.Abs(r.Height - w.Height);
                    if (d < bestDistance)
                    {
                        best = i;
                        bestDistance = d;
                    }
                }

                if (bestDistance <= Tolerance)
                {
                    return best;
                }
            }

            return 0;
        }

        private static IntPtr Find(Rectangle? dipBounds)
        {
            var windows = new List<IntPtr>();
            var rects = new List<Rectangle>();
            var title = new StringBuilder(8);
            var className = new StringBuilder(64);
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 || GetWindowTextLength(h) == 0)
                {
                    return true;
                }

                className.Clear();
                GetClassName(h, className, className.Capacity);
                if (className.ToString() != "Chrome_WidgetWin_1" || !IsBrowser(h) || !GetWindowRect(h, out RECT r))
                {
                    return true;
                }

                double scale = 96.0 / Math.Max(96, GetDpiForWindow(h));
                var dip = new Rectangle((int)Math.Round(r.Left * scale), (int)Math.Round(r.Top * scale), (int)Math.Round((r.Right - r.Left) * scale), (int)Math.Round((r.Bottom - r.Top) * scale));
                if (dip.Width < 200 || dip.Height < 150)
                {
                    return true;
                }

                windows.Add(h);
                rects.Add(dip);
                return true;
            }, IntPtr.Zero);

            int i = Pick(rects, dipBounds);
            return i >= 0 ? windows[i] : IntPtr.Zero;
        }

        private static bool IsBrowser(IntPtr h)
        {
            GetWindowThreadProcessId(h, out uint pid);
            try
            {
                using Process p = Process.GetProcessById((int)pid);
                return Array.IndexOf(BrowserProcesses, p.ProcessName.ToLowerInvariant()) >= 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, int flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
