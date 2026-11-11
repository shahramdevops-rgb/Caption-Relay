using System;
using System.Windows.Forms;
using CaptionRelay.Captions;
using CaptionRelay.Config;
using CaptionRelay.Infrastructure;

namespace CaptionRelay
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            // Must run before any window (including a MessageBox) is created.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log.Init(new FileLogger(AppPaths.LogsDir));

            using var instance = new SingleInstance();
            if (!instance.IsFirst)
            {
                Log.Warn($"Another instance is already running; exiting (pid {Environment.ProcessId}).");
                MessageBox.Show(
                    "CaptionRelay is already running.",
                    AppInfo.Name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 1;
            }

            Application.ThreadException += (_, e) => Log.Error("Unhandled UI-thread exception", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

            Log.Info($"{AppInfo.Name} {AppInfo.Version} starting (pid {Environment.ProcessId}, dir {AppPaths.BaseDir})");

            ConfigLoadResult loaded;
            try
            {
                loaded = ConfigStore.Load(AppPaths.ConfigFile);
            }
            catch (ConfigException ex)
            {
                Log.Error("Could not load config.json", ex);
                MessageBox.Show(
                    $"Could not load config.json:\n\n{ex.Message}\n\nFix or delete the file, then start CaptionRelay again.",
                    AppInfo.Name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 2;
            }

            foreach (string warning in loaded.Warnings)
            {
                Log.Warn("Config: " + warning);
            }

            AppConfig config = loaded.Config;
            Log.Info($"Config {loaded.Outcome}: port {config.Port}, token fingerprint {TokenGenerator.Fingerprint(config.Token)}, " +
                     $"sendKey {config.SendKey}, skipKey {config.SkipKey}, pollMs {config.PollMs}, minSendChars {config.MinSendChars}");

            int deleted = SessionWriter.DeleteOld(AppPaths.SessionsDir, config.SessionRetentionDays);
            if (deleted > 0)
            {
                Log.Info($"Deleted {deleted} session file(s) older than {config.SessionRetentionDays} days.");
            }

            if (config.StartLiveCaptions && !CaptionsReader.EnsureRunning())
            {
                Log.Warn("Could not start Live Captions.");
            }

            var writer = new SessionWriter(AppPaths.SessionsDir);
            var options = new CaptureOptions { PollMs = config.PollMs, RestartLiveCaptions = config.StartLiveCaptions };
            using var capture = new CaptionCapture(new CaptionsReader(), writer, options, ensureLiveCaptions: CaptionsReader.EnsureRunning);
            capture.Start();
            Log.Info($"Capture started (poll {config.PollMs} ms, session file {writer.FilePath}).");

            // Tray icon, hotkeys and the WebSocket server are added in later phases (see ROADMAP.md).
            Application.Run(new ApplicationContext());

            Log.Info("Exiting normally.");
            return 0;
        }
    }
}
