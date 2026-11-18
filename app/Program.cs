using System;
using System.IO;
using System.Windows.Forms;
using CaptionRelay.Captions;
using CaptionRelay.Config;
using CaptionRelay.Infrastructure;
using CaptionRelay.Input;
using CaptionRelay.Relay;
using CaptionRelay.Sending;
using CaptionRelay.Setup;
using CaptionRelay.UI;

namespace CaptionRelay
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Started by Chrome/Edge as the native messaging host: answer with port + token and exit
            // (no window, no single-instance check, no log file shared with the running app).
            if (NativeHost.IsNativeHostLaunch(args))
            {
                using Stream stdin = Console.OpenStandardInput();
                using Stream stdout = Console.OpenStandardOutput();
                return NativeHost.Run(
                    stdin,
                    stdout,
                    args[0],
                    () => ConfigStore.Load(AppPaths.ConfigFile).Config,
                    () => AppLauncher.Launch(Environment.ProcessPath ?? Application.ExecutablePath));
            }

            // Must run before any window (including a MessageBox) is created.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log.Init(new FileLogger(AppPaths.LogsDir));

            using var instance = new SingleInstance();
            if (!instance.IsFirst)
            {
                Log.Warn($"Another instance is already running; exiting (pid {Environment.ProcessId}).");
                if (Array.IndexOf(args, NativeHost.LaunchedByExtensionArg) >= 0)
                {
                    return 1; // the extension's "turn on" raced with a running app: nothing to tell the user
                }

                MessageBox.Show(
                    "CaptionRelay is already running.",
                    AppInfo.Name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 1;
            }

            Application.ThreadException += (_, e) => Log.Error("Unhandled UI-thread exception", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

            Log.Info($"{AppInfo.Name} {AppInfo.Version} starting (pid {Environment.ProcessId}, exe dir {AppPaths.ExeDir}, data dir {AppPaths.DataDir})");

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

            if (config.StartWithWindows && Environment.ProcessPath != null)
            {
                // Keep the startup entry pointing at this exe (the folder may have moved).
                try
                {
                    new StartupRegistration().Set(true, Environment.ProcessPath);
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not refresh the startup entry", ex);
                }
            }

            InstallBrowserParts();

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
            var reader = new CaptionsReader();
            using var liveCaptionsWindow = new LiveCaptionsWindow();
            if (config.HideLiveCaptions)
            {
                liveCaptionsWindow.StartHiding();
            }
            else
            {
                LiveCaptionsWindow.RescueIfOffScreen();
            }

            using var capture = new CaptionCapture(reader, writer, options, ensureLiveCaptions: CaptionsReader.EnsureRunning);
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
                SendKey = sendKey.ToString(),
                SkipKey = skipKey.ToString(),
            });
            relay.ConnectionChanged += connected => Log.Info(connected ? $"Extension connected: {relay.ActiveClient}." : "No extension connected.");
            relay.StartAsync().GetAwaiter().GetResult();

            var controller = new SendController(
                capture,
                relay,
                config.MinSendChars,
                config.Prefix,
                providerName: () => IndicatorText.ProviderLabel(relay.ActiveProvider),
                dispatch: action => invoker.BeginInvoke(action));
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

            using var tray = new TrayContext(config, capture, relay, controller, reader, liveCaptionsWindow, sendKey.ToString(), skipKey.ToString());

            // The extension popup's switch was turned off: shut everything down, as the Exit menu does.
            relay.QuitRequested += () => invoker.BeginInvoke(new Action(tray.ExitThread));

            Application.Run(tray);

            Shutdown(config, hook, relay, capture, liveCaptionsWindow);
            Log.Info("Exiting normally.");
            return 0;
        }

        /// <summary>
        /// Order matters: stop the hotkeys, disconnect the extension (its switch shows off), stop capture (it would
        /// restart Live Captions), put the Live Captions window back, then close Live Captions.
        /// </summary>
        private static void Shutdown(AppConfig config, KeyboardHook hook, RelayServer relay, CaptionCapture capture, LiveCaptionsWindow liveCaptionsWindow)
        {
            hook.Dispose();
            relay.Dispose();
            capture.Dispose();
            liveCaptionsWindow.Dispose();
            if (config.CloseLiveCaptionsOnExit)
            {
                int closed = CaptionsReader.CloseAll(TimeSpan.FromSeconds(3));
                Log.Info($"Live Captions closed ({closed} process(es)).");
            }
        }

        /// <summary>
        /// Unpacks the extension embedded in the exe and registers the native messaging host (HKCU, no admin), on
        /// every start so a moved exe or a newer build keeps working. Failures are logged, never fatal.
        /// </summary>
        private static void InstallBrowserParts()
        {
            try
            {
                bool existed = File.Exists(Path.Combine(AppPaths.ExtensionDir, "manifest.json"));
                int embedded = ExtensionInstaller.EmbeddedFileCount();
                int written = ExtensionInstaller.Extract(AppPaths.ExtensionDir);
                Log.Info($"Extension folder {AppPaths.ExtensionDir}: {embedded} files, {written} updated.");
                if (written > 0 && existed)
                {
                    Log.Info("Extension files changed; reload the extension in chrome://extensions if it was already loaded.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not unpack the browser extension", ex);
            }

            try
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                ExtensionInstaller.WriteHostManifest(AppPaths.NativeHostManifest, exe);
                ExtensionInstaller.RegisterHost(AppPaths.NativeHostManifest, Microsoft.Win32.Registry.CurrentUser);
                Log.Info($"Native messaging host {NativeHost.HostName} registered for Chrome and Edge.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not register the native messaging host", ex);
            }
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
