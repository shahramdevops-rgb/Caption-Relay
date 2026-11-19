using System;
using System.Drawing;
using System.Windows.Forms;
using CaptionRelay.Infrastructure;
using CaptionRelay.Setup;

namespace CaptionRelay.UI
{
    /// <summary>
    /// One-time "connect the browser" guide. Chrome does not let programs install extensions that are not on the
    /// Chrome Web Store, so the user loads the unpacked extension once (Developer mode → Load unpacked → paste path).
    /// Everything else is automatic: the folder path is on the clipboard, the token comes over native messaging, and
    /// this window turns green as soon as the extension connects.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private const int TextWidth = 520;

        private readonly Func<bool> _isConnected;
        private readonly string _extensionDir;
        private readonly Label _status;
        private readonly Label _done;
        private readonly Icon _icon = AppIcons.Create(true);
        private readonly Timer _timer = new Timer { Interval = 500 };
        private bool _connected;

        public SetupForm(string extensionDir, Func<bool> isConnected, string sendKey = "F8")
        {
            _extensionDir = extensionDir;
            _isConnected = isConnected;

            Text = "CaptionRelay: connect your browser";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            AutoScaleMode = AutoScaleMode.Dpi;
            Padding = new Padding(18);
            BackColor = Color.White;
            ShowInTaskbar = true;
            Icon = _icon;

            var layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            Controls.Add(layout);

            layout.Controls.Add(L("Connect CaptionRelay to your browser", bold: true, size: 14f, top: 0));
            layout.Controls.Add(L(
                "Chrome only lets you add this extension by hand, once. It takes about 30 seconds. " +
                "There is nothing to type: the token connects by itself.",
                color: Color.FromArgb(90, 90, 90)));

            // Step 1
            layout.Controls.Add(L("1.  Open the extensions page", bold: true, top: 14));
            var openRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(18, 0, 0, 0), WrapContents = false };
            string? chrome = ExtensionInstaller.FindBrowser(Browser.Chrome);
            string? edge = ExtensionInstaller.FindBrowser(Browser.Edge);
            if (chrome != null)
            {
                openRow.Controls.Add(MakeButton("Open Chrome extensions", () => Open(Browser.Chrome)));
            }

            if (edge != null)
            {
                openRow.Controls.Add(MakeButton(chrome != null ? "Use Edge instead" : "Open Edge extensions", () => Open(Browser.Edge)));
            }

            if (chrome == null && edge == null)
            {
                openRow.Controls.Add(L("Chrome or Edge was not found. Install Chrome, then type chrome://extensions in its address bar."));
            }

            layout.Controls.Add(openRow);
            layout.Controls.Add(Indent(L(
                "If another page opens, type chrome://extensions (edge://extensions in Edge) in the address bar.",
                size: 9f,
                color: Color.FromArgb(110, 110, 110))));

            // Step 2
            layout.Controls.Add(L("2.  Turn on \"Developer mode\"", bold: true, top: 12));
            layout.Controls.Add(Indent(L("The switch is at the top right of the extensions page.")));

            // Step 3
            layout.Controls.Add(L("3.  Click \"Load unpacked\" and choose this folder", bold: true, top: 12));
            layout.Controls.Add(Indent(L(
                "The folder path is already copied. In the folder window, click in the \"Folder\" box at the bottom, " +
                "press Ctrl+V, then click \"Select Folder\".")));
            var pathRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(18, 2, 0, 0), WrapContents = false };
            pathRow.Controls.Add(new TextBox
            {
                Text = extensionDir,
                ReadOnly = true,
                Width = 380,
                BackColor = Color.FromArgb(245, 245, 245),
                Margin = new Padding(0, 4, 6, 0),
            });
            pathRow.Controls.Add(MakeButton("Copy again", CopyPath));
            layout.Controls.Add(pathRow);

            // Step 4
            layout.Controls.Add(L("4.  Done", bold: true, top: 12));
            _status = Indent(L("○  Waiting for the extension to connect…", color: Color.FromArgb(160, 100, 0)));
            layout.Controls.Add(_status);
            _done = Indent(L(
                "Open Gemini, ChatGPT or Claude in the browser. To choose which one gets your text, click the " +
                $"CaptionRelay icon in the browser toolbar (puzzle piece → CaptionRelay). Then press {sendKey} to paste what was said.",
                color: Color.FromArgb(60, 60, 60)));
            _done.Visible = false;
            layout.Controls.Add(_done);

            Button close = MakeButton("Close", Close);
            close.Anchor = AnchorStyles.Right;
            close.Margin = new Padding(0, 16, 0, 0);
            layout.Controls.Add(close);
            CancelButton = close;

            CopyPath();
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
            Poll();
        }

        /// <summary>Raised once, on the UI thread, when the extension has connected.</summary>
        public event Action? Connected;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _icon.Dispose();
            }

            base.Dispose(disposing);
        }

        private static Label L(string text, bool bold = false, float size = 10f, Color? color = null, int top = 4) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = color ?? Color.FromArgb(30, 30, 30),
            Margin = new Padding(0, top, 0, 4),
        };

        private static Label Indent(Label label)
        {
            label.Margin = new Padding(18, label.Margin.Top, 0, label.Margin.Bottom);
            return label;
        }

        private static Button MakeButton(string text, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0, 2, 8, 0),
                UseVisualStyleBackColor = true,
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        private void Open(Browser browser)
        {
            CopyPath();
            bool ok = ExtensionInstaller.OpenExtensionsPage(browser);
            Log.Info($"Setup: open {browser} extensions page: {(ok ? "ok" : "failed")}.");
            if (!ok)
            {
                MessageBox.Show(
                    this,
                    $"Could not open the browser. Open it yourself and type {(browser == Browser.Chrome ? "chrome" : "edge")}://extensions in the address bar.",
                    "CaptionRelay",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private void CopyPath()
        {
            try
            {
                Clipboard.SetText(_extensionDir);
            }
            catch (Exception ex)
            {
                Log.Warn("Setup: could not copy the extension path", ex);
            }
        }

        private void Poll()
        {
            if (_connected || !_isConnected())
            {
                return;
            }

            _connected = true;
            _timer.Stop();
            _status.Text = "✓  Connected! The extension is installed and talking to the app.";
            _status.ForeColor = Color.FromArgb(30, 130, 60);
            _status.Font = new Font(_status.Font, FontStyle.Bold);
            _done.Visible = true;
            Log.Info("Setup: extension connected.");
            Connected?.Invoke();
        }
    }
}
