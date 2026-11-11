using System;
using System.Windows.Forms;
using CaptionRelay.Captions;
using CaptionRelay.Config;
using CaptionRelay.Infrastructure;
using CaptionRelay.Input;
using CaptionRelay.Relay;
using CaptionRelay.Sending;

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

            // Hidden control: its handle lets the hook thread BeginInvoke onto this (UI) thread.
            using var invoker = new Control();
            _ = invoker.Handle;

            Hotkey sendKey = ParseHotkey(config.SendKey, AppConfig.DefaultSendKey, "sendKey");
            Hotkey skipKey = ParseHotkey(config.SkipKey, AppConfig.DefaultSkipKey, "skipKey");
            if (sendKey.Kind != HotkeyKind.None && sendKey.Equals(skipKey))
            {
                Log.Warn($"sendKey and skipKey are both {sendKey}; skip is disabled.");
                skipKey = Hotkey.None;
            }

            using var relay = new RelayServer(new RelayOptions
            {
                Port = config.Port,
                Token = config.Token,
                AllowedExtensionIds = config.AllowedExtensionIds,
            });
            relay.ConnectionChanged += connected => Log.Info(connected ? $"Extension connected: {relay.ActiveClient}." : "No extension connected.");
            relay.StartAsync().GetAwaiter().GetResult();

            var controller = new SendController(capture, relay, config.MinSendChars, config.Prefix);
            controller.Feedback += (kind, message) => Log.Info($"Feedback ({kind}): {message}");

            using var hook = new KeyboardHook(sendKey, skipKey, action => invoker.BeginInvoke(action));
            hook.SendPressed += controller.OnSendPressed;
            hook.SkipPressed += controller.OnSkipPressed;
            try
            {
                hook.Start();
                Log.Info($"Hotkeys: send = {sendKey}, skip = {skipKey}.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error("Keyboard hook failed; hotkeys are disabled", ex);
            }

            // Tray icon and indicator come in Phase 6 (see ROADMAP.md).
            Application.Run(new ApplicationContext());

            Log.Info("Exiting normally.");
            return 0;
        }

        private static Hotkey ParseHotkey(string text, string fallback, string name)
        {
            if (Hotkey.TryParse(text, out Hotkey hotkey))
            {
                return hotkey;
            }

            Log.Warn($"Config: {name} \"{text}\" is not a valid key; using {fallback}.");
            Hotkey.TryParse(fallback, out hotkey);
            return hotkey;
        }
    }
}
