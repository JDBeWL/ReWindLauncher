using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ReWindLauncher.UI
{
    internal static class Theme
    {
        public static readonly Color Frame = Color.FromArgb(238, 241, 230);
        public static readonly Color Ink = Color.FromArgb(52, 52, 52);
        public static readonly Color InkSoft = Color.FromArgb(78, 78, 78);
        public static readonly Color White = Color.FromArgb(255, 255, 255);
        public static readonly Color CloudBorder = Color.FromArgb(176, 205, 220);
        public static readonly Color AccentBlue = Color.FromArgb(0, 102, 204);

        private static Font _bigButtonFont;
        private static Font _smallButtonFont;
        private static Font _openDirFont;
        private static Font _footerFont;
        private static Font _comboFont;
        private static Font _bodyFont;
        private static Font _bodyBoldFont;

        // 缓存的 GDI+ 对象，只在 UI 线程复用，跨线程共享会出错
        private static readonly SolidBrush PlateShadow = new SolidBrush(Color.FromArgb(16, 0, 0, 0));
        private static readonly Dictionary<int, SolidBrush> PlateFills = new Dictionary<int, SolidBrush>();
        private static readonly Dictionary<int, Pen> PlateBorders = new Dictionary<int, Pen>();

        public static Font BigButtonFont =>
            _bigButtonFont ?? (_bigButtonFont = Dpi.Font("微软雅黑", 26f, FontStyle.Bold));
        public static Font SmallButtonFont =>
            _smallButtonFont ?? (_smallButtonFont = Dpi.Font("宋体", 13f));
        public static Font OpenDirFont =>
            _openDirFont ?? (_openDirFont = Dpi.Font("宋体", 15f));
        public static Font FooterFont =>
            _footerFont ?? (_footerFont = Dpi.Font("宋体", 12f));
        public static Font ComboFont =>
            _comboFont ?? (_comboFont = Dpi.Font("宋体", 14f));
        public static Font BodyFont =>
            _bodyFont ?? (_bodyFont = Dpi.Font("宋体", 12f));
        public static Font BodyBoldFont =>
            _bodyBoldFont ?? (_bodyBoldFont = Dpi.Font("宋体", 12f, FontStyle.Bold));

        public static Font PixelFont(string family, float pixels, FontStyle style = FontStyle.Regular)
        {
            return Dpi.Font(family, pixels, style);
        }

        public static Image LoadEmbedded(string fileName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string name = null;
            foreach (string candidate in asm.GetManifestResourceNames())
            {
                if (candidate.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                {
                    name = candidate;
                    break;
                }
            }
            if (name == null) return null;
            using (Stream stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                Image image = Image.FromStream(stream);
                return image;
            }
        }

        public static void DrawPlate(Graphics g, Rectangle rect, int alpha, bool hover, bool pressed)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            int offset = Math.Max(1, Dpi.S(1));
            g.FillRectangle(PlateShadow, rect.X + offset, rect.Y + offset, rect.Width, rect.Height);

            int fillAlpha = pressed ? Math.Max(140, alpha - 30) : (hover ? Math.Min(255, alpha + 16) : alpha);
            g.FillRectangle(FillBrush(fillAlpha), rect);

            float width = Math.Max(1f, Dpi.Scale);
            g.DrawRectangle(BorderPen(pressed ? 0 : (hover ? 1 : 2), width),
                rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        private static SolidBrush FillBrush(int alpha)
        {
            if (!PlateFills.TryGetValue(alpha, out SolidBrush brush))
            {
                brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
                PlateFills[alpha] = brush;
            }
            return brush;
        }

        private static Pen BorderPen(int state, float width)
        {
            int key = state * 1000 + (int)Math.Round(width * 100);
            if (!PlateBorders.TryGetValue(key, out Pen pen))
            {
                Color color = state == 0
                    ? Color.FromArgb(170, 200, 200, 195)
                    : Color.FromArgb(state == 1 ? 215 : 190, 225, 225, 223);
                pen = new Pen(color, width);
                PlateBorders[key] = pen;
            }
            return pen;
        }

        public static void DrawShadowText(Graphics g, string text, Font font, Rectangle rect, Color color, TextFormatFlags flags)
        {
            int offset = Math.Max(1, Dpi.S(1));
            var shadowRect = new Rectangle(rect.X + offset, rect.Y + offset, rect.Width, rect.Height);
            Color shadowColor = color == Color.White
                ? Color.FromArgb(120, 0, 0, 0)
                : Color.FromArgb(48, 255, 255, 255);
            TextRenderer.DrawText(g, text, font, shadowRect, shadowColor, flags);
            TextRenderer.DrawText(g, text, font, rect, color, flags);
        }

        public static void DrawCloseGlyph(Graphics g, Rectangle area, bool hover)
        {
            int size = Math.Max(8, Dpi.S(12));
            int x = area.X + (area.Width - size) / 2;
            int y = area.Y + (area.Height - size) / 2;
            int offset = Math.Max(1, Dpi.S(1));
            float width = Math.Max(1f, 2f * Dpi.Scale);

            using (var penShadow = new Pen(Color.FromArgb(90, 40, 40, 40), width))
            using (var pen = new Pen(Color.White, width))
            {
                g.DrawLine(penShadow, x + offset, y + offset, x + size + offset, y + size + offset);
                g.DrawLine(penShadow, x + size + offset, y + offset, x + offset, y + size + offset);
                g.DrawLine(pen, x, y, x + size, y + size);
                g.DrawLine(pen, x + size, y, x, y + size);
            }
        }

        public static void DrawStretchImage(Graphics g, Image image, Rectangle dest)
        {
            if (image == null) return;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(image, dest);
        }

        public static void DrawStretchImageAlpha(Graphics g, Image image, Rectangle dest, float opacity)
        {
            if (image == null) return;
            var matrix = new ColorMatrix { Matrix33 = Math.Max(0f, Math.Min(1f, opacity)) };
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(matrix);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }
    }
}
