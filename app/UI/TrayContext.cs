using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CaptionRelay.Captions;
using CaptionRelay.Config;
using CaptionRelay.Infrastructure;
using CaptionRelay.Relay;
using CaptionRelay.Sending;

namespace CaptionRelay.UI
{
    /// <summary>
    /// The app's UI: tray icon + menu, the indicator pill, sounds. Polls capture/relay status every 250 ms on the UI
    /// thread (no cross-thread UI calls) and shows SendController feedback.
    /// </summary>
    public sealed class TrayContext : ApplicationContext
    {
        private readonly AppConfig _config;
        private readonly CaptionCapture _capture;
        private readonly RelayServer _relay;
        private readonly CaptionsReader _reader;
        private readonly LiveCaptionsWindow _liveCaptionsWindow;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _showItem;
        private readonly ToolStripMenuItem _soundItem;
        private readonly ToolStripMenuItem _hideLcItem;
        private readonly IndicatorForm _indicator;
        private readonly Sounds _sounds = new Sounds();
        private readonly Timer _timer = new Timer { Interval = 250 };
        private readonly Icon _iconOk = AppIcons.Create(true);
        private readonly Icon _iconBad = AppIcons.Create(false);
        private readonly SendController _controller;
        private string? _lastProvider;
        private IntPtr _previousForeground;
        private bool? _lastAllOk;

        public TrayContext(AppConfig config, CaptionCapture capture, RelayServer relay, SendController controller, CaptionsReader reader, LiveCaptionsWindow liveCaptionsWindow)
        {
            _config = config;
            _capture = capture;
            _relay = relay;
            _reader = reader;
            _liveCaptionsWindow = liveCaptionsWindow;
            _sounds.Enabled = config.Sound;

            _showItem = new ToolStripMenuItem("Hide indicator", null, (_, _) => SetIndicatorVisible(!_indicator!.Visible));
            _soundItem = new ToolStripMenuItem("Sound", null, (_, _) => ToggleSound()) { Checked = config.Sound };
            _hideLcItem = new ToolStripMenuItem("Hide Live Captions window", null, (_, _) => ToggleHideLiveCaptions()) { Checked = config.HideLiveCaptions };
            _menu = new ContextMenuStrip();
            _menu.Items.Add(_showItem);
            _menu.Items.Add(_soundItem);
            _menu.Items.Add(_hideLcItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Open sessions folder", null, (_, _) => OpenSessions()));
            _menu.Items.Add(new ToolStripMenuItem("Copy token (for the extension)", null, (_, _) => CopyToken()));
            _menu.Items.Add(new ToolStripMenuItem("Open config", null, (_, _) => OpenConfig()));
            _menu.Items.Add(new ToolStripMenuItem("Reconnect Live Captions", null, (_, _) => ReconnectLiveCaptions()));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

            // A menu takes the focus while open; give it back to the app the user was in when it closes.
            _menu.Opening += (_, _) => RememberForeground();
            _menu.Closed += (_, e) =>
            {
                Log.Info($"Menu closed ({e.CloseReason}).");
                RestoreForeground();
            };

            _tray = new NotifyIcon
            {
                Icon = _iconBad,
                Text = "CaptionRelay",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _tray.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    SetIndicatorVisible(!_indicator!.Visible);
                }
            };

            Point? saved = config.IndicatorX is int x && config.IndicatorY is int y ? new Point(x, y) : (Point?)null;
            _indicator = new IndicatorForm(saved);
            _indicator.PositionChanged += OnIndicatorMoved;
            _indicator.MenuRequested += p =>
            {
                Log.Info($"Menu requested at {p.X},{p.Y}.");
                _menu.Show(p);
            };
            if (config.IndicatorVisible)
            {
                _indicator.Show();
            }

            UpdateShowItem();

            _controller = controller;
            controller.Feedback += OnFeedback;
            _timer.Tick += (_, _) => UpdateStatus();
            _timer.Start();
            UpdateStatus();

            if (relay.StartError != null)
            {
                _indicator.Flash(FeedbackKind.Error, $"Port {config.Port} in use", 6000);
            }

            Log.Info($"UI ready (indicator {(config.IndicatorVisible ? "shown" : "hidden")} at {_indicator.PillLocation.X},{_indicator.PillLocation.Y}).");
        }

        protected override void ExitThreadCore()
        {
            Log.Info("Exit requested from the menu.");
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                _menu.Dispose();
                _indicator.Dispose();
                _sounds.Dispose();
                _iconOk.Dispose();
                _iconBad.Dispose();
            }

            base.Dispose(disposing);
        }

        private void RememberForeground()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            _previousForeground = pid == (uint)Environment.ProcessId ? _previousForeground : fg;
        }

        private void RestoreForeground()
        {
            IntPtr previous = _previousForeground;
            if (previous == IntPtr.Zero)
            {
                return;
            }

            // After the click handler (e.g. Exit) has run.
            if (_indicator.IsDisposed || !_indicator.IsHandleCreated)
            {
                return;
            }

            try
            {
                _indicator.BeginInvoke(new Action(() =>
                {
                    IntPtr fg = NativeMethods.GetForegroundWindow();
                    NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
                    if (fg == IntPtr.Zero || pid == (uint)Environment.ProcessId)
                    {
                        NativeMethods.SetForegroundWindow(previous);
                    }
                }));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                // Closing.
            }
        }

        private string Provider()
        {
            string? active = _relay.ActiveProvider;
            if (!string.IsNullOrWhiteSpace(active))
            {
                _lastProvider = active;
            }

            return IndicatorText.ProviderLabel(active, _lastProvider);
        }

        private void UpdateStatus()
        {
            try
            {
                CaptureStatus s = _capture.GetStatus();
                bool connected = _relay.IsConnected;
                string provider = Provider();
                _indicator.SetStatus(s.CaptionsAvailable, connected, provider, s.PendingWords, s.PendingTail, _controller.QueuedCount);
                _tray.Text = IndicatorText.Tooltip(s.CaptionsAvailable, connected, provider, s.PendingWords, _relay.StartError);

                bool allOk = s.CaptionsAvailable && connected;
                if (allOk != _lastAllOk)
                {
                    _tray.Icon = allOk ? _iconOk : _iconBad;
                    _lastAllOk = allOk;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Status update failed", ex);
            }
        }

        private void OnFeedback(FeedbackKind kind, string message)
        {
            _indicator.Flash(kind, message);
            _sounds.Play(kind, message);
            UpdateStatus();
        }

        private void OnIndicatorMoved(Point p)
        {
            _config.IndicatorX = p.X;
            _config.IndicatorY = p.Y;
            SaveConfig();
            Log.Info($"Indicator moved to {p.X},{p.Y}.");
        }

        private void SetIndicatorVisible(bool visible)
        {
            if (visible)
            {
                _indicator.Show();
            }
            else
            {
                _indicator.Hide();
            }

            _config.IndicatorVisible = visible;
            SaveConfig();
            UpdateShowItem();
        }

        private void UpdateShowItem() => _showItem.Text = _indicator.Visible ? "Hide indicator" : "Show indicator";

        private void ToggleSound()
        {
            _config.Sound = !_config.Sound;
            _sounds.Enabled = _config.Sound;
            _soundItem.Checked = _config.Sound;
            SaveConfig();
        }

        private void ToggleHideLiveCaptions()
        {
            _config.HideLiveCaptions = !_config.HideLiveCaptions;
            _hideLcItem.Checked = _config.HideLiveCaptions;
            if (_config.HideLiveCaptions)
            {
                _liveCaptionsWindow.StartHiding();
            }
            else
            {
                _liveCaptionsWindow.StopHiding();
            }

            SaveConfig();
            Log.Info($"Hide Live Captions window: {_config.HideLiveCaptions}.");
        }

        private void OpenSessions()
        {
            Directory.CreateDirectory(AppPaths.SessionsDir);
            Start("explorer.exe", "\"" + AppPaths.SessionsDir + "\"");
        }

        // Opened for the user in Notepad (it contains the token; never logged or shown anywhere else).
        private void OpenConfig() => Start("notepad.exe", "\"" + AppPaths.ConfigFile + "\"");

        /// <summary>Puts the token on the clipboard for the extension's options page (never logged).</summary>
        private void CopyToken()
        {
            try
            {
                Clipboard.SetText(_config.Token);
                Log.Info($"Token (fingerprint {TokenGenerator.Fingerprint(_config.Token)}) copied to the clipboard.");
                _indicator.Flash(FeedbackKind.Success, "Token copied", 2500);
            }
            catch (Exception ex)
            {
                Log.Error("Copying the token failed", ex);
                _indicator.Flash(FeedbackKind.Error, "Could not copy token");
            }
        }

        private void ReconnectLiveCaptions()
        {
            _reader.Reset();
            bool ok = CaptionsReader.EnsureRunning();
            Log.Info($"Reconnect Live Captions requested (running: {ok}).");
            _indicator.Flash(ok ? FeedbackKind.Info : FeedbackKind.Error, ok ? "Reconnecting Live Captions" : "Live Captions could not start");
        }

        private void SaveConfig()
        {
            try
            {
                ConfigStore.Save(AppPaths.ConfigFile, _config);
            }
            catch (Exception ex)
            {
                Log.Error("Saving config failed", ex);
                _indicator.Flash(FeedbackKind.Error, "Config not saved");
            }
        }

        private static void Start(string file, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not start {file}", ex);
            }
        }
    }
}
