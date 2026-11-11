using System;
using System.Windows.Forms;

namespace CaptionRelay
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Phase 0 skeleton: an empty message loop. Tray icon, capture, hotkeys and the
            // WebSocket server are added in later phases (see ROADMAP.md).
            Application.Run(new ApplicationContext());
        }
    }
}
