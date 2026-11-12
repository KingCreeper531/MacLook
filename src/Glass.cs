using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>A block of screen pixels (0xAARRGGBB ints) positioned in screen coordinates.</summary>
    internal class Pix
    {
        public int X, Y, W, H;
        public int[] D;
    }

    internal class GlassStyle
    {
        public int Blur;            // frost radius in px
        public double Bezel;        // width of the curved edge in px
        public double Refraction;   // how far the edge bends what's behind it, in px
        public uint Tint;           // 0xRRGGBB
        public double TintAmount;
        public double Saturation = 1.4;
        public double Highlight = 0.8;
    }

    internal static class GlassStyles
    {
        private static uint T { get { return Theme.Dark ? 0x1C1C1Eu : 0xFFFFFFu; } }
        private static uint P { get { return Theme.Dark ? 0x1E1E20u : 0xF6F6F8u; } }

        /// <summary>Applies the user's Frost / Tint / Refraction / Shine multipliers from Settings.</summary>
        private static GlassStyle Adj(GlassStyle s)
        {
            s.Blur = (int)Math.Round(s.Blur * Settings.GlassFrost);
            s.TintAmount = Math.Max(0, Math.Min(0.95, s.TintAmount * Settings.GlassTint));
            s.Refraction *= Settings.GlassRefraction;
            s.Highlight *= Settings.GlassShine;
            return s;
        }

        public static GlassStyle Dock { get { return Adj(new GlassStyle { Blur = 2, Bezel = 18, Refraction = 16, Tint = T, TintAmount = Theme.Dark ? 0.20 : 0.16, Saturation = 1.5, Highlight = 0.9 }); } }
        public static GlassStyle MenuBar { get { return Adj(new GlassStyle { Blur = 3, Bezel = 0, Refraction = 0, Tint = T, TintAmount = Theme.Dark ? 0.28 : 0.24, Saturation = 1.35, Highlight = 0.35 }); } }
        public static GlassStyle Menu { get { return Adj(new GlassStyle { Blur = 14, Bezel = 10, Refraction = 9, Tint = P, TintAmount = 0.62, Saturation = 1.7, Highlight = 0.75 }); } }
        public static GlassStyle Panel { get { return Adj(new GlassStyle { Blur = 18, Bezel = 16, Refraction = 12, Tint = P, TintAmount = 0.48, Saturation = 1.7, Highlight = 0.85 }); } }
        public static GlassStyle Window { get { return Adj(new GlassStyle { Blur = 22, Bezel = 14, Refraction = 10, Tint = P, TintAmount = 0.72, Saturation = 1.6, Highlight = 0.7 }); } }
    }

    internal static class ScreenCapture
    {
        public static Pix Capture()
        {
            try
            {
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                    var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var px = new int[b.Width * b.Height];
                    Marshal.Copy(data.Scan0, px, 0, px.Length);
                    bmp.UnlockBits(data);
                    return new Pix { X = b.Left, Y = b.Top, W = b.Width, H = b.Height, D = px };
                }
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }

        public static Pix CaptureRegion(int x, int y, int w, int h)
        {
            try
            {
                using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
                    var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var px = new int[w * h];
                    Marshal.Copy(data.Scan0, px, 0, px.Length);
                    bmp.UnlockBits(data);
                    return new Pix { X = x, Y = y, W = w, H = h, D = px };
                }
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }
    }

    /// <summary>
    /// CPU "liquid glass": frosts what is behind a surface, bends it through a curved bezel
    /// (with a little chromatic dispersion), saturates it, tints it and adds specular rim light.
    /// </summary>
    internal static class GlassFx
    {
        private const double Lx = -0.5, Ly = -0.866;   // light comes from the top-left

        public static void Apply(Border b, Pix snap, GlassStyle s)
        {
            if (snap == null || b == null || b.ActualWidth < 2 || b.ActualHeight < 2) return;
            var src = PresentationSource.FromVisual(b);
            if (src == null || src.CompositionTarget == null) return;
            var m = src.CompositionTarget.TransformToDevice;
            Point tl;
            try { tl = b.PointToScreen(new Point(0, 0)); } catch { return; }
            int x = (int)Math.Round(tl.X), y = (int)Math.Round(tl.Y);
            int w = (int)Math.Round(b.ActualWidth * m.M11), h = (int)Math.Round(b.ActualHeight * m.M22);
            double radius = b.CornerRadius.TopLeft * m.M11;
            var bmp = Render(snap, x, y, w, h, radius, s);
            if (bmp == null) return;
            var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
            brush.Freeze();
            b.Background = brush;
        }

        public static BitmapSource Render(Pix snap, int x, int y, int w, int h, double radius, GlassStyle s)
        {
            if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return null;
            int br = Math.Max(1, s.Blur / 2);
            int m = s.Blur > 0 ? br * 3 + 2 : 1;
            int ew = w + 2 * m, eh = h + 2 * m;
            var reg = new int[ew * eh];
            for (int j = 0; j < eh; j++)
            {
                int sy = Clamp(y - m + j - snap.Y, 0, snap.H - 1) * snap.W;
                int row = j * ew;
                for (int i = 0; i < ew; i++)
                    reg[row + i] = snap.D[sy + Clamp(x - m + i - snap.X, 0, snap.W - 1)];
            }
            if (s.Blur > 0) { var tmp = new int[reg.Length]; for (int p = 0; p < 3; p++) { BlurH(reg, tmp, ew, eh, br); BlurV(tmp, reg, ew, eh, br); } }

            var outPx = new int[w * h];
            double r = Math.Min(radius, Math.Min(w, h) / 2.0);
            double hw = w / 2.0, hh = h / 2.0, bx = hw - r, by = hh - r;
            double tr = (s.Tint >> 16) & 0xFF, tg = (s.Tint >> 8) & 0xFF, tb = s.Tint & 0xFF, ta = s.TintAmount;
            double sat = s.Saturation, hi = s.Highlight * 255;

            for (int j = 0; j < h; j++)
            {
                double py = j + 0.5 - hh, ay = Math.Abs(py), qy = ay - by;
                for (int i = 0; i < w; i++)
                {
                    double px = i + 0.5 - hw, ax = Math.Abs(px), qx = ax - bx;
                    double d, nx, ny;
                    if (qx > 0 && qy > 0)
                    {
                        double len = Math.Sqrt(qx * qx + qy * qy);
                        d = len - r; nx = Math.Sign(px) * qx / len; ny = Math.Sign(py) * qy / len;
                    }
                    else if (qx > qy) { d = qx - r; nx = Math.Sign(px); ny = 0; }
                    else { d = qy - r; nx = 0; ny = Math.Sign(py); }

                    double t = -d;                     // depth inside the shape
                    if (t <= -0.5) continue;
                    double cov = t + 0.5; if (cov > 1) cov = 1;

                    double cr, cg, cb, k = 0;
                    double sx = i + m, sy = j + m;
                    if (s.Bezel > 0 && t < s.Bezel)
                    {
                        k = 1 - Math.Max(t, 0) / s.Bezel;
                        double disp = s.Refraction * (1 - Math.Sqrt(1 - k * k));
                        cr = Sample(reg, ew, eh, sx - nx * disp, sy - ny * disp, 16);
                        cg = Sample(reg, ew, eh, sx - nx * disp * 1.05, sy - ny * disp * 1.05, 8);
                        cb = Sample(reg, ew, eh, sx - nx * disp * 1.10, sy - ny * disp * 1.10, 0);
                    }
                    else
                    {
                        int c = reg[(j + m) * ew + i + m];
                        cr = (c >> 16) & 0xFF; cg = (c >> 8) & 0xFF; cb = c & 0xFF;
                    }

                    double l = 0.2126 * cr + 0.7152 * cg + 0.0722 * cb;
                    cr = l + (cr - l) * sat; cg = l + (cg - l) * sat; cb = l + (cb - l) * sat;
                    cr = cr * (1 - ta) + tr * ta; cg = cg * (1 - ta) + tg * ta; cb = cb * (1 - ta) + tb * ta;

                    double dl = nx * Lx + ny * Ly;
                    double spec = (dl > 0 ? dl * dl : 0) + (dl < 0 ? 0.55 * dl * dl : 0);
                    double rim = Math.Exp(-Math.Max(t, 0) / 1.3);
                    double add = hi * (spec * rim * 0.9 + 0.12 * k * k * k);
                    cr += add; cg += add; cb += add;

                    int a = (int)(cov * 255);
                    outPx[j * w + i] = (a << 24) | (C8(cr * cov) << 16) | (C8(cg * cov) << 8) | C8(cb * cov);
                }
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, outPx, w * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Heavily frosted, darkened full-screen backdrop (Launchpad, Mission Control).</summary>
        public static BitmapSource Backdrop(Pix snap, int down, int blur, double darken)
        {
            if (snap == null) return null;
            int w = snap.W / down, h = snap.H / down;
            var a = new int[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int v = 0; v < down; v++)
                    {
                        int row = (j * down + v) * snap.W + i * down;
                        for (int u = 0; u < down; u++) { int c = snap.D[row + u]; r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; }
                    }
                    int n = down * down;
                    a[j * w + i] = (r / n << 16) | (g / n << 8) | (b / n);
                }
            var tmp = new int[a.Length];
            for (int p = 0; p < 3; p++) { BlurH(a, tmp, w, h, blur); BlurV(tmp, a, w, h, blur); }
            for (int k = 0; k < a.Length; k++)
            {
                int c = a[k];
                double r = ((c >> 16) & 0xFF), g = ((c >> 8) & 0xFF), b = (c & 0xFF);
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = (l + (r - l) * 1.4) * (1 - darken); g = (l + (g - l) * 1.4) * (1 - darken); b = (l + (b - l) * 1.4) * (1 - darken);
                a[k] = unchecked((int)0xFF000000) | (C8(r) << 16) | (C8(g) << 8) | C8(b);
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, a, w * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Snapshot the screen before a context menu opens, then paint its glass once it has a position.</summary>
        public static void PrepareMenu(ContextMenu menu)
        {
            var snap = ScreenCapture.Capture();
            if (snap == null) return;
            menu.Opened += (s, e) => menu.Dispatcher.BeginInvoke(new Action(() =>
            {
                var bd = menu.Template == null ? null : menu.Template.FindName("GlassBd", menu) as Border;
                if (bd != null) Apply(bd, snap, GlassStyles.Menu);
            }), DispatcherPriority.Loaded);
        }

        private static double Sample(int[] a, int w, int h, double fx, double fy, int shift)
        {
            fx -= 0.5; fy -= 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double ux = fx - x0, uy = fy - y0;
            int xa = Clamp(x0, 0, w - 1), xb = Clamp(x0 + 1, 0, w - 1), ya = Clamp(y0, 0, h - 1) * w, yb = Clamp(y0 + 1, 0, h - 1) * w;
            double c00 = (a[ya + xa] >> shift) & 0xFF, c10 = (a[ya + xb] >> shift) & 0xFF;
            double c01 = (a[yb + xa] >> shift) & 0xFF, c11 = (a[yb + xb] >> shift) & 0xFF;
            return (c00 * (1 - ux) + c10 * ux) * (1 - uy) + (c01 * (1 - ux) + c11 * ux) * uy;
        }

        private static void BlurH(int[] src, int[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, sr = 0, sg = 0, sb = 0;
                for (int i = -r; i <= r; i++) { int c = src[row + Clamp(i, 0, w - 1)]; sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF; }
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = (sr / div << 16) | (sg / div << 8) | (sb / div);
                    int o = src[row + Clamp(x - r, 0, w - 1)], n = src[row + Clamp(x + r + 1, 0, w - 1)];
                    sr += ((n >> 16) & 0xFF) - ((o >> 16) & 0xFF); sg += ((n >> 8) & 0xFF) - ((o >> 8) & 0xFF); sb += (n & 0xFF) - (o & 0xFF);
                }
            }
        }

        private static void BlurV(int[] src, int[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1;
            for (int x = 0; x < w; x++)
            {
                int sr = 0, sg = 0, sb = 0;
                for (int i = -r; i <= r; i++) { int c = src[Clamp(i, 0, h - 1) * w + x]; sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF; }
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = (sr / div << 16) | (sg / div << 8) | (sb / div);
                    int o = src[Clamp(y - r, 0, h - 1) * w + x], n = src[Clamp(y + r + 1, 0, h - 1) * w + x];
                    sr += ((n >> 16) & 0xFF) - ((o >> 16) & 0xFF); sg += ((n >> 8) & 0xFF) - ((o >> 8) & 0xFF); sb += (n & 0xFF) - (o & 0xFF);
                }
            }
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        private static int C8(double v) { return v <= 0 ? 0 : v >= 255 ? 255 : (int)v; }
    }

    /// <summary>Original artwork for Mac Look's own dock items.</summary>
    internal static class BuiltinIcons
    {
        public static BitmapSource Get(string target)
        {
            string t = target.ToLowerInvariant();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var rect = new Rect(8, 8, 112, 112);
                if (t == "maclook:launchpad")
                {
                    dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x3D, 0x4A), Color.FromRgb(0x17, 0x18, 0x20), 90), null, rect, 27, 27);
                    var colors = new[] { "#FF5F57", "#FEBC2E", "#28C840", "#3B82F6", "#A855F7", "#EC4899", "#14B8A6", "#F97316", "#EAB308" };
                    for (int i = 0; i < 9; i++)
                    {
                        var c = (Color)ColorConverter.ConvertFromString(colors[i]);
                        dc.DrawRoundedRectangle(new SolidColorBrush(c), null, new Rect(26 + (i % 3) * 27, 26 + (i / 3) * 27, 20, 20), 6, 6);
                    }
                }
                else
                {
                    dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(0x5B, 0x8D, 0xF6), Color.FromRgb(0x2B, 0x4B, 0xC9), 90), null, rect, 27, 27);
                    var white = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
                    var soft = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
                    dc.DrawRoundedRectangle(soft, null, new Rect(24, 30, 36, 26), 5, 5);
                    dc.DrawRoundedRectangle(soft, null, new Rect(68, 30, 36, 26), 5, 5);
                    dc.DrawRoundedRectangle(white, null, new Rect(34, 64, 60, 38), 6, 6);
                }
            }
            var rtb = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
