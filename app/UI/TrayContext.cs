using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using CaptionRelay.Captions;
using CaptionRelay.Config;
using CaptionRelay.Infrastructure;
using CaptionRelay.Relay;
using CaptionRelay.Sending;

namespace CaptionRelay.UI
{
    /// <summary>
    /// The app's UI: taskbar window, tray icon + menu, the indicator pill. Polls capture/relay status every 250 ms on
    /// the UI thread (no cross-thread UI calls) and shows SendController feedback. Every way of exiting (menu Exit,
    /// closing the taskbar window, the extension's switch) goes through <see cref="ExitThreadCore"/>.
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
        private readonly ToolStripMenuItem _hideLcItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _tapPreviewItem;
        private readonly TapPreviewForm _tapPreview;
        private readonly StartupRegistration _startup = new StartupRegistration();
        private readonly IndicatorForm _indicator;
        private readonly MainForm _main;
        private readonly Timer _timer = new Timer { Interval = 250 };
        private readonly Icon _iconOk = AppIcons.Create(true);
        private readonly Icon _iconBad = AppIcons.Create(false);
        private readonly SendController _controller;
        private readonly string _sendKeyLabel;
        private string? _lastProvider;
        private IntPtr _previousForeground;
        private bool? _lastAllOk;
        private SetupForm? _setup;
        private bool _exiting;

        // What the Live Captions window shows, read in the background while tapping (UI Automation, cross-process).
        private string? _visibleText;
        private bool _visibleReading;
        private long _visibleReadAt = long.MinValue / 2;

        public TrayContext(AppConfig config, CaptionCapture capture, RelayServer relay, SendController controller, CaptionsReader reader, LiveCaptionsWindow liveCaptionsWindow, string sendKeyLabel, string skipKeyLabel, string tapKeyLabel)
        {
            _config = config;
            _sendKeyLabel = sendKeyLabel;
            _capture = capture;
            _relay = relay;
            _reader = reader;
            _liveCaptionsWindow = liveCaptionsWindow;

            _showItem = new ToolStripMenuItem("Hide indicator", null, (_, _) => SetIndicatorVisible(!_indicator!.Visible));
            _hideLcItem = new ToolStripMenuItem("Hide Live Captions window", null, (_, _) => ToggleHideLiveCaptions()) { Checked = config.HideLiveCaptions };
            _menu = new ContextMenuStrip();
            _menu.Items.Add(_showItem);
            _menu.Items.Add(_hideLcItem);
            _menu.Items.Add(_tapPreviewItem = new ToolStripMenuItem("Show tap preview", null, (_, _) => SetTapPreview(!_config.TapPreview)) { Checked = config.TapPreview });
            _menu.Items.Add(_startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartWithWindows()) { Checked = config.StartWithWindows });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Set up browser extension…", null, (_, _) => ShowSetup()));
            _menu.Items.Add(new ToolStripMenuItem("Open sessions folder", null, (_, _) => OpenSessions()));
            _menu.Items.Add(new ToolStripMenuItem("Copy token (for the extension)", null, (_, _) => CopyToken()));
            _menu.Items.Add(new ToolStripMenuItem("Open config", null, (_, _) => OpenConfig()));
            _menu.Items.Add(new ToolStripMenuItem("Reconnect Live Captions", null, (_, _) => ReconnectLiveCaptions()));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

            _main = new MainForm(sendKeyLabel, skipKeyLabel, tapKeyLabel, controller.TapWords, config.TapPreview);
            _main.TapPreviewToggled += SetTapPreview;
            _main.SetupRequested += ShowSetup;
            _main.SessionsRequested += OpenSessions;
            _main.ExitRequested += ExitThread;
            _main.ShowMinimizedOnTaskbar();

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

            _tapPreview = new TapPreviewForm(config.TapPreviewOpacity);

            _controller = controller;
            controller.Feedback += OnFeedback;
            controller.TapChanged += UpdateTapPreview;
            controller.ChatFocusRequested += OnChatFocusRequested;
            _timer.Tick += (_, _) => UpdateStatus();
            _timer.Start();
            UpdateStatus();

            if (relay.StartError != null)
            {
                _indicator.Flash(FeedbackKind.Error, $"Port {config.Port} in use", 6000);
            }

            Log.Info($"UI ready (indicator {(config.IndicatorVisible ? "shown" : "hidden")} at {_indicator.PillLocation.X},{_indicator.PillLocation.Y}).");

            if (!config.ExtensionSetUp)
            {
                // First run: give an already installed extension a moment to connect before showing the guide.
                var firstRun = new Timer { Interval = 3000 };
                firstRun.Tick += (_, _) =>
                {
                    firstRun.Dispose();
                    if (!_config.ExtensionSetUp && !_relay.IsConnected)
                    {
                        ShowSetup();
                    }
                };
                firstRun.Start();
            }
        }

        private void ShowSetup()
        {
            if (_setup != null && !_setup.IsDisposed)
            {
                _setup.WindowState = FormWindowState.Normal;
                _setup.Activate();
                return;
            }

            Log.Info("Showing the browser extension setup window.");
            _setup = new SetupForm(AppPaths.ExtensionDir, () => _relay.IsConnected, _sendKeyLabel);
            _setup.Connected += MarkExtensionSetUp;
            _setup.FormClosed += (_, _) => _setup = null;
            _setup.Show();
        }

        private void MarkExtensionSetUp()
        {
            if (_config.ExtensionSetUp)
            {
                return;
            }

            _config.ExtensionSetUp = true;
            SaveConfig();
            Log.Info("Browser extension set up (first connection).");
        }

        protected override void ExitThreadCore()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            Log.Info("Exit requested.");
            _timer.Stop();
            _tapPreview.Hide();
            _setup?.Close();
            _main.CloseForExit();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _setup?.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                _menu.Dispose();
                _indicator.Dispose();
                _tapPreview.Dispose();
                _main.Dispose();
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
                _main.SetStatus(s.CaptionsAvailable, connected, provider, s.PendingWords, _controller.QueuedCount);
                _tray.Text = IndicatorText.Tooltip(s.CaptionsAvailable, connected, provider, _relay.StartError);

                if (connected && !_config.ExtensionSetUp)
                {
                    MarkExtensionSetUp();
                }

                if (_controller.Taps > 0)
                {
                    UpdateTapPreview(); // new speech while tapping changes the selection
                }

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
            UpdateStatus();
        }

        /// <summary>
        /// After a paste the extension focuses the chat (tab, window, input). A moment later, if Windows did not let
        /// the browser come to the front, CaptionRelay brings that window forward itself.
        /// </summary>
        private void OnChatFocusRequested(Rectangle? bounds)
        {
            var wait = new Timer { Interval = 200 };
            wait.Tick += (_, _) =>
            {
                wait.Dispose();
                if (_exiting)
                {
                    return;
                }

                try
                {
                    Log.Info($"Chat window focus: {ChatWindow.Activate(bounds)}.");
                }
                catch (Exception ex)
                {
                    Log.Error("Bringing the chat window to the front failed", ex);
                }
            };
            wait.Start();
        }

        /// <summary>Shows the tap selection above the indicator while tapping (if enabled); hides it otherwise.</summary>
        private void UpdateTapPreview()
        {
            try
            {
                int taps = _controller.Taps;
                if (taps == 0 || !_config.TapPreview)
                {
                    _visibleText = null; // the next tapping reads the window afresh
                    if (_tapPreview.Visible)
                    {
                        _tapPreview.Hide();
                    }

                    return;
                }

                RefreshVisibleText();
                int limit = _controller.TapWordLimit;
                Point anchor = _indicator.Visible ? new Point(_indicator.Left, _indicator.Top) : _indicator.PillLocation;
                _tapPreview.ShowPreview(_capture.PreviewLast(limit, _visibleText), taps, limit, anchor, _controller.SendOnRelease);
            }
            catch (Exception ex)
            {
                Log.Error("Tap preview failed", ex);
            }
        }

        /// <summary>
        /// Reads the text visible in the Live Captions window on a background thread (at most every 250 ms, one read
        /// at a time) and redraws the preview with it.
        /// </summary>
        private void RefreshVisibleText()
        {
            long now = Environment.TickCount64;
            if (_visibleReading || now - _visibleReadAt < 250)
            {
                return;
            }

            _visibleReading = true;
            _visibleReadAt = now;
            Task.Run(() => _reader.ReadVisible()).ContinueWith(t =>
            {
                string? text = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                try
                {
                    _indicator.BeginInvoke(new Action(() =>
                    {
                        _visibleReading = false;
                        if (_controller.Taps > 0)
                        {
                            _visibleText = text;
                            UpdateTapPreview();
                        }
                    }));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
                {
                    // Closing.
                }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// Turns the tap preview on or off, from any of its switches (tray menu, taskbar window, extension popup), and
        /// keeps them all in step. Call on the UI thread.
        /// </summary>
        public void SetTapPreview(bool on)
        {
            if (_exiting)
            {
                return;
            }

            bool changed = _config.TapPreview != on;
            _config.TapPreview = on;
            _tapPreviewItem.Checked = on;
            _main.SetTapPreview(on);
            _relay.NotifySettings(on);
            UpdateTapPreview();
            if (!changed)
            {
                return;
            }

            SaveConfig();
            Log.Info($"Tap preview: {on}.");
            _indicator.Flash(FeedbackKind.Info, on ? "Tap preview on" : "Tap preview off");
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

        private void ToggleStartWithWindows()
        {
            bool enable = !_config.StartWithWindows;
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            try
            {
                _startup.Set(enable, exe);
                _config.StartWithWindows = enable;
                _startupItem.Checked = enable;
                SaveConfig();
                Log.Info($"Start with Windows: {enable}.");
                _indicator.Flash(FeedbackKind.Info, enable ? "Starts with Windows" : "Won't start with Windows");
            }
            catch (Exception ex)
            {
                Log.Error("Changing Start with Windows failed", ex);
                _indicator.Flash(FeedbackKind.Error, "Could not change startup setting");
            }
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
