using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacLook
{
    /// <summary>What the screen saver draws behind its clock. Built once, then moved every frame.</summary>
    internal abstract class SaverScene
    {
        public readonly Canvas Root = new Canvas();
        protected readonly double W, H;

        protected SaverScene(double w, double h)
        {
            W = w; H = h;
            Root.Width = w; Root.Height = h;
            Root.ClipToBounds = true;
            Root.IsHitTestVisible = false;
        }

        /// <summary>sim: scene time in seconds (slows to a stop on exit); now: real seconds since shown;
        /// settle: 0..1, how far the scene has settled into its final, slightly punchier frame.</summary>
        public abstract void Frame(double sim, double now, double settle);

        public virtual void SetLighting(string lighting, double now) { }

        /// <summary>False when the scene draws its own clock (Retro).</summary>
        public virtual bool WantsClock { get { return true; } }

        protected static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
        protected static double Smoothstep(double v) { v = Clamp01(v); return v * v * (3 - 2 * v); }

        protected static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    /// <summary>One of the four times of day. Everything the aerial scene paints comes from here,
    /// so blending two palettes cross-fades the whole landscape.</summary>
    internal sealed class SkyPalette
    {
        public Color SkyTop, SkyMid, SkyHorizon, OrbCore, OrbHalo, HorizonGlow, Haze, RidgeNear, RidgeLit, Mist, Cloud, WaterTop, WaterDeep;
        public double OrbX, OrbY, OrbR, HaloR, Stars, MistAlpha;

        private static Color C(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        /// <summary>"auto" picks by the local clock: 5-10 morning, 10-17 day, 17-21 evening, else night.</summary>
        public static string Resolve(string lighting)
        {
            if (lighting == "morning" || lighting == "day" || lighting == "evening" || lighting == "night") return lighting;
            int h = DateTime.Now.Hour;
            if (h >= 5 && h < 10) return "morning";
            if (h >= 10 && h < 17) return "day";
            if (h >= 17 && h < 21) return "evening";
            return "night";
        }

        public static SkyPalette For(string name)
        {
            var p = new SkyPalette();
            switch (name)
            {
                case "morning":   // cool pink and peach dawn, low sun, lots of mist
                    p.SkyTop = C(0xFF3B4778); p.SkyMid = C(0xFFB088A6); p.SkyHorizon = C(0xFFFFCBA6);
                    p.OrbCore = C(0xFFFFF4DC); p.OrbHalo = C(0xD9FFC49A); p.HorizonGlow = C(0x88FFB892);
                    p.Haze = C(0xFFE4B7B0); p.RidgeNear = C(0xFF2C2D4A); p.RidgeLit = C(0xFF6E5A78); p.Mist = C(0xFFF2CBC0);
                    p.Cloud = C(0xCCFFE0D4); p.WaterTop = C(0xFFF1C2AA); p.WaterDeep = C(0xFF515A86);
                    p.OrbX = 0.30; p.OrbY = 0.47; p.OrbR = 0.036; p.HaloR = 0.48; p.Stars = 0; p.MistAlpha = 0.62;
                    break;
                case "evening":   // amber and purple sunset with a long glow
                    p.SkyTop = C(0xFF2A1D50); p.SkyMid = C(0xFF9E4470); p.SkyHorizon = C(0xFFFFA14E);
                    p.OrbCore = C(0xFFFFE6A8); p.OrbHalo = C(0xF2FF9A50); p.HorizonGlow = C(0x99FF9A48);
                    p.Haze = C(0xFFD48469); p.RidgeNear = C(0xFF29172F); p.RidgeLit = C(0xFF6C3445); p.Mist = C(0xFFE6976E);
                    p.Cloud = C(0xBFFFAF78); p.WaterTop = C(0xFFF39A5A); p.WaterDeep = C(0xFF3A2149);
                    p.OrbX = 0.64; p.OrbY = 0.50; p.OrbR = 0.042; p.HaloR = 0.55; p.Stars = 0.12; p.MistAlpha = 0.42;
                    break;
                case "night":     // deep navy, stars and the moon
                    p.SkyTop = C(0xFF040817); p.SkyMid = C(0xFF0C1736); p.SkyHorizon = C(0xFF1F315E);
                    p.OrbCore = C(0xFFF1F3FF); p.OrbHalo = C(0x809FB4FF); p.HorizonGlow = C(0x404C66AD);
                    p.Haze = C(0xFF22335C); p.RidgeNear = C(0xFF060A17); p.RidgeLit = C(0xFF1B2748); p.Mist = C(0xFF2B3C68);
                    p.Cloud = C(0x408C9CC8); p.WaterTop = C(0xFF23365F); p.WaterDeep = C(0xFF050A19);
                    p.OrbX = 0.76; p.OrbY = 0.17; p.OrbR = 0.030; p.HaloR = 0.30; p.Stars = 1; p.MistAlpha = 0.30;
                    break;
                default:          // day: bright blue, white clouds
                    p.SkyTop = C(0xFF2A6CCF); p.SkyMid = C(0xFF6EA7E8); p.SkyHorizon = C(0xFFD2E8F8);
                    p.OrbCore = C(0xFFFFFFFF); p.OrbHalo = C(0xCCFFF8E0); p.HorizonGlow = C(0x40FFFFFF);
                    p.Haze = C(0xFFA8C6DE); p.RidgeNear = C(0xFF2D473A); p.RidgeLit = C(0xFF61815F); p.Mist = C(0xFFE4EFF6);
                    p.Cloud = C(0xEEFFFFFF); p.WaterTop = C(0xFF9DC7E7); p.WaterDeep = C(0xFF2B5D8D);
                    p.OrbX = 0.73; p.OrbY = 0.15; p.OrbR = 0.030; p.HaloR = 0.36; p.Stars = 0; p.MistAlpha = 0.34;
                    break;
            }
            return p;
        }

        public static Color Mix(Color a, Color b, double k)
        {
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * k + 0.5), (byte)(a.R + (b.R - a.R) * k + 0.5),
                (byte)(a.G + (b.G - a.G) * k + 0.5), (byte)(a.B + (b.B - a.B) * k + 0.5));
        }

        private static double Mix(double a, double b, double k) { return a + (b - a) * k; }

        /// <summary>Writes the blend of a and b into this palette (reused every frame: no garbage).</summary>
        public void Blend(SkyPalette a, SkyPalette b, double k)
        {
            SkyTop = Mix(a.SkyTop, b.SkyTop, k); SkyMid = Mix(a.SkyMid, b.SkyMid, k); SkyHorizon = Mix(a.SkyHorizon, b.SkyHorizon, k);
            OrbCore = Mix(a.OrbCore, b.OrbCore, k); OrbHalo = Mix(a.OrbHalo, b.OrbHalo, k); HorizonGlow = Mix(a.HorizonGlow, b.HorizonGlow, k);
            Haze = Mix(a.Haze, b.Haze, k); RidgeNear = Mix(a.RidgeNear, b.RidgeNear, k); RidgeLit = Mix(a.RidgeLit, b.RidgeLit, k);
            Mist = Mix(a.Mist, b.Mist, k); Cloud = Mix(a.Cloud, b.Cloud, k); WaterTop = Mix(a.WaterTop, b.WaterTop, k); WaterDeep = Mix(a.WaterDeep, b.WaterDeep, k);
            OrbX = Mix(a.OrbX, b.OrbX, k); OrbY = Mix(a.OrbY, b.OrbY, k); OrbR = Mix(a.OrbR, b.OrbR, k); HaloR = Mix(a.HaloR, b.HaloR, k);
            Stars = Mix(a.Stars, b.Stars, k); MistAlpha = Mix(a.MistAlpha, b.MistAlpha, k);
        }

        /// <summary>A touch more saturation and contrast (k = 0..1): the still frame the exit settles on.</summary>
        public void Punch(double k)
        {
            if (k <= 0) return;
            double sat = 1 + 0.22 * k, con = 1 + 0.14 * k;
            SkyTop = P(SkyTop, sat, con); SkyMid = P(SkyMid, sat, con); SkyHorizon = P(SkyHorizon, sat, con);
            OrbHalo = P(OrbHalo, sat, con); HorizonGlow = P(HorizonGlow, sat, con);
            Haze = P(Haze, sat, con); RidgeNear = P(RidgeNear, sat, con); RidgeLit = P(RidgeLit, sat, con);
            Mist = P(Mist, sat, con); Cloud = P(Cloud, sat, con); WaterTop = P(WaterTop, sat, con); WaterDeep = P(WaterDeep, sat, con);
        }

        private static Color P(Color c, double sat, double con)
        {
            double l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            return Color.FromArgb(c.A, Ch(c.R, l, sat, con), Ch(c.G, l, sat, con), Ch(c.B, l, sat, con));
        }

        private static byte Ch(double v, double l, double sat, double con)
        {
            v = l + (v - l) * sat;
            v = 128 + (v - 128) * con;
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5);
        }
    }

    /// <summary>
    /// A slow aerial flight over mountain ranges and a lake: ridge silhouettes at real depths come
    /// towards the camera (parallax by perspective), and each one that passes beneath is reborn far away
    /// with a new shape. Everything is vector shapes and gradients moved by transforms, so the GPU does
    /// the work; the CPU only updates a few dozen numbers per frame.
    /// </summary>
    internal sealed class AerialScene : SaverScene
    {
        private const int RidgeCount = 7;
        private const double ZNear = 0.5, ZFar = 9.5;          // world units in front of the camera
        private const double Speed = 0.16;                     // forward glide, world units per second
        private const double CamHeight = 1.45;                 // above the valley floor; peaks stay below it
        private const double HorizonFrac = 0.56;
        private const double Overscan = 1.05;                  // room for the gentle banking
        private const double CrossFadeSec = 2.6;
        private const double RidgeBottom = 0.4;                // world units below the valley floor
        private const double PuffNearZ = 1.4, PuffFadeZ = 1.2;  // close up, a mist puff would cover the screen: fade it out

        private class Ridge
        {
            public double Z;
            public Canvas Layer;
            public Path Shape;
            public MatrixTransform Place;
            public GradientStop Lit, Body, Foot, Fade;
            public bool PuffsShown = true;
            public GradientStop MistIn, MistOut;
            public Ellipse[] Puffs;
            public TranslateTransform[] PuffMove;
            public double[] PuffX, PuffSpeed;
        }

        private class Cloud
        {
            public Canvas Shape;
            public TranslateTransform Move;
            public double X, Y, Width, Drift, Parallax, Span;
        }

        private readonly Random rnd;
        private readonly double xSpan;          // ridges reach this far left/right (world units)
        private readonly double horizonY;
        private readonly Canvas world = new Canvas();
        private readonly RotateTransform bank;
        private readonly GradientStop skyTop, skyMid, skyHorizon, skyBottom;
        private readonly Path[] stars = new Path[3];
        private readonly double[] starPhase = { 0, 2.1, 4.2 }, starRate = { 0.9, 1.3, 0.7 };
        private readonly MatrixTransform haloPlace, corePlace, glowPlace, reflectPlace;
        private readonly GradientStop haloIn, haloNear, haloMid, haloOut, coreIn, coreMid, coreOut, glowIn, glowOut, reflectIn, reflectOut;
        private readonly Path shimmer;
        private readonly TranslateTransform shimmerMove;
        private readonly SolidColorBrush shimmerFill = new SolidColorBrush();
        private readonly GradientStop cloudIn, cloudMid, cloudOut;
        private readonly List<Cloud> clouds = new List<Cloud>();
        private readonly GradientStop waterTop, waterDeep;
        private readonly SolidColorBrush farFill = new SolidColorBrush(), farReflectFill = new SolidColorBrush();
        private readonly TranslateTransform farMove, farReflectMove;
        private readonly Canvas ridgeHost = new Canvas();
        private readonly List<Ridge> ridges = new List<Ridge>();

        private SkyPalette from, to;
        private readonly SkyPalette cur = new SkyPalette();
        private string target;
        private double fadeStart = -100;
        private double lastSim;

        public AerialScene(double w, double h, int seed, string lighting) : base(w, h)
        {
            rnd = new Random(seed);
            horizonY = h * HorizonFrac;
            // Wide enough to cover the view at the far distance on any aspect ratio, plus the side drift.
            xSpan = Math.Max(14, w / h * ZFar * 0.5 * Overscan + 3);

            target = SkyPalette.Resolve(lighting);
            from = SkyPalette.For(target);
            to = from;

            bank = new RotateTransform(0, w / 2, h / 2);
            var tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform(Overscan, Overscan, w / 2, h / 2));
            tg.Children.Add(bank);
            world.RenderTransform = tg;
            world.Width = w; world.Height = h;
            Root.Background = Brushes.Black;
            Root.Children.Add(world);

            // Sky.
            var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            skyTop = Stop(sky, 0); skyMid = Stop(sky, HorizonFrac * 0.55); skyHorizon = Stop(sky, HorizonFrac); skyBottom = Stop(sky, 1);
            Add(new Rectangle { Width = w, Height = h, Fill = sky });

            // Stars: three groups that twinkle out of step.
            for (int g = 0; g < stars.Length; g++)
            {
                var gg = new GeometryGroup();
                for (int i = 0; i < 70; i++)
                {
                    double x = rnd.NextDouble() * w;
                    double y = Math.Pow(rnd.NextDouble(), 1.5) * horizonY * 0.92;
                    double r = 0.5 + Math.Pow(rnd.NextDouble(), 3) * 1.3;
                    gg.Children.Add(new EllipseGeometry(new Point(x, y), r, r));
                }
                gg.Freeze();
                stars[g] = new Path { Data = gg, Fill = Brushes.White, Opacity = 0 };
                Add(stars[g]);
            }

            // Sun / moon: a wide halo, the disc, and the glow it spreads along the horizon.
            var halo = UnitDisc(out haloPlace);
            var hb = Radial();
            haloIn = Stop(hb, 0); haloNear = Stop(hb, 0.07); haloMid = Stop(hb, 0.36); haloOut = Stop(hb, 1);
            halo.Fill = hb;
            var glow = UnitDisc(out glowPlace);
            var gb = Radial();
            glowIn = Stop(gb, 0); glowOut = Stop(gb, 1);
            glow.Fill = gb;
            var core = UnitDisc(out corePlace);
            var cb = Radial();
            coreIn = Stop(cb, 0); coreMid = Stop(cb, 0.86); coreOut = Stop(cb, 1);
            core.Fill = cb;
            Add(glow); Add(halo); Add(core);

            // High clouds: bigger and faster the higher (closer) they are.
            var cloudBrush = Radial();
            cloudIn = Stop(cloudBrush, 0); cloudMid = Stop(cloudBrush, 0.5); cloudOut = Stop(cloudBrush, 1);
            for (int i = 0; i < 9; i++) AddCloud(cloudBrush, i / 9.0);

            // The lake: reflects the sky, the sun's glow and the distant range.
            var water = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            waterTop = Stop(water, 0); waterDeep = Stop(water, 1);
            var lake = new Rectangle { Width = w, Height = h - horizonY + 2, Fill = water };
            Canvas.SetTop(lake, horizonY - 1);
            Add(lake);

            var reflect = UnitDisc(out reflectPlace);
            var rb = Radial();
            reflectIn = Stop(rb, 0); reflectOut = Stop(rb, 1);
            reflect.Fill = rb;
            Add(reflect);

            var lines = new GeometryGroup();
            for (int i = 0; i < 16; i++)
            {
                double y = horizonY + 3 + Math.Pow(i / 15.0, 1.4) * h * 0.2;
                double lw = w * (0.008 + rnd.NextDouble() * 0.03) * (1 + i * 0.1);
                double x = (rnd.NextDouble() - 0.5) * w * 0.04 - lw / 2;
                double lh = 0.8 + i * 0.1;
                lines.Children.Add(new RectangleGeometry(new Rect(x, y, lw, lh), lh / 2, lh / 2));
            }
            lines.Freeze();
            shimmerMove = new TranslateTransform();
            shimmer = new Path { Data = lines, Fill = shimmerFill, RenderTransform = shimmerMove };
            Add(shimmer);

            // The far range on the horizon, and its soft upside-down reflection in the lake.
            var far = FarRange();
            farMove = new TranslateTransform();
            var farReflect = new Path { Data = far, Fill = farReflectFill, Opacity = 0.35 };
            var flip = new TransformGroup();
            flip.Children.Add(new ScaleTransform(1, -0.6, 0, horizonY));
            farReflectMove = new TranslateTransform();
            flip.Children.Add(farReflectMove);
            farReflect.RenderTransform = flip;
            Add(farReflect);
            Add(new Path { Data = far, Fill = farFill, RenderTransform = farMove });

            // Ridges, farthest first.
            world.Children.Add(ridgeHost);
            double gap = (ZFar - ZNear) / RidgeCount;
            for (int i = RidgeCount - 1; i >= 0; i--)
            {
                var r = NewRidge();
                r.Z = ZNear + (i + 0.7) * gap;
                ridges.Add(r);
                ridgeHost.Children.Add(r.Layer);
            }
        }

        private void Add(UIElement e) { world.Children.Add(e); }

        private static GradientStop Stop(GradientBrush b, double offset)
        {
            var s = new GradientStop(Colors.Transparent, offset);
            b.GradientStops.Add(s);
            return s;
        }

        private static RadialGradientBrush Radial()
        {
            return new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
        }

        /// <summary>A 1x1 disc; Place() gives it its size and centre.</summary>
        private static Ellipse UnitDisc(out MatrixTransform place)
        {
            place = new MatrixTransform();
            return new Ellipse { Width = 1, Height = 1, RenderTransform = place };
        }

        private static void Place(MatrixTransform m, double cx, double cy, double w, double h)
        {
            m.Matrix = new Matrix(w, 0, 0, h, cx - w / 2, cy - h / 2);
        }

        private void AddCloud(Brush brush, double k)
        {
            var c = new Cloud();
            double high = rnd.NextDouble();                          // 0 = near the horizon, 1 = high above
            double width = W * (0.14 + 0.22 * high) * (0.8 + rnd.NextDouble() * 0.4);
            double height = width * (0.22 + rnd.NextDouble() * 0.08);
            c.Shape = new Canvas();
            // Puffs sit on a roughly flat base, bigger towards the middle: a cumulus, not a blob.
            int puffs = 6 + rnd.Next(4);
            for (int i = 0; i < puffs; i++)
            {
                double along = (i + 0.5) / puffs;
                double big = 0.45 + 0.55 * Math.Sin(along * Math.PI);
                double pw = width * (0.22 + rnd.NextDouble() * 0.18) * (0.7 + big * 0.5);
                double ph = height * (0.45 + 0.6 * big) * (0.85 + rnd.NextDouble() * 0.3);
                var e = new Ellipse { Width = pw, Height = ph, Fill = brush };
                Canvas.SetLeft(e, along * width - pw / 2 + (rnd.NextDouble() - 0.5) * width * 0.08);
                Canvas.SetTop(e, height - ph * (0.85 + rnd.NextDouble() * 0.15));
                c.Shape.Children.Add(e);
            }
            c.Y = horizonY * (0.62 - 0.55 * high) - height / 2;
            c.Width = width;
            c.Span = W * Overscan + width;
            c.X = k * c.Span + rnd.NextDouble() * W * 0.08;
            c.Drift = W * (0.004 + 0.010 * high);
            c.Parallax = W * (0.006 + 0.03 * high);
            c.Move = new TranslateTransform();
            c.Shape.RenderTransform = c.Move;
            c.Shape.Opacity = 0.55 + 0.45 * high;
            // A cloud only moves: draw it once into a soft bitmap and slide that (redrawn only while
            // the lighting cross-fades). Tessellating dozens of gradient puffs every frame is costly.
            c.Shape.CacheMode = new BitmapCache(0.75);
            clouds.Add(c);
            Add(c.Shape);
        }

        /// <summary>Smooth random values on a looping lattice (1D value noise).</summary>
        private sealed class Noise
        {
            private readonly double[] v;
            public Noise(Random r, int n) { v = new double[n]; for (int i = 0; i < n; i++) v[i] = r.NextDouble(); }
            public double At(double x)
            {
                double f = Math.Floor(x);
                int i = (int)f;
                double t = x - f;
                t = t * t * (3 - 2 * t);
                int n = v.Length;
                int a = ((i % n) + n) % n, b = (a + 1) % n;
                return v[a] + (v[b] - v[a]) * t;
            }
        }

        /// <summary>Mountain height (0..1) along a ridge: sharp ridged peaks plus finer detail.</summary>
        private static Func<double, double> Mountains(Random r, double scale)
        {
            var n1 = new Noise(r, 64); var n2 = new Noise(r, 64); var n3 = new Noise(r, 64); var n4 = new Noise(r, 64);
            double o1 = r.NextDouble() * 50, o2 = r.NextDouble() * 50;
            return x =>
            {
                double p = 1 - Math.Abs(n1.At(x * 0.32 * scale + o1) * 2 - 1);   // ridged: peaks come to a point
                p = p * p;
                double v = 0.62 * p + 0.25 * n2.At(x * 1.05 * scale + o2) + 0.09 * n3.At(x * 3.1 * scale) + 0.04 * n4.At(x * 8.3 * scale);
                return v < 0 ? 0 : v > 1 ? 1 : v;
            };
        }

        private Geometry FarRange()
        {
            var m = Mountains(rnd, 1);
            var g = new StreamGeometry();
            double x0 = -W * 0.15, x1 = W * 1.15, step = W / 300;
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x0, horizonY + 2), true, true);
                var pts = new List<Point>();
                for (double x = x0; x <= x1; x += step)
                    pts.Add(new Point(x, horizonY + 1 - H * (0.012 + 0.055 * m(x / W * 9))));
                pts.Add(new Point(x1, horizonY + 2));
                ctx.PolyLineTo(pts, true, true);
            }
            g.Freeze();
            return g;
        }

        private Ridge NewRidge()
        {
            var r = new Ridge();
            r.Place = new MatrixTransform();
            r.Layer = new Canvas { RenderTransform = r.Place };
            // Peaks lit, body darker, and the foot dissolving into the valley mist. Ending at the foot (not
            // the bottom of the screen) keeps the overdraw of the far ridges small.
            var fill = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, -1.2), EndPoint = new Point(0, RidgeBottom) };
            r.Lit = Stop(fill, 0); r.Body = Stop(fill, 0.48); r.Foot = Stop(fill, 0.78); r.Fade = Stop(fill, 1);
            r.Shape = new Path { Fill = fill };
            r.Layer.Children.Add(r.Shape);

            var mist = Radial();
            r.MistIn = Stop(mist, 0); r.MistOut = Stop(mist, 1);
            r.Puffs = new Ellipse[2]; r.PuffMove = new TranslateTransform[2]; r.PuffX = new double[2]; r.PuffSpeed = new double[2];
            for (int i = 0; i < 2; i++)
            {
                r.PuffMove[i] = new TranslateTransform();
                r.Puffs[i] = new Ellipse { Fill = mist, RenderTransform = r.PuffMove[i] };
                r.Layer.Children.Add(r.Puffs[i]);
            }
            Reshape(r);
            return r;
        }

        /// <summary>A new skyline for a ridge (at the start, and each time it is reborn far away).</summary>
        private void Reshape(Ridge r)
        {
            var m = Mountains(rnd, 0.8 + rnd.NextDouble() * 0.5);
            double low = 0.12 + rnd.NextDouble() * 0.1, high = 0.75 + rnd.NextDouble() * 0.4;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(-xSpan, RidgeBottom), true, true);
                var pts = new List<Point>(500);
                // Fine steps near the middle, where the ridge is seen up close; coarse at the far sides.
                double x = -xSpan;
                while (x < xSpan)
                {
                    pts.Add(new Point(x, -(low + (high - low) * m(x))));
                    x += Math.Abs(x) < 2.5 ? 0.03 : 0.12;
                }
                pts.Add(new Point(xSpan, -(low + (high - low) * m(xSpan))));
                pts.Add(new Point(xSpan, RidgeBottom));
                ctx.PolyLineTo(pts, true, true);
            }
            g.Freeze();
            r.Shape.Data = g;

            for (int i = 0; i < r.Puffs.Length; i++)
            {
                double pw = 2.5 + rnd.NextDouble() * 3, ph = 0.28 + rnd.NextDouble() * 0.22;
                r.Puffs[i].Width = pw; r.Puffs[i].Height = ph;
                Canvas.SetTop(r.Puffs[i], -0.12 - ph / 2 - rnd.NextDouble() * 0.15);
                Canvas.SetLeft(r.Puffs[i], -pw / 2);
                r.PuffX[i] = (rnd.NextDouble() - 0.5) * 5;
                r.PuffSpeed[i] = (rnd.NextDouble() - 0.5) * 0.05;
            }
        }

        public override void SetLighting(string lighting, double now)
        {
            string next = SkyPalette.Resolve(lighting);
            if (next == target) return;
            // Start from whatever is on screen right now, even mid-fade.
            var snap = new SkyPalette();
            snap.Blend(from, to, FadeK(now));
            from = snap;
            to = SkyPalette.For(next);
            target = next;
            fadeStart = now;
        }

        private double FadeK(double now) { return Smoothstep((now - fadeStart) / CrossFadeSec); }

        public override void Frame(double sim, double now, double settle)
        {
            var p = cur;
            p.Blend(from, to, FadeK(now));
            p.Punch(settle);

            // Camera: a slow forward glide that drifts side to side and banks into the turns.
            double camX = 0.9 * Math.Sin(sim * 0.037) + 0.35 * Math.Sin(sim * 0.083 + 1.3);
            double camVX = 0.9 * 0.037 * Math.Cos(sim * 0.037) + 0.35 * 0.083 * Math.Cos(sim * 0.083 + 1.3);
            double camH = CamHeight + 0.07 * Math.Sin(sim * 0.052 + 0.4);
            double roll = Math.Max(-1.2, Math.Min(1.2, -camVX * 22));
            bank.Angle = roll;
            double hy = horizonY + H * 0.006 * Math.Sin(sim * 0.06);

            skyTop.Color = p.SkyTop; skyMid.Color = p.SkyMid; skyHorizon.Color = p.SkyHorizon; skyBottom.Color = p.SkyHorizon;

            for (int g = 0; g < stars.Length; g++)
                stars[g].Opacity = p.Stars * (0.6 + 0.4 * Math.Sin(now * starRate[g] + starPhase[g]));

            double ox = p.OrbX * W - camX * W * 0.004, oy = p.OrbY * H;
            double hr = p.HaloR * H * 2, cr = p.OrbR * H * 2;
            Place(haloPlace, ox, oy, hr, hr);
            Place(corePlace, ox, oy, cr, cr);
            Place(glowPlace, ox, hy, W * 1.7, H * 0.24);
            Place(reflectPlace, ox, hy + H * 0.12, cr * 2.4, H * 0.3);
            haloIn.Color = p.OrbHalo; haloNear.Color = Alpha(p.OrbHalo, 0.7); haloMid.Color = Alpha(p.OrbHalo, 0.22); haloOut.Color = Alpha(p.OrbHalo, 0);
            coreIn.Color = p.OrbCore; coreMid.Color = p.OrbCore; coreOut.Color = Alpha(p.OrbCore, 0);
            glowIn.Color = p.HorizonGlow; glowOut.Color = Alpha(p.HorizonGlow, 0);
            reflectIn.Color = Alpha(p.OrbHalo, 0.9); reflectOut.Color = Alpha(p.OrbHalo, 0);
            shimmerFill.Color = Alpha(p.OrbCore, 0.3);
            shimmerMove.X = ox + Math.Sin(now * 0.7) * W * 0.004;
            shimmerMove.Y = hy - horizonY;
            shimmer.Opacity = 0.55 + 0.3 * Math.Sin(now * 1.7);

            cloudIn.Color = p.Cloud; cloudMid.Color = Alpha(p.Cloud, 0.55); cloudOut.Color = Alpha(p.Cloud, 0);
            foreach (var c in clouds)
            {
                // Wraps around: leaves on the left fully hidden, comes back in from the right.
                double x = c.X - sim * c.Drift - camX * c.Parallax;
                x = ((x % c.Span) + c.Span) % c.Span;
                c.Move.X = x - c.Width - W * (Overscan - 1) / 2;
                c.Move.Y = c.Y + (hy - horizonY);
            }

            waterTop.Color = SkyPalette.Mix(p.WaterTop, p.SkyHorizon, 0.25); waterDeep.Color = p.WaterDeep;
            farFill.Color = SkyPalette.Mix(p.Haze, p.SkyHorizon, 0.38);
            farReflectFill.Color = SkyPalette.Mix(p.Haze, p.WaterTop, 0.5);
            farMove.X = -camX * W * 0.012; farMove.Y = hy - horizonY;
            farReflectMove.X = farMove.X; farReflectMove.Y = farMove.Y;

            // Ridges glide towards the camera; one that has passed beneath is reborn at the back.
            double dz = Math.Max(0, sim - lastSim) * Speed;
            lastSim = sim;
            if (dz > 2) dz = 0;   // a jump in time (first frame of a preview render): don't reshuffle
            for (int i = 0; i < ridges.Count; i++)
            {
                var r = ridges[i];
                r.Z -= dz;
                if (r.Z < ZNear)
                {
                    r.Z += ZFar - ZNear;
                    Reshape(r);
                    ridgeHost.Children.Remove(r.Layer);
                    ridgeHost.Children.Insert(0, r.Layer);
                }
            }
            foreach (var r in ridges)
            {
                double s = H / r.Z;
                r.Place.Matrix = new Matrix(s, 0, 0, s, W / 2 - camX * s, hy + camH * s);
                double haze = Math.Pow(Clamp01((r.Z - ZNear) / (ZFar - ZNear)), 0.75);
                r.Layer.Opacity = Clamp01((ZFar - r.Z) / 1.2);
                r.Lit.Color = SkyPalette.Mix(p.RidgeLit, p.Haze, haze);
                r.Body.Color = SkyPalette.Mix(p.RidgeNear, p.Haze, haze);
                var foot = SkyPalette.Mix(SkyPalette.Mix(p.RidgeNear, p.Mist, 0.45), p.Haze, haze);
                r.Foot.Color = foot;
                r.Fade.Color = Alpha(foot, 0);
                double puffs = Clamp01((r.Z - PuffNearZ) / PuffFadeZ);
                bool show = puffs > 0.01;
                if (show != r.PuffsShown)
                {
                    r.PuffsShown = show;
                    foreach (var e in r.Puffs) e.Visibility = show ? Visibility.Visible : Visibility.Hidden;
                }
                if (!show) continue;
                var mist = SkyPalette.Mix(p.Mist, p.Haze, haze * 0.5);
                r.MistIn.Color = Alpha(mist, p.MistAlpha * puffs); r.MistOut.Color = Alpha(mist, 0);
                for (int i = 0; i < r.Puffs.Length; i++) r.PuffMove[i].X = r.PuffX[i] + sim * r.PuffSpeed[i];
            }
        }

        private static Color Alpha(Color c, double k) { return Color.FromArgb((byte)(c.A * k + 0.5), c.R, c.G, c.B); }
    }
}
