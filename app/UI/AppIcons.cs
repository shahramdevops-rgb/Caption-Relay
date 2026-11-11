using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CaptionRelay.UI
{
    /// <summary>Tray icons drawn at runtime (no .ico files): a caption bubble with a green or red status dot.</summary>
    internal static class AppIcons
    {
        public static Icon Create(bool allOk, int size = 32)
        {
            using var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;

                // Speech bubble with two "caption" lines.
                using (var bubble = new GraphicsPath())
                {
                    bubble.AddArc(2 * s, 4 * s, 8 * s, 8 * s, 180, 90);
                    bubble.AddArc(22 * s, 4 * s, 8 * s, 8 * s, 270, 90);
                    bubble.AddArc(22 * s, 16 * s, 8 * s, 8 * s, 0, 90);
                    bubble.AddLine(14 * s, 24 * s, 8 * s, 30 * s);
                    bubble.AddLine(8 * s, 30 * s, 9 * s, 24 * s);
                    bubble.AddArc(2 * s, 16 * s, 8 * s, 8 * s, 90, 90);
                    bubble.CloseFigure();
                    using var fill = new SolidBrush(Color.FromArgb(70, 130, 230));
                    g.FillPath(fill, bubble);
                }

                using (var line = new Pen(Color.White, 2.5f * s))
                {
                    g.DrawLine(line, 8 * s, 11 * s, 23 * s, 11 * s);
                    g.DrawLine(line, 8 * s, 17 * s, 18 * s, 17 * s);
                }

                using var dot = new SolidBrush(allOk ? Color.FromArgb(80, 200, 120) : Color.FromArgb(235, 90, 90));
                using var ring = new Pen(Color.FromArgb(24, 24, 28), 1.5f * s);
                g.FillEllipse(dot, 20 * s, 20 * s, 11 * s, 11 * s);
                g.DrawEllipse(ring, 20 * s, 20 * s, 11 * s, 11 * s);
            }

            IntPtr handle = bmp.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(handle).Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }
}
