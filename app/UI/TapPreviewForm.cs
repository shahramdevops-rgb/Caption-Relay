using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CaptionRelay.Captions;
using static CaptionRelay.UI.NativeMethods;

namespace CaptionRelay.UI
{
    /// <summary>
    /// See-through preview of the tap selection, shown above the indicator while the tap key is being pressed: the
    /// words that will be pasted are highlighted, the older pending words before them are dimmed. It can never be
    /// clicked or focused: clicks go through to the window below (WS_EX_TRANSPARENT + layered), it never activates
    /// and is not in Alt+Tab. Call its methods on the UI thread.
    /// </summary>
    public sealed class TapPreviewForm : Form
    {
        private const int MaxLines = 14;

        private static readonly Color Background = Color.FromArgb(20, 20, 24);
        private static readonly Color Border = Color.FromArgb(90, 90, 104);
        private static readonly Color Header = Color.FromArgb(240, 190, 90);
        private static readonly Color DimText = Color.FromArgb(130, 130, 140);
        private static readonly Color SelectedText = Color.FromArgb(255, 255, 255);
        private static readonly Color Highlight = Color.FromArgb(46, 96, 190);

        private readonly Font _font = new Font("Segoe UI", 10f);
        private readonly Font _headerFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        private readonly List<Line> _lines = new List<Line>();
        private string _header = "";
        private string? _empty;
        private int _lineHeight;

        public TapPreviewForm(double opacity)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Background;
            DoubleBuffered = true;
            Opacity = Math.Clamp(opacity, 0.2, 1.0);
            Text = "CaptionRelay tap preview";
            AccessibleName = "CaptionRelay tap preview";
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;

                // Click-through (transparent to the mouse), never activated, not in Alt+Tab, always on top.
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>
        /// Shows (or updates) the preview with its bottom-left corner just above <paramref name="anchor"/> (the
        /// indicator's top-left corner).
        /// </summary>
        public void ShowPreview(TapPreview preview, int taps, int wordLimit, Point anchor, bool sendOnRelease = false)
        {
            _header = HeaderText(preview, taps, wordLimit, sendOnRelease);
            _empty = !preview.CaptionsAvailable ? "Live Captions not available"
                : preview.Selected.Length == 0 ? "Nothing new to paste"
                : null;

            Rectangle area = Screen.FromPoint(anchor).WorkingArea;
            int width = Math.Min(LogicalToDeviceUnits(520), Math.Max(LogicalToDeviceUnits(200), area.Right - anchor.X - LogicalToDeviceUnits(8)));
            int pad = LogicalToDeviceUnits(10);
            LayOutWords(preview, width - 2 * pad);

            int headerHeight = LogicalToDeviceUnits(20);
            int bodyLines = _empty != null ? 1 : Math.Max(1, _lines.Count);
            int height = pad + headerHeight + bodyLines * _lineHeight + pad;
            int top = Math.Max(area.Top, anchor.Y - LogicalToDeviceUnits(6) - height);
            Bounds = new Rectangle(anchor.X, top, width, height);
            if (!Visible)
            {
                Show();
            }

            Invalidate();
        }

        /// <summary>"Tap ×2 · last 30 words · 26 to paste · hold to send" (or "release: paste + send" once held long enough).</summary>
        internal static string HeaderText(TapPreview preview, int taps, int wordLimit, bool sendOnRelease = false)
        {
            string text = $"Tap ×{taps} · last {wordLimit} words";
            if (preview.Selected.Length > 0)
            {
                text += $" · {preview.SelectedWords} to paste";
            }

            if (preview.AboveWindow > 0)
            {
                text += $" ({preview.AboveWindow} above the Live Captions window)";
            }

            return text + (sendOnRelease ? " · RELEASE: PASTE + SEND" : " · hold to send");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using GraphicsPath path = Rounded(new Rectangle(0, 0, Width, Height), LogicalToDeviceUnits(10));
            Region = new Region(path);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = LogicalToDeviceUnits(10);
            using (var pen = new Pen(Border))
            using (GraphicsPath path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(10)))
            {
                g.DrawPath(pen, path);
            }

            var headerRect = new Rectangle(pad, pad, Width - 2 * pad, LogicalToDeviceUnits(18));
            TextRenderer.DrawText(g, _header, _headerFont, headerRect, Header, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            int y = pad + LogicalToDeviceUnits(20);
            if (_empty != null)
            {
                TextRenderer.DrawText(g, _empty, _font, new Point(pad, y), DimText, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                return;
            }

            using var highlight = new SolidBrush(Highlight);
            foreach (Line line in _lines)
            {
                foreach (Piece p in line.Pieces)
                {
                    var rect = new Rectangle(pad + p.X, y, p.Width, _lineHeight);
                    if (p.Selected)
                    {
                        // Highlight the word plus the space after it, so a selected run reads as one band.
                        g.FillRectangle(highlight, rect.X - LogicalToDeviceUnits(2), rect.Y + 1, rect.Width + p.SpaceAfter + LogicalToDeviceUnits(2), rect.Height - 2);
                    }

                    TextRenderer.DrawText(g, p.Text, _font, rect, p.Selected ? SelectedText : DimText, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                }

                y += _lineHeight;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _font.Dispose();
                _headerFont.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>Word-wraps the dimmed context and the selected text; keeps the last <see cref="MaxLines"/> lines.</summary>
        private void LayOutWords(TapPreview preview, int maxWidth)
        {
            _lines.Clear();
            _lineHeight = TextRenderer.MeasureText("Ag", _font, Size.Empty, TextFormatFlags.NoPadding).Height + LogicalToDeviceUnits(4);
            if (preview.Selected.Length == 0)
            {
                return;
            }

            int space = TextRenderer.MeasureText("x x", _font, Size.Empty, TextFormatFlags.NoPadding).Width -
                        TextRenderer.MeasureText("xx", _font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var words = new List<(string Text, bool Selected)>();
            foreach (string w in preview.Context.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add((w, false));
            }

            foreach (string w in preview.Selected.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add((w, true));
            }

            var line = new Line();
            int x = 0;
            for (int i = 0; i < words.Count; i++)
            {
                int w = TextRenderer.MeasureText(words[i].Text, _font, Size.Empty, TextFormatFlags.NoPadding).Width;
                if (x > 0 && x + w > maxWidth)
                {
                    _lines.Add(line);
                    line = new Line();
                    x = 0;
                }

                bool spaceSelected = words[i].Selected && i + 1 < words.Count && words[i + 1].Selected;
                line.Pieces.Add(new Piece(words[i].Text, x, Math.Min(w, maxWidth), words[i].Selected, spaceSelected ? space : 0));
                x += w + space;
            }

            _lines.Add(line);
            if (_lines.Count > MaxLines)
            {
                _lines.RemoveRange(0, _lines.Count - MaxLines);
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

        private sealed class Line
        {
            public List<Piece> Pieces { get; } = new List<Piece>();
        }

        private readonly struct Piece
        {
            public Piece(string text, int x, int width, bool selected, int spaceAfter)
            {
                Text = text;
                X = x;
                Width = width;
                Selected = selected;
                SpaceAfter = spaceAfter;
            }

            public string Text { get; }

            public int X { get; }

            public int Width { get; }

            public bool Selected { get; }

            public int SpaceAfter { get; }
        }
    }
}
