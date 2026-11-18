using System;
using System.Drawing;
using System.Windows.Forms;

namespace CaptionRelay.UI
{
    /// <summary>
    /// The app's taskbar window. It starts minimized WITHOUT taking focus (the user stays in their window), so
    /// CaptionRelay shows up on the taskbar next to other apps. Clicking it shows the status and the hotkeys.
    /// Closing it (the X, or "Close window" on the taskbar) exits CaptionRelay completely.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int SW_SHOWMINNOACTIVE = 7;
        private static readonly Color Ok = Color.FromArgb(46, 157, 79);
        private static readonly Color Bad = Color.FromArgb(198, 40, 40);
        private static readonly Color Dim = Color.FromArgb(90, 90, 90);

        private readonly Label _captionsDot;
        private readonly Label _captionsText;
        private readonly Label _extensionDot;
        private readonly Label _extensionText;
        private readonly Label _pendingText;
        private readonly CheckBox _tapPreview;
        private bool _settingTapPreview;
        private readonly Icon? _icon;
        private bool _closingForExit;

        public MainForm(string sendKey, string skipKey, string tapKey, int tapWords, bool tapPreview)
        {
            Text = "CaptionRelay";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            AutoScaleMode = AutoScaleMode.Dpi;
            Padding = new Padding(18, 14, 18, 14);
            BackColor = Color.White;
            _icon = LoadIcon();
            if (_icon != null)
            {
                Icon = _icon;
            }

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            Controls.Add(layout);

            layout.Controls.Add(L("CaptionRelay", bold: true, size: 14f, top: 0));

            (_captionsDot, _captionsText) = StatusRow(layout);
            (_extensionDot, _extensionText) = StatusRow(layout);
            _pendingText = L("", color: Dim);
            _pendingText.Margin = new Padding(22, 2, 0, 4);
            layout.Controls.Add(_pendingText);

            layout.Controls.Add(L("Shortcuts (work in any window)", bold: true, top: 12));
            layout.Controls.Add(KeyRow(tapKey, $"Tap: the last {tapWords} words, tap again for {tapWords} more. Hold: paste and send"));
            layout.Controls.Add(KeyRow(sendKey, "Paste everything heard since the last skip"));
            layout.Controls.Add(KeyRow(skipKey, "Skip: start fresh from here"));

            _tapPreview = new CheckBox
            {
                Text = "Show the tap preview while tapping (see-through, above the indicator)",
                Checked = tapPreview,
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 0),
            };
            _tapPreview.CheckedChanged += (_, _) =>
            {
                if (!_settingTapPreview)
                {
                    TapPreviewToggled?.Invoke(_tapPreview.Checked);
                }
            };
            layout.Controls.Add(_tapPreview);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
            buttons.Controls.Add(MakeButton("Set up browser extension…", () => SetupRequested?.Invoke()));
            buttons.Controls.Add(MakeButton("Open sessions folder", () => SessionsRequested?.Invoke()));
            layout.Controls.Add(buttons);

            var exitRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            Button exit = MakeButton("Exit CaptionRelay", RequestExit);
            exit.ForeColor = Bad;
            exitRow.Controls.Add(exit);
            Label hint = L("Closing this window also exits CaptionRelay and Live Captions.", size: 9f, color: Dim);
            hint.Margin = new Padding(4, 7, 0, 0);
            exitRow.Controls.Add(hint);
            layout.Controls.Add(exitRow);

            SetStatus(false, false, "Chat", 0, 0);
        }

        public event Action? SetupRequested;

        /// <summary>The user turned the tap preview checkbox on or off.</summary>
        public event Action<bool>? TapPreviewToggled;

        public event Action? SessionsRequested;

        /// <summary>The user closed the window or clicked Exit: the whole app should exit.</summary>
        public event Action? ExitRequested;

        /// <summary>Shows the window minimized on the taskbar without activating it (focus stays where it is).</summary>
        public void ShowMinimizedOnTaskbar()
        {
            PerformLayout();
            Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 3));
            ShowWindow(Handle, SW_SHOWMINNOACTIVE);
        }

        /// <summary>Shows the tap preview setting (changed elsewhere) without raising <see cref="TapPreviewToggled"/>.</summary>
        public void SetTapPreview(bool on)
        {
            _settingTapPreview = true;
            _tapPreview.Checked = on;
            _settingTapPreview = false;
        }

        /// <summary>Closes the window as part of exiting (no second exit request).</summary>
        public void CloseForExit()
        {
            _closingForExit = true;
            if (!IsDisposed)
            {
                Close();
            }
        }

        public void SetStatus(bool captions, bool connected, string provider, int pendingWords, int queued)
        {
            Set(_captionsDot, _captionsText, captions, captions ? "Live Captions: running" : "Live Captions: not available");
            Set(_extensionDot, _extensionText, connected, connected ? $"Browser extension: connected ({provider})" : "Browser extension: not connected");
            string pending = pendingWords == 1 ? "1 word heard since the last skip" : $"{pendingWords} words heard since the last skip";
            _pendingText.Text = queued > 0 ? $"{pending} · {queued} queued" : pending;
            string title = $"CaptionRelay · {(captions && connected ? "ready" : "not ready")}";
            if (Text != title)
            {
                Text = title;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closingForExit)
            {
                // X button, Alt+F4, taskbar "Close window", Windows shutdown: exit the whole app (after this event).
                e.Cancel = e.CloseReason != CloseReason.WindowsShutDown;
                BeginInvoke(new Action(RequestExit));
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon?.Dispose();
            }

            base.Dispose(disposing);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static Icon? LoadIcon()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                return exe != null ? Icon.ExtractAssociatedIcon(exe) : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is System.IO.IOException)
            {
                return null;
            }
        }

        private void RequestExit()
        {
            if (!_closingForExit)
            {
                ExitRequested?.Invoke();
            }
        }

        private static void Set(Label dot, Label text, bool ok, string message)
        {
            Color color = ok ? Ok : Bad;
            if (dot.ForeColor != color)
            {
                dot.ForeColor = color;
            }

            if (text.Text != message)
            {
                text.Text = message;
            }
        }

        private static (Label Dot, Label Text) StatusRow(TableLayoutPanel layout)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            var dot = new Label { Text = "●", AutoSize = true, ForeColor = Bad, Margin = new Padding(0, 0, 4, 0) };
            Label text = L("");
            text.Margin = new Padding(0);
            row.Controls.Add(dot);
            row.Controls.Add(text);
            layout.Controls.Add(row);
            return (dot, text);
        }

        private static Control KeyRow(string combo, string what)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            bool none = string.IsNullOrEmpty(combo) || combo == "None";
            var keys = new Label
            {
                Text = none ? "not set" : combo.Contains(' ') ? combo : combo.Replace("+", " + "),
                AutoSize = false,
                Size = new Size(110, 26),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(246, 248, 250),
                Font = new Font("Segoe UI", 9.5f, none ? FontStyle.Italic : FontStyle.Bold),
                Margin = new Padding(0, 0, 10, 0),
            };
            Label text = L(what);
            text.Margin = new Padding(0, 3, 0, 0);
            row.Controls.Add(keys);
            row.Controls.Add(text);
            return row;
        }

        private static Label L(string text, bool bold = false, float size = 10f, Color? color = null, int top = 4) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = color ?? Color.FromArgb(30, 30, 30),
            Margin = new Padding(0, top, 0, 2),
        };

        private static Button MakeButton(string text, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0, 0, 8, 0),
                UseVisualStyleBackColor = true,
            };
            b.Click += (_, _) => onClick();
            return b;
        }
    }
}
