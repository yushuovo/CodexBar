using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexBar
{
    internal static class Palette
    {
        public static readonly Color Background = Color.FromArgb(20, 23, 29);
        public static readonly Color Surface = Color.FromArgb(29, 33, 41);
        public static readonly Color Text = Color.FromArgb(238, 240, 244);
        public static readonly Color Muted = Color.FromArgb(157, 165, 180);
        public static readonly Color Accent = Color.FromArgb(163, 215, 196);
        public static readonly Color Warning = Color.FromArgb(238, 192, 124);
        public static readonly Color Line = Color.FromArgb(51, 57, 69);
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 8F);
        public static readonly Font Title = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        public static readonly Font Value = new Font("Segoe UI", 25F, FontStyle.Regular);
        public static void TextAt(Graphics g, string value, Font font, Color color, Rectangle bounds, TextFormatFlags extra)
        { TextRenderer.DrawText(g, value ?? "", font, bounds, color, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | extra); }
        public static void TextAt(Graphics g, string value, Font font, Color color, Rectangle bounds)
        { TextAt(g, value, font, color, bounds, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); }
        public static Button Button(string text)
        {
            Button button = new Button { Text = text, Font = Body, BackColor = Line, ForeColor = Text, FlatStyle = FlatStyle.Flat, Height = 34, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 73, 87);
            return button;
        }
        public static GraphicsPath Round(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath(); int d = radius * 2;
            path.AddArc(rectangle.X, rectangle.Y, d, d, 180, 90);
            path.AddArc(rectangle.Right - d, rectangle.Y, d, d, 270, 90);
            path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
    }

    public static class BallRenderer
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr handle);
        public static double FillTop(double remaining)
        {
            // Invert the circle segment area so 25% means a quarter of the ball's area.
            double target = Math.Max(0, Math.Min(100, remaining)) / 100;
            double lo = -1, hi = 1;
            for (int i = 0; i < 45; i++)
            {
                double y = (lo + hi) / 2;
                double fraction = (Math.Acos(y) - y * Math.Sqrt(Math.Max(0, 1 - y * y))) / Math.PI;
                if (fraction > target) lo = y; else hi = y;
            }
            return ((lo + hi) / 2 + 1) / 2;
        }
        public static void Draw(Graphics g, RectangleF bounds, double? weekly, double? fiveHour, bool stale, bool generic)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = bounds.Width;
            float thick = s * 0.105F;
            RectangleF ring = new RectangleF(bounds.X + thick / 2 + 1, bounds.Y + thick / 2 + 1, s - thick - 2, s - thick - 2);
            Color bright = Color.FromArgb(231, 234, 239), dark = Color.FromArgb(79, 85, 96);
            using (Pen track = new Pen(dark, thick)) g.DrawEllipse(track, ring);
            if (generic)
            {
                using (Pen accent = new Pen(Palette.Accent, thick)) g.DrawArc(accent, ring, -90, 265);
                using (Brush dot = new SolidBrush(bright)) g.FillEllipse(dot, bounds.X + s * 0.35F, bounds.Y + s * 0.35F, s * 0.3F, s * 0.3F);
                return;
            }
            if (fiveHour.HasValue && !stale)
            {
                using (Pen available = new Pen(bright, thick))
                {
                    float sweep = (float)(360 * Math.Max(0, Math.Min(100, fiveHour.Value)) / 100);
                    if (sweep >= 359.999F) g.DrawEllipse(available, ring);
                    else if (sweep > 0) g.DrawArc(available, ring, -90, sweep);
                }
            }
            else
            {
                using (Pen unknown = new Pen(Color.FromArgb(139, 147, 163), thick))
                { unknown.DashStyle = DashStyle.Dot; g.DrawEllipse(unknown, ring); }
            }
            float inset = s * 0.22F;
            RectangleF ball = new RectangleF(bounds.X + inset, bounds.Y + inset, s - 2 * inset, s - 2 * inset);
            using (Brush empty = new SolidBrush(dark)) g.FillEllipse(empty, ball);
            if (weekly.HasValue && !stale)
            {
                GraphicsState state = g.Save();
                using (GraphicsPath clip = new GraphicsPath())
                {
                    clip.AddEllipse(ball); g.SetClip(clip, CombineMode.Intersect);
                    float top = ball.Y + (float)FillTop(weekly.Value) * ball.Height;
                    using (Brush liquid = new SolidBrush(bright)) g.FillRectangle(liquid, ball.X, top, ball.Width, ball.Bottom - top + 1);
                }
                g.Restore(state);
            }
            else
            {
                using (Font font = new Font("Segoe UI", Math.Max(7, s * 0.23F), FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush brush = new SolidBrush(bright))
                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("?", font, brush, ball, format);
            }
        }
        public static Icon IconFor(CodexSnapshot snapshot, bool showBall)
        {
            using (Bitmap bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    double? weekly = snapshot != null && snapshot.Weekly != null ? (double?)snapshot.Weekly.Remaining : null;
                    double? five = snapshot != null && snapshot.FiveHour != null ? (double?)snapshot.FiveHour.Remaining : null;
                    Draw(graphics, new RectangleF(0, 0, 32, 32), weekly, five, snapshot == null || snapshot.Stale(DateTimeOffset.Now), !showBall);
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }

}
