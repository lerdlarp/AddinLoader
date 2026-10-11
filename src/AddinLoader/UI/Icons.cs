using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AddinLoader.UI
{
    /// <summary>Ribbon icons drawn in code (no image files to ship).</summary>
    public static class Icons
    {
        static readonly Color Accent = Color.FromRgb(0xFF, 0x96, 0x96);

        public static ImageSource Loader(int size) => Draw(size, "↻", Accent);       // ↻
        public static ImageSource Faceless(int size) => Draw(size, "▶", Accent);     // ▶
        public static ImageSource Console(int size) => Draw(size, ">_", Color.FromRgb(0x44, 0x44, 0x44), Colors.White);
        public static ImageSource Reload(int size) => Draw(size, "⟳", Color.FromRgb(0x5B, 0x9B, 0xD5), Colors.White);

        static ImageSource Draw(int size, string glyph, Color background, Color? foreground = null)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var radius = size * 0.22;
                context.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size), radius, radius);
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    size * (glyph.Length > 1 ? 0.5 : 0.72), new SolidColorBrush(foreground ?? Colors.Black), 1.0);
                context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
