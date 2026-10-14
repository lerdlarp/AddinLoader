using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AddinLoader.UI
{
    /// <summary>
    /// Ribbon icons drawn in code (no image files to ship). Every glyph is vector geometry laid out on a
    /// 32x32 grid and centred on it, so the icons stay aligned at 16 and 32 px and sharp at any display scale.
    /// </summary>
    public static class Icons
    {
        static readonly Color CoralLight = Color.FromRgb(0xF4, 0x7C, 0x7C);
        static readonly Color CoralDark = Color.FromRgb(0xDC, 0x4C, 0x55);
        static readonly Color SlateLight = Color.FromRgb(0x45, 0x4B, 0x55);
        static readonly Color SlateDark = Color.FromRgb(0x2C, 0x30, 0x38);
        static readonly Color BlueLight = Color.FromRgb(0x5A, 0xA6, 0xEE);
        static readonly Color BlueDark = Color.FromRgb(0x2F, 0x7C, 0xD3);

        /// <summary>Circular arrow: load / reload an add-in.</summary>
        public static ImageSource Loader(int size) => Draw(size, CoralLight, CoralDark, false, context =>
        {
            Stroke(context, "M 22.13,11.61 A 8,8 0 1 1 11.41,10.2", Colors.White);
            Fill(context, "M 15.02,7.67 L 9,6.76 L 13.82,13.64 Z", Colors.White);
        });

        /// <summary>Play triangle (optically centred): run the last command again.</summary>
        public static ImageSource Faceless(int size) => Draw(size, CoralLight, CoralDark, false, context =>
            Fill(context, "M 12,9.5 L 12,22.5 L 23,16 Z", Colors.White, 2.5));

        /// <summary>Terminal prompt: show / hide the Console pane.</summary>
        public static ImageSource Console(int size) => Draw(size, SlateLight, SlateDark, true, context =>
        {
            Stroke(context, "M 8.5,10.5 L 14,16 L 8.5,21.5", Colors.White);
            Stroke(context, "M 16.5,22 L 23.5,22", CoralLight);
        });

        /// <summary>Two chasing arrows: reload everything.</summary>
        public static ImageSource Reload(int size) => Draw(size, BlueLight, BlueDark, false, context =>
        {
            Stroke(context, "M 8.72,13.35 A 7.75,7.75 0 0 1 22.71,12.12", Colors.White);
            Stroke(context, "M 23.28,18.65 A 7.75,7.75 0 0 1 9.29,19.88", Colors.White);
            Fill(context, "M 24.81,15.76 L 26.18,10.12 L 19.25,14.12 Z", Colors.White);
            Fill(context, "M 7.19,16.24 L 5.82,21.88 L 12.75,17.88 Z", Colors.White);
        });

        const double Grid = 32;

        static ImageSource Draw(int size, Color top, Color bottom, bool outline, Action<DrawingContext> glyph)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new ScaleTransform(size / Grid, size / Grid));
                var background = new LinearGradientBrush(top, bottom, 90);
                context.DrawRoundedRectangle(background, null, new Rect(0, 0, Grid, Grid), 7, 7);
                if (outline)
                {
                    // Keeps dark tiles visible on Revit's dark theme.
                    var border = new Pen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), 1);
                    context.DrawRoundedRectangle(null, border, new Rect(0.5, 0.5, Grid - 1, Grid - 1), 6.5, 6.5);
                }
                glyph(context);
                context.Pop();
            }

            // Render at the display's scale so the icon is not upscaled (blurry) on high-DPI screens;
            // the bitmap's DPI keeps its logical size at size x size.
            var scale = DisplayScale();
            var pixels = (int)Math.Round(size * scale);
            var bitmap = new RenderTargetBitmap(pixels, pixels, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        static void Stroke(DrawingContext context, string path, Color color)
        {
            var pen = new Pen(new SolidColorBrush(color), 2.75)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            context.DrawGeometry(null, pen, Geometry.Parse(path));
        }

        /// <summary>Fills a shape; the matching stroke rounds its corners.</summary>
        static void Fill(DrawingContext context, string path, Color color, double cornerRounding = 1)
        {
            var brush = new SolidColorBrush(color);
            context.DrawGeometry(brush, new Pen(brush, cornerRounding) { LineJoin = PenLineJoin.Round }, Geometry.Parse(path));
        }

        static double? displayScale;

        static double DisplayScale()
        {
            if (displayScale == null)
            {
                var scale = 1.0;
                try
                {
                    var dc = GetDC(IntPtr.Zero);
                    if (dc != IntPtr.Zero)
                    {
                        scale = GetDeviceCaps(dc, LogPixelsX) / 96.0;
                        ReleaseDC(IntPtr.Zero, dc);
                    }
                }
                catch (Exception)
                {
                    // Fall back to 100 %.
                }
                displayScale = scale >= 1 && scale <= 4 ? scale : 1.0;
            }
            return displayScale.Value;
        }

        const int LogPixelsX = 88;

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int index);
    }
}
