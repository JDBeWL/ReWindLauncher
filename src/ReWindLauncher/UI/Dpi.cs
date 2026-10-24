using System;
using System.Drawing;
using System.Globalization;

namespace ReWindLauncher.UI
{
    internal static class Dpi
    {
        public static float Scale { get; private set; } = 1f;
        public static int DpiValue { get; private set; } = 96;

        public static void Init()
        {
            string overrideValue = Environment.GetEnvironmentVariable("REWINDLAUNCHER_DPI");
            float scale;
            if (!string.IsNullOrEmpty(overrideValue) &&
                float.TryParse(overrideValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
                parsed >= 1f && parsed <= 3f)
            {
                scale = parsed;
            }
            else
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                    scale = graphics.DpiX / 96f;
            }

            if (scale < 1f) scale = 1f;
            if (scale > 3f) scale = 3f;
            Scale = scale;
            DpiValue = (int)Math.Round(96 * scale);
        }

        public static int S(int value) => (int)Math.Round(value * Scale, MidpointRounding.AwayFromZero);

        public static Rectangle S(Rectangle rect) =>
            new Rectangle(S(rect.X), S(rect.Y), S(rect.Width), S(rect.Height));

        public static Point S(Point point) => new Point(S(point.X), S(point.Y));

        public static Font Font(string family, float pixels, FontStyle style = FontStyle.Regular)
        {
            float size = Math.Max(1f, pixels * Scale);
            try
            {
                return new Font(family, size, style, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Pixel);
            }
        }
    }
}
