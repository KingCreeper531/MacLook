using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacLook
{
    /// <summary>
    /// Mac Look's own app icon, drawn from scratch: a dusk-gradient squircle with a tiny glass
    /// menu bar and dock. Shared by MacLook.exe and MacLookSetup.exe.
    /// </summary>
    internal static class AppIcon
    {
        private static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        public static BitmapSource Render(int size)
        {
            double s = size;
            double pad = s * 0.06, plate = s - 2 * pad;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var shape = Squircle(pad, pad, plate);
                dc.PushClip(shape);

                var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.3, 1) };
                sky.GradientStops.Add(new GradientStop(C("#2B2F8F"), 0));
                sky.GradientStops.Add(new GradientStop(C("#7A4FD0"), 0.45));
                sky.GradientStops.Add(new GradientStop(C("#F07AA8"), 0.78));
                sky.GradientStops.Add(new GradientStop(C("#FFB48A"), 1));
                dc.DrawRectangle(sky, null, new Rect(0, 0, s, s));

                // Rolling hills
                Hill(dc, s, 0.62, C("#B27BE0"), C("#6C45B8"));
                Hill(dc, s, 0.74, C("#F59A86"), C("#B3477A"));

                // Glass menu bar
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Rect(0, pad, s, s * 0.075));

                // Glass dock with three app dots
                double dw = plate * 0.62, dh = s * 0.13, dx = (s - dw) / 2, dy = pad + plate - dh - s * 0.07;
                var dock = new RectangleGeometry(new Rect(dx, dy, dw, dh), dh * 0.38, dh * 0.38);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)),
                    new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), Math.Max(1, s * 0.006)), dock);
                var dots = new[] { "#FF5F57", "#FEBC2E", "#28C840" };
                double d = dh * 0.56, gap = (dw - 3 * d) / 4;
                for (int i = 0; i < 3; i++)
                    dc.DrawRoundedRectangle(new SolidColorBrush(C(dots[i])), null,
                        new Rect(dx + gap + i * (d + gap), dy + (dh - d) / 2, d, d), d * 0.28, d * 0.28);

                // Gloss
                var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(80, 255, 255, 255), 0));
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.5));
                dc.DrawRectangle(sheen, null, new Rect(0, 0, s, s));
                dc.Pop();

                var rim = new LinearGradientBrush { StartPoint = new Point(0.2, 0), EndPoint = new Point(0.8, 1) };
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(220, 255, 255, 255), 0));
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(50, 255, 255, 255), 0.55));
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(120, 255, 255, 255), 1));
                dc.DrawGeometry(null, new Pen(rim, Math.Max(1, s * 0.012)), Squircle(pad + s * 0.006, pad + s * 0.006, plate - s * 0.012));
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static void Hill(DrawingContext dc, double s, double y, Color top, Color bottom)
        {
            var fig = new PathFigure { StartPoint = new Point(0, s * (y + 0.04)), IsClosed = true };
            fig.Segments.Add(new BezierSegment(new Point(s * 0.3, s * (y - 0.08)), new Point(s * 0.6, s * (y + 0.1)), new Point(s, s * (y - 0.02)), true));
            fig.Segments.Add(new LineSegment(new Point(s, s), false));
            fig.Segments.Add(new LineSegment(new Point(0, s), false));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            dc.DrawGeometry(new LinearGradientBrush(top, bottom, 90), null, g);
        }

        public static Geometry Squircle(double x, double y, double size)
        {
            const int N = 160;
            const double n = 5;
            double r = size / 2, cx = x + r, cy = y + r;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (int i = 0; i < N; i++)
                {
                    double t = 2 * Math.PI * i / N, c = Math.Cos(t), sn = Math.Sin(t);
                    var p = new Point(cx + r * Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / n), cy + r * Math.Sign(sn) * Math.Pow(Math.Abs(sn), 2 / n));
                    if (i == 0) ctx.BeginFigure(p, true, true); else ctx.LineTo(p, true, true);
                }
            }
            g.Freeze();
            return g;
        }

        /// <summary>Writes a Windows .ico with PNG-compressed 256/64/48/32/16 px images.</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 256, 64, 48, 32, 16 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Render(sizes[i])));
                using (var ms = new MemoryStream()) { enc.Save(ms); pngs[i] = ms.ToArray(); }
            }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
