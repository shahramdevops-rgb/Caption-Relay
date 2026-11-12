using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CaptionRelay.Sending;
using static CaptionRelay.UI.NativeMethods;

namespace CaptionRelay.UI
{
    /// <summary>
    /// Tiny always-on-top status pill that never takes focus: "● Captions  ● Gemini  · 142 words pending", plus 1.5 s
    /// flash messages. Left click toggles a panel (above the pill) with the end of the pending text; drag moves it;
    /// right click raises <see cref="MenuRequested"/>. Custom painted; the window title carries the same text for UI
    /// Automation. Call its methods on the UI thread.
    /// </summary>
    public sealed class IndicatorForm : Form
    {
        public const int FlashMs = 1500;

        private static readonly Color Background = Color.FromArgb(24, 24, 28);
        private static readonly Color Border = Color.FromArgb(62, 62, 72);
        private static readonly Color TextColor = Color.FromArgb(225, 225, 230);
        private static readonly Color Dim = Color.FromArgb(140, 140, 150);
        private static readonly Color Ok = Color.FromArgb(80, 200, 120);
        private static readonly Color Bad = Color.FromArgb(235, 90, 90);
        private static readonly Color Amber = Color.FromArgb(240, 190, 90);

        private readonly Font _font = new Font("Segoe UI", 9f);
        private readonly Font _panelFont = new Font("Segoe UI", 8.5f);
        private readonly Timer _flashTimer = new Timer();
        private readonly int _pillHeight;
        private readonly int _panelHeight;
        private readonly int _width;

        private bool _captionsOk;
        private bool _extensionOk;
        private string _provider = IndicatorText.DefaultProvider;
        private int _words;
        private int _queued;
        private string _tail = "";
        private string? _flashText;
        private FeedbackKind _flashKind;
        private bool _expanded;
        private Point _pillTopLeft;
        private Point _downAt;
        private bool _mouseDown;
        private bool _moved;

        public IndicatorForm(Point? saved)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Background;
            DoubleBuffered = true;
            Opacity = 0.93;
            Text = "CaptionRelay indicator";
            AccessibleName = "CaptionRelay indicator";

            _pillHeight = LogicalToDeviceUnits(26);
            _panelHeight = LogicalToDeviceUnits(112);
            _width = LogicalToDeviceUnits(300);

            Rectangle primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Rectangle[] areas = Array.ConvertAll(Screen.AllScreens, s => s.WorkingArea);
            _pillTopLeft = IndicatorText.Place(saved, new Size(_width, _pillHeight), areas, primary, LogicalToDeviceUnits(16));
            ApplyBounds();

            _flashTimer.Interval = FlashMs;
            _flashTimer.Tick += (_, _) =>
            {
                _flashTimer.Stop();
                _flashText = null;
                Refresh2();
            };

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            Refresh2();
        }

        /// <summary>The pill was dragged to a new place (screen coordinates of its top-left corner).</summary>
        public event Action<Point>? PositionChanged;

        /// <summary>Right click: show the app menu at this screen point.</summary>
        public event Action<Point>? MenuRequested;

        public bool Expanded => _expanded;

        public Point PillLocation => _pillTopLeft;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;

                // WS_EX_TOPMOST here instead of TopMost = true: the TopMost setter can activate the window.
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        public void SetStatus(bool captionsOk, bool extensionOk, string provider, int words, string tail, int queued = 0)
        {
            if (captionsOk == _captionsOk && extensionOk == _extensionOk && provider == _provider && words == _words && tail == _tail && queued == _queued)
            {
                return;
            }

            _queued = queued;
            _captionsOk = captionsOk;
            _extensionOk = extensionOk;
            _provider = provider;
            _words = words;
            _tail = tail;
            Refresh2();
        }

        public void Flash(FeedbackKind kind, string text, int ms = FlashMs)
        {
            _flashKind = kind;
            _flashText = text;
            _flashTimer.Stop();
            _flashTimer.Interval = ms;
            _flashTimer.Start();
            Refresh2();
        }

        public void ToggleExpanded()
        {
            _expanded = !_expanded;
            ApplyBounds();
            Refresh2();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int radius = LogicalToDeviceUnits(12);
            using GraphicsPath path = Rounded(new Rectangle(0, 0, Width, Height), radius);
            Region = new Region(path);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = LogicalToDeviceUnits(10);
            int pillTop = _expanded ? _panelHeight : 0;

            using (var pen = new Pen(Border))
            using (GraphicsPath path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(12)))
            {
                g.DrawPath(pen, path);
            }

            if (_expanded)
            {
                var title = _words == 0 ? "Nothing pending" : $"Pending ({IndicatorText.Words(_words)}):";
                var titleRect = new Rectangle(pad, LogicalToDeviceUnits(6), Width - 2 * pad, LogicalToDeviceUnits(16));
                TextRenderer.DrawText(g, title, _panelFont, titleRect, Dim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                var textRect = new Rectangle(pad, titleRect.Bottom + LogicalToDeviceUnits(2), Width - 2 * pad, _panelHeight - titleRect.Bottom - LogicalToDeviceUnits(8));
                TextRenderer.DrawText(g, _tail, _panelFont, textRect, TextColor, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                using var sep = new Pen(Border);
                g.DrawLine(sep, pad, _panelHeight - 1, Width - pad, _panelHeight - 1);
            }

            int x = pad;
            int y = pillTop;
            x = DrawDot(g, x, y, _captionsOk ? Ok : Bad);
            x = DrawText(g, "Captions", x, y, TextColor) + LogicalToDeviceUnits(10);
            x = DrawDot(g, x, y, _extensionOk ? Ok : Bad);
            x = DrawText(g, _provider, x, y, TextColor) + LogicalToDeviceUnits(8);

            if (_flashText != null)
            {
                Color c = _flashKind == FeedbackKind.Success ? Ok : _flashKind == FeedbackKind.Error ? Bad : Amber;
                DrawText(g, _flashText, x, y, c, Width - x - pad);
            }
            else if (_queued > 0)
            {
                // Stays until the queued text is submitted (or fails).
                x = DrawText(g, "· queued", x, y, Amber, Width - x - pad);
                DrawText(g, " · " + IndicatorText.Words(_words), x, y, Dim, Width - x - pad);
            }
            else
            {
                DrawText(g, "· " + IndicatorText.Words(_words), x, y, Dim, Width - x - pad);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flashTimer.Dispose();
                _font.Dispose();
                _panelFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private int DrawDot(Graphics g, int x, int pillTop, Color color)
        {
            int d = LogicalToDeviceUnits(8);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, x, pillTop + (_pillHeight - d) / 2, d, d);
            return x + d + LogicalToDeviceUnits(5);
        }

        private int DrawText(Graphics g, string text, int x, int pillTop, Color color, int maxWidth = int.MaxValue)
        {
            Size size = TextRenderer.MeasureText(g, text, _font, Size.Empty, TextFormatFlags.NoPadding);
            int w = Math.Min(size.Width, Math.Max(0, maxWidth));
            var rect = new Rectangle(x, pillTop, w, _pillHeight);
            TextRenderer.DrawText(g, text, _font, rect, color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return x + w;
        }

        private void Refresh2()
        {
            Text = IndicatorText.StatusLine(_captionsOk, _extensionOk, _provider, _words, _flashText, _queued);
            AccessibleDescription = _expanded ? "expanded" : "collapsed";
            Invalidate();
        }

        private void ApplyBounds()
        {
            int height = _expanded ? _pillHeight + _panelHeight : _pillHeight;
            int top = _expanded ? _pillTopLeft.Y - _panelHeight : _pillTopLeft.Y;
            Bounds = new Rectangle(_pillTopLeft.X, top, _width, height);
        }

        private void OnMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            // MouseDown also arrives for the 2nd click of a double click (Clicks == 2), so quick clicks all toggle.
            _mouseDown = true;
            _moved = false;
            _downAt = Cursor.Position;
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_mouseDown || _moved)
            {
                return;
            }

            Point p = Cursor.Position;
            Size drag = SystemInformation.DragSize;
            if (Math.Abs(p.X - _downAt.X) < drag.Width && Math.Abs(p.Y - _downAt.Y) < drag.Height)
            {
                return;
            }

            _moved = true;
            _mouseDown = false;

            // System move loop (returns when the button is released); the window is not activated.
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);

            _pillTopLeft = new Point(Left, _expanded ? Top + _panelHeight : Top);
            PositionChanged?.Invoke(_pillTopLeft);
        }

        private void OnMouseUp(object? sender, MouseEventArgs e)
        {
            // On button up: a menu shown on button down would receive that release and could pick an item.
            if (e.Button == MouseButtons.Right)
            {
                MenuRequested?.Invoke(Cursor.Position);
                return;
            }

            if (e.Button != MouseButtons.Left || !_mouseDown)
            {
                return;
            }

            _mouseDown = false;
            if (!_moved)
            {
                ToggleExpanded();
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
