using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MacLook
{
    /// <summary>
    /// Makes every app icon feel premium: places it on a glossy "squircle" plate with a glass rim
    /// (or clips full-bleed icons into the squircle), and supports per-app custom images.
    /// </summary>
    internal static class IconStyler
    {
        private static readonly string MapFile = Path.Combine(Config.Dir, "custom-icons.txt");
        private static readonly string IconDir = Path.Combine(Config.Dir, "icons");
        private static Dictionary<string, string> custom;
        private static readonly Dictionary<string, BitmapSource> cache = new Dictionary<string, BitmapSource>();

        private static Dictionary<string, string> Custom
        {
            get
            {
                if (custom != null) return custom;
                custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(MapFile))
                        foreach (var line in File.ReadAllLines(MapFile))
                        {
                            int i = line.LastIndexOf('|');
                            if (i > 0) custom[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                        }
                }
                catch (Exception ex) { Program.Log(ex); }
                return custom;
            }
        }

        public static bool HasCustom(string key) { return key != null && Custom.ContainsKey(key); }

        public static void SetCustom(string key, string imagePath)
        {
            Directory.CreateDirectory(IconDir);
            string dest = Path.Combine(IconDir, Guid.NewGuid().ToString("N") + Path.GetExtension(imagePath));
            File.Copy(imagePath, dest, true);
            Custom[key] = dest;
            SaveMap();
            cache.Clear();
        }

        public static void ResetCustom(string key)
        {
            if (Custom.Remove(key)) { SaveMap(); cache.Clear(); }
        }

        public static void ResetAll()
        {
            Custom.Clear();
            SaveMap();
            cache.Clear();
        }

        public static void ClearCache() { cache.Clear(); }

        /// <summary>Let the user pick a PNG/JPG/ICO (e.g. one downloaded from macosicons.com) for an app.</summary>
        public static void PickFor(string key)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose an icon image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.webp|All files|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads"
            };
            if (dlg.ShowDialog() != true) return;
            try { SetCustom(key, dlg.FileName); Program.IconsChanged(); }
            catch (Exception ex) { Program.Log(ex); MessageBox.Show("Couldn't use that image.\n\n" + ex.Message, "Mac Look"); }
        }

        private static void SaveMap()
        {
            Directory.CreateDirectory(Config.Dir);
            File.WriteAllLines(MapFile, Custom.Select(kv => kv.Key + "|" + kv.Value).ToArray());
        }

        /// <summary>The icon to show for a shell target, styled per Settings.</summary>
        public static BitmapSource Get(string key, int rawSize = 256)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string ck = key.ToLowerInvariant() + "|" + Settings.IconStyle + "|" + rawSize;
            BitmapSource result;
            if (cache.TryGetValue(ck, out result)) return result;

            string path;
            if (Custom.TryGetValue(key, out path) && File.Exists(path))
                result = LoadImage(path);
            if (result == null)
            {
                var raw = key.StartsWith("maclook:", StringComparison.OrdinalIgnoreCase) ? BuiltinIcons.Get(key) : IconLoader.Get(key, rawSize);
                result = raw == null ? null : (Settings.IconStyle == "glass" && !key.StartsWith("maclook:", StringComparison.OrdinalIgnoreCase) ? Plate(raw) : raw);
            }
            cache[ck] = result;
            return result;
        }

        private static BitmapSource LoadImage(string path)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = 256;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }

        // ------------------------------------------------------------------ plate rendering
        private const int S = 256;

        public static BitmapSource Plate(BitmapSource raw)
        {
            try
            {
                var src = raw.Format == PixelFormats.Pbgra32 ? raw : new FormatConvertedBitmap(raw, PixelFormats.Pbgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight;
                var px = new int[w * h];
                src.CopyPixels(px, w * 4, 0);

                // Analyse: bounding box, coverage, dominant colour.
                int minX = w, minY = h, maxX = -1, maxY = -1, opaque = 0;
                double sr = 0, sg = 0, sb = 0, sw = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int c = px[y * w + x];
                        int a = (c >> 24) & 0xFF;
                        if (a < 24) continue;
                        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                        if (a > 200) opaque++;
                        double r = ((c >> 16) & 0xFF) * 255.0 / a, g = ((c >> 8) & 0xFF) * 255.0 / a, b = (c & 0xFF) * 255.0 / a;
                        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                        double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                        double wt = 0.05 + sat * sat;
                        sr += r * wt; sg += g * wt; sb += b * wt; sw += wt;
                    }
                if (maxX < 0) return raw;
                double bw = maxX - minX + 1, bh = maxY - minY + 1;
                double coverage = opaque / (bw * bh);
                bool fullBleed = bw >= w * 0.86 && bh >= h * 0.86 && coverage > 0.82;
                var dom = Color.FromRgb((byte)Math.Min(255, sr / sw), (byte)Math.Min(255, sg / sw), (byte)Math.Min(255, sb / sw));

                // macOS icon grid: the squircle fills ~80% of the canvas, sitting slightly high.
                double plate = S * 0.805, ox = (S - plate) / 2, oy = (S - plate) / 2 - S * 0.012;
                var squircle = Squircle(ox, oy, plate);

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    // Soft drop shadow under the plate.
                    var shadowBrush = new RadialGradientBrush(Color.FromArgb(70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
                    dc.DrawEllipse(shadowBrush, null, new Point(S / 2.0, oy + plate * 0.98), plate * 0.48, plate * 0.08);

                    dc.PushClip(squircle);
                    if (fullBleed)
                    {
                        // Icon already is a tile: just clip it into the squircle.
                        var crop = new CroppedBitmap(src, new Int32Rect(minX, minY, (int)bw, (int)bh));
                        dc.DrawImage(crop, new Rect(ox, oy, plate, plate));
                    }
                    else
                    {
                        var top = Mix(dom, Colors.White, 0.86);
                        var bottom = Mix(dom, Colors.White, 0.66);
                        dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(ox, oy, plate, plate));

                        // Fit the original artwork at ~62% of the plate, with its own soft shadow.
                        double fit = plate * 0.62 / Math.Max(bw, bh);
                        double iw = bw * fit, ih = bh * fit;
                        var crop = new CroppedBitmap(src, new Int32Rect(minX, minY, (int)bw, (int)bh));
                        var rect = new Rect(S / 2.0 - iw / 2, oy + plate / 2 - ih / 2, iw, ih);
                        dc.PushOpacity(0.22);
                        dc.DrawImage(Silhouette(crop), new Rect(rect.X, rect.Y + plate * 0.025, iw, ih));
                        dc.Pop();
                        dc.DrawImage(crop, rect);
                    }

                    // Liquid-glass gloss: bright top sheen and a darker base.
                    var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                    sheen.GradientStops.Add(new GradientStop(Color.FromArgb(95, 255, 255, 255), 0));
                    sheen.GradientStops.Add(new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.45));
                    sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55));
                    sheen.GradientStops.Add(new GradientStop(Color.FromArgb(30, 0, 0, 0), 1));
                    dc.DrawRectangle(sheen, null, new Rect(ox, oy, plate, plate));
                    dc.Pop();

                    // Glass rim: light catching the top-left edge, fading toward the bottom.
                    var rim = new LinearGradientBrush { StartPoint = new Point(0.2, 0), EndPoint = new Point(0.8, 1) };
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(230, 255, 255, 255), 0));
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(60, 255, 255, 255), 0.5));
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(140, 255, 255, 255), 1));
                    dc.DrawGeometry(null, new Pen(rim, 2.2), Squircle(ox + 1.1, oy + 1.1, plate - 2.2));
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 1), squircle);
                }
                var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex) { Program.Log(ex); return raw; }
        }

        private static BitmapSource Silhouette(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var px = new int[w * h];
            src.CopyPixels(px, w * 4, 0);
            for (int i = 0; i < px.Length; i++) px[i] = px[i] & unchecked((int)0xFF000000);
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            bs.Freeze();
            return bs;
        }

        private static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Superellipse (n=5) — the continuous-curvature "squircle" shape.</summary>
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
                    double t = 2 * Math.PI * i / N;
                    double c = Math.Cos(t), s = Math.Sin(t);
                    double px = cx + r * Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / n);
                    double py = cy + r * Math.Sign(s) * Math.Pow(Math.Abs(s), 2 / n);
                    if (i == 0) ctx.BeginFigure(new Point(px, py), true, true);
                    else ctx.LineTo(new Point(px, py), true, true);
                }
            }
            g.Freeze();
            return g;
        }
    }
}
