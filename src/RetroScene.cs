using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacLook
{
    /// <summary>
    /// The Retro screen saver: a 1-bit, black-and-white homage to early Macs. Three line shapes whose
    /// corners bounce off the screen edges independently (so they fold and stretch), each trailed by
    /// fading echoes of where it just was, a chunky pixel clock that slowly wanders (no burn-in), and a
    /// faint CRT scanline and vignette on top. Positions are computed straight from the time, so any
    /// frame rate gives the same motion.
    /// </summary>
    internal sealed class RetroScene : SaverScene
    {
        private const int Echoes = 11;
        private const double EchoLag = 0.08;      // seconds between two echoes

        private class Shape
        {
            public double[] X0, Y0, VX, VY;
            public Path[] Trail;
        }

        private readonly double scale;             // device pixels per DIP: keeps every pixel crisp
        private readonly List<Shape> shapes = new List<Shape>();
        private readonly Canvas clockGroup = new Canvas();
        private readonly TranslateTransform clockMove = new TranslateTransform();
        private readonly Rectangle clockBack = new Rectangle { Fill = Brushes.Black };
        private readonly Path timePath = new Path { Fill = Brushes.White };
        private readonly Path datePath = new Path { Fill = Brushes.White, Opacity = 0.8 };
        private readonly double bigPix, smallPix;
        private double clockW, clockH, clockX0, clockY0;
        private int shownMinute = -1;

        public RetroScene(double w, double h, double scale, int seed) : base(w, h)
        {
            this.scale = scale <= 0 ? 1 : scale;
            var rnd = new Random(seed);
            Root.Background = Brushes.Black;
            RenderOptions.SetEdgeMode(Root, EdgeMode.Aliased);   // hard 1-bit edges, no smoothing

            double line = Math.Max(1, Math.Round(1.5 * this.scale)) / this.scale;
            int[] corners = { 4, 4, 3 };
            foreach (int n in corners)
            {
                var s = new Shape { X0 = new double[n], Y0 = new double[n], VX = new double[n], VY = new double[n], Trail = new Path[Echoes] };
                for (int i = 0; i < n; i++)
                {
                    s.X0[i] = rnd.NextDouble() * w * 4;
                    s.Y0[i] = rnd.NextDouble() * h * 4;
                    double a = rnd.NextDouble() * Math.PI * 2, v = h * (0.09 + rnd.NextDouble() * 0.13);
                    s.VX[i] = Math.Cos(a) * v;
                    s.VY[i] = Math.Sin(a) * v;
                }
                // Oldest echo first so the newest line is drawn on top.
                for (int k = Echoes - 1; k >= 0; k--)
                {
                    s.Trail[k] = new Path
                    {
                        Stroke = Brushes.White, StrokeThickness = line, StrokeLineJoin = PenLineJoin.Miter,
                        Opacity = Math.Pow(1 - k / (double)Echoes, 1.8)
                    };
                    Root.Children.Add(s.Trail[k]);
                }
                shapes.Add(s);
            }

            bigPix = Snap(h / 95);
            smallPix = Math.Max(1 / this.scale, Snap(bigPix * 0.42));
            clockGroup.Children.Add(clockBack);   // black card behind the digits: the lines pass under it
            clockGroup.Children.Add(timePath);
            clockGroup.Children.Add(datePath);
            clockGroup.RenderTransform = clockMove;
            Root.Children.Add(clockGroup);
            clockX0 = rnd.NextDouble() * w * 3;
            clockY0 = rnd.NextDouble() * h * 3;
            UpdateClock();

            double hintPix = Math.Max(1 / this.scale, Snap(h / 400));
            double hintW;
            var hint = new Path { Fill = Brushes.White, Opacity = 0.45, Data = Text("MOVE THE MOUSE OR PRESS ANY KEY", hintPix, out hintW) };
            Canvas.SetLeft(hint, Snap((w - hintW) / 2));
            Canvas.SetTop(hint, Snap(h - h * 0.05 - 7 * hintPix));
            Root.Children.Add(hint);

            // CRT touch: thin dark scanlines every 3 device pixels, and darker corners.
            double period = 3 / this.scale;
            var lines = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, period),
                SpreadMethod = GradientSpreadMethod.Repeat
            };
            lines.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            lines.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.62));
            lines.GradientStops.Add(new GradientStop(Color.FromArgb(80, 0, 0, 0), 0.62));
            lines.GradientStops.Add(new GradientStop(Color.FromArgb(80, 0, 0, 0), 1));
            lines.Freeze();
            Root.Children.Add(new Rectangle { Width = w, Height = h, Fill = lines });
            var vignette = new RadialGradientBrush { RadiusX = 0.75, RadiusY = 0.75 };
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(170, 0, 0, 0), 1));
            vignette.Freeze();
            Root.Children.Add(new Rectangle { Width = w, Height = h, Fill = vignette });
        }

        public override bool WantsClock { get { return false; } }

        private double Snap(double v) { return Math.Round(v * scale) / scale; }

        /// <summary>Bouncing between 0 and len: a triangle wave, so the position needs no history.</summary>
        private static double Bounce(double p, double len)
        {
            if (len <= 0) return 0;
            double m = p % (2 * len);
            if (m < 0) m += 2 * len;
            return m <= len ? m : 2 * len - m;
        }

        public override void Frame(double sim, double now, double settle)
        {
            double inset = H * 0.03, bw = W - inset * 2, bh = H - inset * 2;
            foreach (var s in shapes)
            {
                int n = s.X0.Length;
                for (int k = 0; k < Echoes; k++)
                {
                    double t = sim - k * EchoLag;
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        for (int i = 0; i < n; i++)
                        {
                            var pt = new Point(inset + Bounce(s.X0[i] + s.VX[i] * t, bw), inset + Bounce(s.Y0[i] + s.VY[i] * t, bh));
                            if (i == 0) ctx.BeginFigure(pt, false, true); else ctx.LineTo(pt, true, false);
                        }
                    }
                    g.Freeze();
                    s.Trail[k].Data = g;
                }
            }

            var clockNow = DateTime.Now;
            if (clockNow.Hour * 60 + clockNow.Minute != shownMinute) UpdateClock();
            // The clock wanders slowly, stepping in whole "pixels" like an old screen would.
            double cx = Bounce(clockX0 + sim * H * 0.018, Math.Max(1, W - clockW - inset * 2)) + inset;
            double cy = Bounce(clockY0 + sim * H * 0.013, Math.Max(1, H * 0.8 - clockH - inset)) + inset;
            clockMove.X = Math.Round(cx / smallPix) * smallPix;
            clockMove.Y = Math.Round(cy / smallPix) * smallPix;
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;
            shownMinute = now.Hour * 60 + now.Minute;
            string time = Settings.Clock24h ? now.ToString("HH:mm") : now.ToString("h:mm", CultureInfo.InvariantCulture) + (now.Hour < 12 ? " AM" : " PM");
            string date = Plain(now.ToString("dddd d MMMM", CultureInfo.CurrentCulture));
            double tw, dw;
            timePath.Data = Text(time, bigPix, out tw);
            datePath.Data = Text(date, smallPix, out dw);
            Canvas.SetLeft(timePath, Snap(Math.Max(0, (dw - tw) / 2)));
            Canvas.SetLeft(datePath, Snap(Math.Max(0, (tw - dw) / 2)));
            Canvas.SetTop(datePath, 7 * bigPix + bigPix * 1.6);
            clockW = Math.Max(tw, dw);
            clockH = 7 * bigPix + bigPix * 1.6 + 7 * smallPix;
            double pad = bigPix * 2;
            Canvas.SetLeft(clockBack, -pad); Canvas.SetTop(clockBack, -pad);
            clockBack.Width = clockW + pad * 2; clockBack.Height = clockH + pad * 2;
        }

        /// <summary>Upper case without accents, so every letter exists in the pixel font.</summary>
        private static string Plain(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        /// <summary>Text in the 5x7 pixel font, one rectangle per horizontal run of lit pixels.</summary>
        private static Geometry Text(string text, double p, out double width)
        {
            var g = new GeometryGroup();
            double x = 0;
            foreach (char ch in text)
            {
                string glyph;
                if (Font.TryGetValue(ch, out glyph))
                {
                    var rows = glyph.Split('|');
                    for (int ry = 0; ry < 7 && ry < rows.Length; ry++)
                    {
                        int run = -1;
                        for (int cx = 0; cx <= 5; cx++)
                        {
                            bool on = cx < 5 && cx < rows[ry].Length && rows[ry][cx] == '#';
                            if (on && run < 0) run = cx;
                            if (!on && run >= 0)
                            {
                                g.Children.Add(new RectangleGeometry(new Rect(x + run * p, ry * p, (cx - run) * p, p)));
                                run = -1;
                            }
                        }
                    }
                }
                x += 6 * p;
            }
            width = Math.Max(0, x - p);
            g.Freeze();
            return g;
        }

        private static readonly Dictionary<char, string> Font = new Dictionary<char, string>
        {
            { '0', ".###.|#...#|#..##|#.#.#|##..#|#...#|.###." }, { '1', "..#..|.##..|..#..|..#..|..#..|..#..|.###." },
            { '2', ".###.|#...#|....#|...#.|..#..|.#...|#####" }, { '3', "#####|...#.|..#..|...#.|....#|#...#|.###." },
            { '4', "...#.|..##.|.#.#.|#..#.|#####|...#.|...#." }, { '5', "#####|#....|####.|....#|....#|#...#|.###." },
            { '6', "..##.|.#...|#....|####.|#...#|#...#|.###." }, { '7', "#####|....#|...#.|..#..|.#...|.#...|.#..." },
            { '8', ".###.|#...#|#...#|.###.|#...#|#...#|.###." }, { '9', ".###.|#...#|#...#|.####|....#|...#.|.##.." },
            { ':', ".....|..#..|..#..|.....|..#..|..#..|....." }, { '.', ".....|.....|.....|.....|.....|.##..|.##.." },
            { ',', ".....|.....|.....|.....|.##..|..#..|.#..." }, { '-', ".....|.....|.....|.###.|.....|.....|....." },
            { 'A', ".###.|#...#|#...#|#####|#...#|#...#|#...#" }, { 'B', "####.|#...#|#...#|####.|#...#|#...#|####." },
            { 'C', ".###.|#...#|#....|#....|#....|#...#|.###." }, { 'D', "###..|#..#.|#...#|#...#|#...#|#..#.|###.." },
            { 'E', "#####|#....|#....|####.|#....|#....|#####" }, { 'F', "#####|#....|#....|####.|#....|#....|#...." },
            { 'G', ".###.|#...#|#....|#.###|#...#|#...#|.####" }, { 'H', "#...#|#...#|#...#|#####|#...#|#...#|#...#" },
            { 'I', ".###.|..#..|..#..|..#..|..#..|..#..|.###." }, { 'J', "..###|...#.|...#.|...#.|...#.|#..#.|.##.." },
            { 'K', "#...#|#..#.|#.#..|##...|#.#..|#..#.|#...#" }, { 'L', "#....|#....|#....|#....|#....|#....|#####" },
            { 'M', "#...#|##.##|#.#.#|#.#.#|#...#|#...#|#...#" }, { 'N', "#...#|#...#|##..#|#.#.#|#..##|#...#|#...#" },
            { 'O', ".###.|#...#|#...#|#...#|#...#|#...#|.###." }, { 'P', "####.|#...#|#...#|####.|#....|#....|#...." },
            { 'Q', ".###.|#...#|#...#|#...#|#.#.#|#..#.|.##.#" }, { 'R', "####.|#...#|#...#|####.|#.#..|#..#.|#...#" },
            { 'S', ".####|#....|#....|.###.|....#|....#|####." }, { 'T', "#####|..#..|..#..|..#..|..#..|..#..|..#.." },
            { 'U', "#...#|#...#|#...#|#...#|#...#|#...#|.###." }, { 'V', "#...#|#...#|#...#|#...#|#...#|.#.#.|..#.." },
            { 'W', "#...#|#...#|#...#|#.#.#|#.#.#|#.#.#|.#.#." }, { 'X', "#...#|#...#|.#.#.|..#..|.#.#.|#...#|#...#" },
            { 'Y', "#...#|#...#|.#.#.|..#..|..#..|..#..|..#.." }, { 'Z', "#####|....#|...#.|..#..|.#...|#....|#####" },
        };
    }
}
