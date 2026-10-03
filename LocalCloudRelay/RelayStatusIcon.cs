using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LocalCloudRelay;

internal static class RelayStatusIcon
{
    // Dark theme status colors
    public static readonly Color ColorRunning = Color.FromArgb(0, 212, 170);   // #00D4AA teal
    public static readonly Color ColorWarning = Color.FromArgb(255, 179, 71);  // #FFB347 amber
    public static readonly Color ColorError = Color.FromArgb(255, 107, 107);   // #FF6B6B coral
    public static readonly Color ColorIdle = Color.FromArgb(107, 107, 141);    // #6B6B8D muted gray

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Rounded square background
            using var path = CreateRoundedRect(2, 2, 28, 28, 7);
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);

            // Signal wave arcs (radiating from bottom-left dot)
            float opacity = (color == ColorIdle) ? 0.5f : 1.0f;
            using var wavePen = new Pen(Color.FromArgb((int)(255 * opacity), Color.White), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            // Arc 1 (innermost)
            g.DrawArc(wavePen, 2, 12, 14, 14, -90, 90);
            // Arc 2 (middle)
            g.DrawArc(wavePen, -1, 9, 20, 20, -90, 90);
            // Arc 3 (outermost)
            g.DrawArc(wavePen, -4, 6, 26, 26, -90, 90);

            // Source dot (bottom-left)
            using var dotBrush = new SolidBrush(Color.FromArgb((int)(255 * opacity), Color.White));
            g.FillEllipse(dotBrush, 6, 22, 5, 5);

            // Status glyph in the top-right corner. Every state carries a distinct
            // shape so the tray is not readable by hue alone.
            using var markPen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (color == ColorRunning)
            {
                g.DrawLines(markPen, new[] { new Point(20, 12), new Point(23, 15), new Point(28, 8) });
            }
            else if (color == ColorError)
            {
                g.DrawLine(markPen, 21, 8, 27, 14);
                g.DrawLine(markPen, 27, 8, 21, 14);
            }
            else if (color == ColorWarning)
            {
                g.DrawLine(markPen, 24, 8, 24, 14);
                g.FillEllipse(Brushes.White, 22.8f, 15.8f, 2.4f, 2.4f);
            }
            else
            {
                g.DrawLine(markPen, 20, 12, 28, 12);
            }
        }

        // Icon.FromHandle does not own the HICON, so clone it and destroy the
        // original. Without this every SetStatus leaks one GDI handle.
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
