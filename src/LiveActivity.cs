using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>
    /// A damped spring stepped once per frame. One spring can drive several properties at once (a morph),
    /// which WPF's per-property animations can't keep in step when retargeted mid-flight.
    /// </summary>
    internal class Spring
    {
        public double Value, Velocity, Target;
        public double Stiffness = 420, DampingRatio = 0.75, Epsilon = 0.001;
        public Action Step, Settled;
        private bool running;
        private TimeSpan last;

        public Spring(double v) { Value = Target = v; }
        public bool Running { get { return running; } }

        public void To(double target)
        {
            Target = target;
            if (running) return;
            if (Value == target && Velocity == 0) return;   // already there
            running = true;
            last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
            Smooth.Begin();
        }

        public void Snap(double v)
        {
            Value = Target = v;
            Velocity = 0;
            Halt();
            if (Step != null) Step();
        }

        public void Halt()
        {
            if (!running) return;
            running = false;
            CompositionTarget.Rendering -= OnFrame;
            Smooth.End();
        }

        private void OnFrame(object sender, EventArgs e)
        {
            try
            {
                var re = e as RenderingEventArgs;
                TimeSpan now = re != null ? re.RenderingTime : TimeSpan.FromTicks(DateTime.UtcNow.Ticks);
                if (now == last) return;   // Rendering can fire more than once per frame
                double dt = last == TimeSpan.Zero ? 1.0 / Math.Max(60, Smooth.RefreshRate) : (now - last).TotalSeconds;
                last = now;
                dt = Math.Max(0, Math.Min(dt, 1.0 / 30));   // after a stall, don't leap
                double c = 2 * DampingRatio * Math.Sqrt(Stiffness);
                int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
                double h = dt / n;
                for (int i = 0; i < n; i++)
                {
                    double a = -Stiffness * (Value - Target) - c * Velocity;
                    Velocity += a * h;
                    Value += Velocity * h;
                }
                bool done = Math.Abs(Value - Target) < Epsilon && Math.Abs(Velocity) < Epsilon * 20;
                if (done) { Value = Target; Velocity = 0; Halt(); }
                if (Step != null) Step();
                if (done && Settled != null) Settled();
            }
            catch (Exception ex) { Program.Log(ex); Halt(); }
        }
    }

    /// <summary>The countdown timer behind the Live Activity (started from the menu bar clock's right-click menu).</summary>
    internal static class LiveTimer
    {
        public static event Action Changed;   // started, paused, finished, cancelled
        public static event Action Tick;      // a few times a second while counting down

        public static bool Active { get; private set; }
        public static bool Paused { get; private set; }
        public static bool Done { get; private set; }
        public static TimeSpan Total { get; private set; }

        private static DateTime end;
        private static TimeSpan left;
        private static DispatcherTimer ticker;
        private static int doneVersion;
        private const int TickMs = 200;
        private const int DoneShowsMs = 12000;   // "Timer done" stays in the bar this long unless dismissed

        public static TimeSpan Remaining
        {
            get
            {
                if (!Active) return TimeSpan.Zero;
                if (Paused) return left;
                var r = end - DateTime.UtcNow;
                return r < TimeSpan.Zero ? TimeSpan.Zero : r;
            }
        }

        /// <summary>Fraction of the time still left (1 at the start, 0 at the end).</summary>
        public static double FractionLeft
        {
            get { return Total.TotalSeconds <= 0 ? 0 : Math.Max(0, Math.Min(1, Remaining.TotalSeconds / Total.TotalSeconds)); }
        }

        public static void Start(int minutes) { Start(TimeSpan.FromMinutes(minutes)); }

        public static void Start(TimeSpan span)
        {
            Total = span;
            end = DateTime.UtcNow + span;
            Active = true; Paused = false; Done = false;
            Run(true);
            Raise(Changed);
        }

        public static void PauseResume()
        {
            if (!Active) return;
            if (Paused) { end = DateTime.UtcNow + left; Paused = false; Run(true); }
            else { left = Remaining; Paused = true; Run(false); }
            Raise(Changed);
        }

        public static void AddMinute()
        {
            if (Done || !Active) { Start(1); return; }
            var m = TimeSpan.FromMinutes(1);
            if (Paused) left += m; else end += m;
            Total += m;
            Raise(Changed);
        }

        public static void Cancel()
        {
            Active = false; Paused = false; Done = false;
            Run(false);
            Raise(Changed);
        }

        public static void Dismiss()
        {
            if (!Done) return;
            Done = false;
            Raise(Changed);
        }

        public static void Repeat() { Start(Total.TotalSeconds > 0 ? Total : TimeSpan.FromMinutes(1)); }

        /// <summary>Formats a span as m:ss (or h:mm:ss).</summary>
        public static string Format(TimeSpan t)
        {
            int s = (int)Math.Ceiling(t.TotalSeconds - 0.05);
            if (s < 0) s = 0;
            if (s >= 3600) return (s / 3600) + ":" + (s / 60 % 60).ToString("00") + ":" + (s % 60).ToString("00");
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        /// <summary>Right-click menu for the clock and the pill.</summary>
        public static ContextMenu Menu()
        {
            var m = new ContextMenu();
            m.Items.Add(new MenuItem { Header = "Start Timer", IsEnabled = false });
            foreach (int min in new[] { 1, 5, 10, 15, 25, 60 })
            {
                int mm = min;
                var mi = new MenuItem { Header = "    " + (min == 1 ? "1 minute" : min + " minutes") };
                mi.Click += (s, e) => Start(mm);
                m.Items.Add(mi);
            }
            if (Active)
            {
                m.Items.Add(new Separator());
                var pr = new MenuItem { Header = Paused ? "Resume Timer" : "Pause Timer" };
                pr.Click += (s, e) => PauseResume();
                m.Items.Add(pr);
                var cancel = new MenuItem { Header = "Cancel Timer" };
                cancel.Click += (s, e) => Cancel();
                m.Items.Add(cancel);
            }
            m.Items.Add(new Separator());
            var mini = new MenuItem { Header = "Music Mini Player" };
            mini.Click += (s, e) => MiniPlayer.Toggle();
            m.Items.Add(mini);
            return m;
        }

        private static void Run(bool on)
        {
            if (ticker == null)
            {
                ticker = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickMs) };
                ticker.Tick += (s, e) => OnTick();
            }
            if (on) ticker.Start(); else ticker.Stop();
        }

        private static void OnTick()
        {
            try
            {
                if (!Active || Paused) { Run(false); return; }
                if (Remaining > TimeSpan.Zero) { Raise(Tick); return; }
                Active = false; Done = true;
                Run(false);
                Chime();
                int v = ++doneVersion;
                Launcher.Later(DoneShowsMs, () => { if (v == doneVersion) Dismiss(); });
                Raise(Changed);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>A soft system sound, or the default beep if those files aren't there.</summary>
        private static void Chime()
        {
            try
            {
                string media = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                foreach (var f in new[] { "Windows Notify Calendar.wav", "Windows Notify System Generic.wav", "Windows Notify.wav" })
                {
                    string p = System.IO.Path.Combine(media, f);
                    if (!File.Exists(p)) continue;
                    new System.Media.SoundPlayer(p).Play();
                    return;
                }
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static void Raise(Action h)
        {
            if (h == null) return;
            try { h(); } catch (Exception ex) { Program.Log(ex); }
        }
    }

    /// <summary>Small helpers shared by the pill, the expanded activity and the mini player.</summary>
    internal static class LiveUi
    {
        public const string GPlay = "", GPause = "", GPrev = "", GNext = "", GShuffle = "",
            GRepeatAll = "", GRepeatOne = "", GNote = "", GClose = "", GAdd = "",
            GBell = "", GMini = "", GPin = "", GPinned = "";

        public static readonly Color TimerOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);

        public static TextBlock Glyph(string g, double size, Brush fg)
        {
            return new TextBlock
            {
                Text = g, FontFamily = new FontFamily(Theme.IconFont), FontSize = size, Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static TextBlock Text(string s, double size, FontWeight weight, Brush fg)
        {
            var t = new TextBlock
            {
                Text = s, FontSize = size, FontWeight = weight, Foreground = fg, FontFamily = new FontFamily(Theme.UiFont),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);   // digits don't jiggle as they count
            return t;
        }

        public static Brush Solid(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>A round icon button that dims while pressed and runs its action on release.</summary>
        public static Border RoundButton(TextBlock glyph, double size, Brush bg, Brush hover, Action a)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = bg, Child = glyph, Cursor = Cursors.Hand };
            b.MouseEnter += (s, e) => b.Background = hover;
            b.MouseLeave += (s, e) => { b.Background = bg; b.Opacity = 1; };
            b.MouseLeftButtonDown += (s, e) => { b.Opacity = 0.6; e.Handled = true; };
            b.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (b.Opacity >= 1) return;   // pressed somewhere else
                b.Opacity = 1;
                try { a(); } catch (Exception ex) { Program.Log(ex); }
            };
            return b;
        }

        /// <summary>Album art (or a music-note placeholder) filling a rounded square.</summary>
        public static void ShowArt(Border b, TextBlock placeholder)
        {
            var art = NowPlaying.Art;
            object tag = art != null ? (object)art : "none";
            if (ReferenceEquals(b.Tag, tag)) return;   // already showing it
            b.Tag = tag;
            if (art != null)
            {
                var ib = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                ib.Freeze();
                b.Background = ib;
                placeholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                var g = new LinearGradientBrush(Color.FromRgb(0x5A, 0x5A, 0x62), Color.FromRgb(0x2C, 0x2C, 0x32), 90);
                g.Freeze();
                b.Background = g;
                placeholder.Visibility = Visibility.Visible;
            }
        }

        /// <summary>A ring arc from 12 o'clock, clockwise, covering `fraction` of the circle.</summary>
        public static Geometry Arc(double size, double thickness, double fraction)
        {
            fraction = Math.Max(0, Math.Min(0.9999, fraction));
            double r = (size - thickness) / 2, c = size / 2;
            var g = new StreamGeometry();
            if (fraction <= 0.0001) { g.Freeze(); return g; }
            double ang = fraction * 2 * Math.PI;
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c, c - r), false, false);
                ctx.ArcTo(new Point(c + r * Math.Sin(ang), c - r * Math.Cos(ang)), new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        public static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    }

    /// <summary>
    /// The Live Activity capsule in the menu bar (left of the status icons): album art and a waveform
    /// while music plays, a countdown ring while a timer runs. Click it to morph it open.
    /// </summary>
    internal class LivePill : Border
    {
        private enum Kind { None, Music, Timer }

        private const double CapsuleHeight = 20, SideGap = 4;
        private const double MusicWidth = 50, TimerWidth = 66;
        private const int WaveMs = 90;               // how often the waveform takes a new audio reading
        private const int KeepAfterPauseMs = 10000;  // the music pill lingers this long after pausing
        private const double PeakFloor = 0.0005;     // below this the speakers are silent

        private readonly Border capsule, art;
        private readonly TextBlock artNote, timerText;
        private readonly Grid musicLayer, timerLayer;
        private readonly Rectangle[] bars = new Rectangle[4];
        private readonly ScaleTransform[] barScale = new ScaleTransform[4];
        private readonly SolidColorBrush barBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xED));
        private readonly System.Windows.Shapes.Path ring;
        private readonly ScaleTransform pop = new ScaleTransform(1, 1);
        private readonly Spring presence = new Spring(0) { Stiffness = 340, Epsilon = 0.002 };
        private readonly Spring width = new Spring(MusicWidth) { Stiffness = 420, DampingRatio = 0.8, Epsilon = 0.2 };
        private readonly Action<Border> rightClick;
        private readonly DispatcherTimer wave, fallback;
        private Kind kind = Kind.None;
        private DateTime pausedAt = DateTime.MinValue, heardAt = DateTime.MinValue;
        private int heardTicks;
        private double waveT;
        private bool wasDone;
        private LiveActivityWindow expanded;
        private static readonly double[] speeds = { 7.1, 9.3, 6.2, 8.4 }, phases = { 0.3, 1.9, 3.1, 4.6 };

        public LivePill(Action<Border> rightClick)
        {
            this.rightClick = rightClick;
            Height = MenuBarWindow.BarHeight;
            Width = 0;
            Visibility = Visibility.Collapsed;
            Background = Brushes.Transparent;
            ClipToBounds = true;

            // Music: tiny art + four bars.
            art = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3.5), VerticalAlignment = VerticalAlignment.Center };
            artNote = LiveUi.Glyph(LiveUi.GNote, 8, Brushes.White);
            art.Child = artNote;
            var waveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < bars.Length; i++)
            {
                barScale[i] = new ScaleTransform(1, 0.25, 0, 6);
                bars[i] = new Rectangle { Width = 2.4, Height = 12, RadiusX = 1.2, RadiusY = 1.2, Fill = barBrush, RenderTransform = barScale[i], Margin = new Thickness(i == 0 ? 0 : 1.6, 0, 0, 0) };
                waveRow.Children.Add(bars[i]);
            }
            var mrow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            mrow.Children.Add(art);
            mrow.Children.Add(waveRow);
            musicLayer = new Grid { Children = { mrow } };

            // Timer: ring + m:ss.
            var ringBox = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            ringBox.Children.Add(new Ellipse { Stroke = LiveUi.Solid(0x55, 0xFF, 0x9F, 0x0A), StrokeThickness = 2 });
            ring = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(LiveUi.TimerOrange), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            ringBox.Children.Add(ring);
            timerText = LiveUi.Text("0:00", 11.5, FontWeights.SemiBold, new SolidColorBrush(LiveUi.TimerOrange));
            timerText.Margin = new Thickness(5, 0, 0, 0);
            var trow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            trow.Children.Add(ringBox);
            trow.Children.Add(timerText);
            timerLayer = new Grid { Children = { trow }, Opacity = 0 };

            var layers = new Grid();
            layers.Children.Add(musicLayer);
            layers.Children.Add(timerLayer);
            capsule = new Border
            {
                Height = CapsuleHeight, CornerRadius = new CornerRadius(CapsuleHeight / 2), Child = layers,
                Background = LiveUi.Solid(0xF0, 0x0E, 0x0E, 0x10), BorderBrush = LiveUi.Solid(0x30, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(0.7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = pop, RenderTransformOrigin = new Point(0.5, 0.5), Cursor = Cursors.Hand
            };
            Child = capsule;

            // On the clear outer box, not the capsule: the capsule is hidden while the open activity stands in
            // for it, and clicking the same spot again should still close it. The box also takes the highlight.
            MouseLeftButtonDown += (s, e) => { e.Handled = true; Expanded().Toggle(this); };
            MouseRightButtonUp += (s, e) => { e.Handled = true; if (this.rightClick != null) this.rightClick(this); };
            capsule.MouseEnter += (s, e) => capsule.Background = LiveUi.Solid(0xFF, 0x22, 0x22, 0x25);
            capsule.MouseLeave += (s, e) => capsule.Background = LiveUi.Solid(0xF0, 0x0E, 0x0E, 0x10);

            presence.Step = Layout;
            width.Step = Layout;
            presence.Settled = () => { if (presence.Value <= 0) Visibility = Visibility.Collapsed; };

            wave = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(WaveMs) };
            wave.Tick += (s, e) => WaveTick();
            fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            fallback.Tick += (s, e) => FallbackTick();

            NowPlaying.Changed += Update;
            LiveTimer.Changed += Update;
            LiveTimer.Tick += UpdateTimerFace;
            IsVisibleChanged += (s, e) => UpdateWave();
            Loaded += (s, e) => ApplySettings();
        }

        /// <summary>Settings.LiveActivities off = no pill at all.</summary>
        public void ApplySettings()
        {
            if (Settings.LiveActivities)
            {
                NowPlaying.Start();
                if (!fallback.IsEnabled) fallback.Start();
            }
            else
            {
                fallback.Stop();
                if (expanded != null) expanded.CloseNow();
            }
            Update();
        }

        private LiveActivityWindow Expanded()
        {
            if (expanded == null) expanded = new LiveActivityWindow();
            return expanded;
        }

        /// <summary>The music pill is live while something plays (and a little after pausing).</summary>
        private bool MusicLive()
        {
            if (NowPlaying.Available)
            {
                if (!NowPlaying.HasSession) return false;
                if (NowPlaying.IsPlaying) { pausedAt = DateTime.MinValue; return true; }
                if (pausedAt == DateTime.MinValue) { pausedAt = DateTime.UtcNow; Launcher.Later(KeepAfterPauseMs + 100, Update); }
                return (DateTime.UtcNow - pausedAt).TotalMilliseconds < KeepAfterPauseMs;
            }
            // No SMTC on this PC: live while the speakers have been busy for a few seconds.
            return heardTicks >= 3 && (DateTime.UtcNow - heardAt).TotalSeconds < 4;
        }

        /// <summary>Re-decides what the pill shows (timer beats music) and springs it in, over or out.</summary>
        public void Update()
        {
            try
            {
                bool open = expanded != null && expanded.IsOpen;
                Kind k = !Settings.LiveActivities ? Kind.None
                       : (LiveTimer.Active || LiveTimer.Done) ? Kind.Timer
                       : MusicLive() ? Kind.Music : Kind.None;
                if (k == Kind.None && open && Settings.LiveActivities) k = kind;   // never pull the pill from under an open activity

                if (k != Kind.None)
                {
                    bool fresh = kind == Kind.None;
                    if (fresh) { width.Snap(k == Kind.Timer ? TimerWidth : MusicWidth); Visibility = Visibility.Visible; presence.DampingRatio = 0.62; }
                    width.To(k == Kind.Timer ? TimerWidth : MusicWidth);
                    presence.To(1);
                    Fade(musicLayer, k == Kind.Music);
                    Fade(timerLayer, k == Kind.Timer);
                }
                else if (kind != Kind.None)
                {
                    presence.DampingRatio = 1;
                    presence.To(0);
                }
                kind = k;

                ShowMusicFace();
                UpdateTimerFace();
                UpdateWave();
                if (open) expanded.Refresh();

                // A finished timer pops its activity open by itself for a moment.
                bool done = LiveTimer.Done;
                if (done && !wasDone && Settings.LiveActivities)
                    Launcher.Later(380, () => { if (LiveTimer.Done && IsVisible) Expanded().Open(this, false); });
                if (!done && wasDone && open && !LiveTimer.Active && expanded.ShowsTimer) expanded.Retract();
                wasDone = done;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private void Fade(UIElement e, bool on)
        {
            var fe = (FrameworkElement)e;
            if (fe.Tag is bool && (bool)fe.Tag == on) return;   // already showing / hidden
            fe.Tag = on;
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(160)));
            e.IsHitTestVisible = on;
        }

        private void Layout()
        {
            double p = Math.Max(0, presence.Value);
            double w = width.Value * p;
            Width = Math.Max(0, w + SideGap * 2 * Math.Min(1, p));
            capsule.Width = Math.Max(0, w);
            double s = 0.55 + 0.45 * Math.Min(1.15, p);
            pop.ScaleX = s;
            pop.ScaleY = s;
            capsule.Opacity = LiveUi.Clamp01(p * 1.6);
        }

        private void ShowMusicFace()
        {
            LiveUi.ShowArt(art, artNote);
            barBrush.Color = NowPlaying.HasTint ? NowPlaying.Tint : Color.FromRgb(0xE8, 0xE8, 0xED);
        }

        private void UpdateTimerFace()
        {
            if (LiveTimer.Done)
            {
                timerText.Text = "Done";
                ring.Data = LiveUi.Arc(12, 2, 0.9999);
                return;
            }
            timerText.Text = LiveTimer.Format(LiveTimer.Remaining);
            ring.Data = LiveUi.Arc(12, 2, LiveTimer.FractionLeft);
            if (expanded != null && expanded.IsOpen) expanded.Refresh();
        }

        /// <summary>The waveform only animates while music is live, playing and actually on screen.</summary>
        private void UpdateWave()
        {
            bool run = kind == Kind.Music && IsVisible && (NowPlaying.Available ? NowPlaying.IsPlaying : true);
            if (run && !wave.IsEnabled) wave.Start();
            else if (!run && wave.IsEnabled)
            {
                wave.Stop();
                for (int i = 0; i < bars.Length; i++)
                    barScale[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(220)));
            }
        }

        private void WaveTick()
        {
            try
            {
                float peak = AudioMeter.Peak();
                double level = Math.Sqrt(peak);   // closer to how loud it sounds
                bool heard = peak > PeakFloor;
                waveT += WaveMs / 1000.0;
                var rnd = new Random((int)(waveT * 1000));
                for (int i = 0; i < bars.Length; i++)
                {
                    double wob = 0.5 + 0.5 * Math.Abs(Math.Sin(waveT * speeds[i] + phases[i]));
                    double target = heard
                        ? 0.2 + 0.8 * Math.Min(1, level * 1.5 * (0.55 + 0.45 * wob) * (0.85 + 0.3 * rnd.NextDouble()))
                        : 0.3 + 0.5 * wob;   // silent but "playing" (muted, or the meter is unavailable)
                    // A short animation to each new reading; capped at 60 fps because it runs for whole songs.
                    var a = new DoubleAnimation(target, TimeSpan.FromMilliseconds(WaveMs + 20));
                    Timeline.SetDesiredFrameRate(a, 60);
                    barScale[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
            }
            catch (Exception ex) { Program.Log(ex); wave.Stop(); }
        }

        /// <summary>Without SMTC, notices sound from the speakers instead (checked once a second, cheap).</summary>
        private void FallbackTick()
        {
            if (NowPlaying.Available || !Settings.LiveActivities) { heardTicks = 0; return; }
            if (AudioMeter.Peak() > PeakFloor * 4) { heardAt = DateTime.UtcNow; heardTicks = Math.Min(heardTicks + 1, 10); }
            else if ((DateTime.UtcNow - heardAt).TotalSeconds > 4) heardTicks = 0;
            bool live = MusicLive();
            if (live != (kind == Kind.Music) && !(LiveTimer.Active || LiveTimer.Done)) Update();
        }

        /// <summary>Used by the expanded activity: hides the capsule while the morph stands in for it.</summary>
        public void SetStandIn(bool hidden)
        {
            capsule.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;
            if (!hidden) Update();
        }

        public bool ShowsTimer { get { return kind == Kind.Timer; } }

        /// <summary>A picture of the capsule as it looks now (the morph starts from it).</summary>
        public BitmapSource Snapshot(double scale)
        {
            try
            {
                double w = capsule.ActualWidth, h = capsule.ActualHeight;
                if (w < 1 || h < 1) return null;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(capsule), null, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }

        public FrameworkElement Capsule { get { return capsule; } }
    }

    /// <summary>
    /// The opened Live Activity. One spring drives position, size and corner radius from the pill's exact
    /// spot to the full card under the menu bar (and back), while the content cross-fades.
    /// </summary>
    internal class LiveActivityWindow : Window
    {
        private const double MusicW = 360, MusicH = 172, TimerW = 300, TimerH = 104, Pad = 34;
        private const double GapBelowBar = 6, OpenRadius = 24;
        private const int ReopenDebounceMs = 300, AutoCloseMs = 5500, RefreshMs = 250;

        private readonly Canvas root = new Canvas();
        private readonly Border island, shadow;
        private readonly RectangleGeometry clip = new RectangleGeometry();
        private readonly Image standIn = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        private readonly Grid content = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly ScaleTransform contentScale = new ScaleTransform(1, 1);
        private readonly Spring morph = new Spring(0) { Stiffness = 380, Epsilon = 0.0015 };
        private readonly DispatcherTimer refresh;
        private Grid musicView, timerView;
        private Rect from, to;
        private double pillRadius = 10;
        private LivePill pill;
        private bool closing, activated, timerMode;
        private DateTime lastClosed = DateTime.MinValue;
        private int autoCloseVersion;

        // Music view parts
        private Border mArt, mProgressFill, mPlay;
        private TextBlock mArtNote, mTitle, mArtist, mElapsed, mLeft, mPlayGlyph;
        private Grid mTrack;
        // Timer view parts
        private TextBlock tTime, tLabel, tPauseGlyph;
        private Border tPause, tAdd;
        private StackPanel tRunning, tDone;

        public LiveActivityWindow()
        {
            Title = "Mac Look Live Activity";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily(Theme.UiFont);
            FontSize = 13;
            Foreground = Brushes.White;

            shadow = new Border { Background = LiveUi.Solid(0x60, 0, 0, 0), Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.4 } };
            island = new Border
            {
                Background = LiveUi.Solid(0xF5, 0x10, 0x10, 0x12), BorderBrush = LiveUi.Solid(0x26, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(0.8), Clip = clip
            };
            var layers = new Grid();
            layers.Children.Add(standIn);
            content.RenderTransform = contentScale;
            content.RenderTransformOrigin = new Point(0.5, 0);
            layers.Children.Add(content);
            island.Child = layers;
            root.Children.Add(shadow);
            root.Children.Add(island);
            Content = root;

            musicView = BuildMusic();
            timerView = BuildTimer();
            content.Children.Add(musicView);
            content.Children.Add(timerView);

            morph.Step = Frame;
            morph.Settled = () => { if (morph.Target <= 0) Finish(); };
            refresh = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(RefreshMs) };
            refresh.Tick += (s, e) => Refresh();

            Deactivated += (s, e) => { if (activated) Retract(); };
            // Alt+F4 would destroy the window for good; morph it away instead (unless the app is quitting).
            Closing += (s, e) => { if (!Dispatcher.HasShutdownStarted) { e.Cancel = true; Retract(); } };
            Activated += (s, e) => activated = true;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Retract(); e.Handled = true; } };
            MouseEnter += (s, e) => autoCloseVersion++;   // the user is looking at it: don't vanish
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            };
        }

        public bool IsOpen { get { return IsVisible && !closing; } }
        public bool ShowsTimer { get { return timerMode; } }

        public void Toggle(LivePill p)
        {
            if (IsOpen) { Retract(); return; }
            if ((DateTime.UtcNow - lastClosed).TotalMilliseconds < ReopenDebounceMs) return;   // the click that closed us
            Open(p, true);
        }

        /// <summary>Morphs open out of the pill. activate=false (timer done) shows it without taking focus.</summary>
        public void Open(LivePill p, bool activate)
        {
            try
            {
                if (IsOpen || p == null || !p.IsVisible) return;
                pill = p;
                timerMode = p.ShowsTimer;
                musicView.Visibility = timerMode ? Visibility.Collapsed : Visibility.Visible;
                timerView.Visibility = timerMode ? Visibility.Visible : Visibility.Collapsed;

                var src = PresentationSource.FromVisual(p.Capsule);
                if (src == null) return;
                double scale = src.CompositionTarget.TransformToDevice.M11;
                Point tl = p.Capsule.PointToScreen(new Point(0, 0));
                var pr = new Rect(tl.X / scale, tl.Y / scale, p.Capsule.ActualWidth, p.Capsule.ActualHeight);
                pillRadius = pr.Height / 2;

                double w = timerMode ? TimerW : MusicW, h = timerMode ? TimerH : MusicH;
                var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                double sw = screen.Width / scale, sl = screen.Left / scale;
                double ex = Math.Max(sl + 8, Math.Min(sl + sw - w - 8, pr.Left + pr.Width / 2 - w / 2));
                var er = new Rect(ex, MenuBarWindow.BarHeight + GapBelowBar, w, h);

                // The window covers both shapes plus room for the shadow; the empty parts are click-through.
                double left = Math.Min(pr.Left, er.Left) - Pad, top = Math.Min(pr.Top, er.Top) - 4;
                double right = Math.Max(pr.Right, er.Right) + Pad, bottom = er.Bottom + Pad;
                Left = left; Top = top; Width = right - left; Height = bottom - top;
                from = new Rect(pr.Left - left, pr.Top - top, pr.Width, pr.Height);
                to = new Rect(er.Left - left, er.Top - top, w, h);
                content.Width = w; content.Height = h;

                standIn.Source = p.Snapshot(scale);
                standIn.Width = pr.Width; standIn.Height = pr.Height;
                Refresh();

                closing = false;
                activated = false;
                ShowActivated = activate;
                morph.Snap(0);
                Show();
                p.SetStandIn(true);
                if (activate)
                {
                    Activate();
                    Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
                }
                else
                {
                    int v = ++autoCloseVersion;
                    Launcher.Later(AutoCloseMs, () => { if (v == autoCloseVersion && !activated) Retract(); });
                }
                morph.DampingRatio = 0.7;
                morph.To(1);
                refresh.Start();
            }
            catch (Exception ex) { Program.Log(ex); CloseNow(); }
        }

        /// <summary>Morphs back into the pill.</summary>
        public void Retract()
        {
            if (!IsVisible || closing) return;
            closing = true;
            lastClosed = DateTime.UtcNow;
            morph.DampingRatio = 0.86;
            morph.To(0);
        }

        public void CloseNow()
        {
            morph.Snap(0);
            Finish();
        }

        private void Finish()
        {
            refresh.Stop();
            closing = false;
            if (IsVisible) { lastClosed = DateTime.UtcNow; Hide(); }
            standIn.Source = null;
            if (pill != null) pill.SetStandIn(false);
        }

        private void Frame()
        {
            double p = morph.Value;
            double x = Lerp(from.X, to.X, p), y = Lerp(from.Y, to.Y, p);
            double w = Math.Max(4, Lerp(from.Width, to.Width, p)), h = Math.Max(4, Lerp(from.Height, to.Height, p));
            double r = Math.Min(h / 2, Lerp(pillRadius, OpenRadius, LiveUi.Clamp01(p)));
            Canvas.SetLeft(island, x); Canvas.SetTop(island, y);
            island.Width = w; island.Height = h; island.CornerRadius = new CornerRadius(r);
            Canvas.SetLeft(shadow, x + 2); Canvas.SetTop(shadow, y + 2);
            shadow.Width = Math.Max(0, w - 4); shadow.Height = Math.Max(0, h - 4); shadow.CornerRadius = new CornerRadius(r);
            shadow.Opacity = LiveUi.Clamp01(p);
            clip.Rect = new Rect(0, 0, w, h);
            clip.RadiusX = r; clip.RadiusY = r;
            // The pill's picture fades out fast; the full content fades in once there's room for it.
            standIn.Opacity = LiveUi.Clamp01(1 - p * 3.5);
            content.Opacity = LiveUi.Clamp01((p - 0.3) / 0.55);
            double s = 0.9 + 0.1 * LiveUi.Clamp01(p);
            contentScale.ScaleX = s; contentScale.ScaleY = s;
        }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        // ------------------------------------------------------------------ music card
        private Grid BuildMusic()
        {
            var g = new Grid { Margin = new Thickness(16) };
            var dim = LiveUi.Solid(0xA6, 0xFF, 0xFF, 0xFF);

            // Row 1: art, title / artist, and the mini player button.
            var top = new DockPanel { Height = 56, VerticalAlignment = VerticalAlignment.Top };
            mArt = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(12) };
            mArtNote = LiveUi.Glyph(LiveUi.GNote, 22, LiveUi.Solid(0xB0, 0xFF, 0xFF, 0xFF));
            mArt.Child = mArtNote;
            top.Children.Add(mArt);
            var mini = LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GMini, 13, Brushes.White), 30, LiveUi.Solid(0x26, 0xFF, 0xFF, 0xFF), LiveUi.Solid(0x40, 0xFF, 0xFF, 0xFF),
                () => { Retract(); MiniPlayer.Toggle(); });
            mini.ToolTip = "Open Mini Player";
            mini.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(mini, Dock.Right);
            top.Children.Insert(1, mini);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
            mTitle = LiveUi.Text("", 14.5, FontWeights.SemiBold, Brushes.White);
            mArtist = LiveUi.Text("", 12.5, FontWeights.Normal, dim);
            mArtist.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(mTitle);
            words.Children.Add(mArtist);
            top.Children.Add(words);
            g.Children.Add(top);

            // Row 2: progress (click to seek).
            var prog = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 70, 0, 0), Height = 16 };
            mElapsed = LiveUi.Text("0:00", 10.5, FontWeights.Medium, dim);
            mElapsed.Width = 38;
            mLeft = LiveUi.Text("-0:00", 10.5, FontWeights.Medium, dim);
            mLeft.Width = 38;
            mLeft.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(mElapsed, Dock.Left);
            DockPanel.SetDock(mLeft, Dock.Right);
            prog.Children.Add(mElapsed);
            prog.Children.Add(mLeft);
            mTrack = new Grid { Height = 16, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            mTrack.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = LiveUi.Solid(0x40, 0xFF, 0xFF, 0xFF) });
            mProgressFill = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Background = LiveUi.Solid(0xF0, 0xFF, 0xFF, 0xFF) };
            mTrack.Children.Add(mProgressFill);
            mTrack.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (!NowPlaying.CanSeek || NowPlaying.DurationSec <= 0 || mTrack.ActualWidth < 1) return;
                NowPlaying.Seek(LiveUi.Clamp01(e.GetPosition(mTrack).X / mTrack.ActualWidth) * NowPlaying.DurationSec);
                Refresh();
            };
            prog.Children.Add(mTrack);
            g.Children.Add(prog);

            // Row 3: previous / play-pause / next.
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            var clear = Brushes.Transparent;
            var hover = LiveUi.Solid(0x22, 0xFF, 0xFF, 0xFF);
            controls.Children.Add(LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GPrev, 17, Brushes.White), 40, clear, hover, () => { NowPlaying.Previous(); MediaKeyFallback(0xB1); }));
            mPlayGlyph = LiveUi.Glyph(LiveUi.GPlay, 22, Brushes.White);
            mPlay = LiveUi.RoundButton(mPlayGlyph, 46, clear, hover, () => { if (NowPlaying.Available) NowPlaying.PlayPause(); else Native.PressKeys(0xB3); Refresh(); });
            mPlay.Margin = new Thickness(18, 0, 18, 0);
            controls.Children.Add(mPlay);
            controls.Children.Add(LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GNext, 17, Brushes.White), 40, clear, hover, () => { NowPlaying.Next(); MediaKeyFallback(0xB0); }));
            g.Children.Add(controls);
            return g;
        }

        /// <summary>Without SMTC the buttons press the keyboard's media keys instead.</summary>
        private static void MediaKeyFallback(byte vk) { if (!NowPlaying.Available) Native.PressKeys(vk); }

        // ------------------------------------------------------------------ timer card
        private Grid BuildTimer()
        {
            var g = new Grid { Margin = new Thickness(20, 14, 16, 14) };
            var orange = new SolidColorBrush(LiveUi.TimerOrange);
            orange.Freeze();

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            tLabel = LiveUi.Text("Timer", 12, FontWeights.SemiBold, orange);
            tTime = LiveUi.Text("0:00", 40, FontWeights.Light, orange);
            tTime.Margin = new Thickness(-1, -4, 0, -4);
            words.Children.Add(tLabel);
            words.Children.Add(tTime);
            g.Children.Add(words);

            var soft = LiveUi.Solid(0x33, 0xFF, 0x9F, 0x0A);
            var softHover = LiveUi.Solid(0x4D, 0xFF, 0x9F, 0x0A);
            var grey = LiveUi.Solid(0x33, 0xFF, 0xFF, 0xFF);
            var greyHover = LiveUi.Solid(0x4D, 0xFF, 0xFF, 0xFF);

            tRunning = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            tAdd = LiveUi.RoundButton(LiveUi.Text("+1", 13, FontWeights.SemiBold, Brushes.White), 44, grey, greyHover, () => { LiveTimer.AddMinute(); Refresh(); });
            ((TextBlock)tAdd.Child).HorizontalAlignment = HorizontalAlignment.Center;
            tAdd.ToolTip = "Add 1 minute";
            tPauseGlyph = LiveUi.Glyph(LiveUi.GPause, 17, orange);
            tPause = LiveUi.RoundButton(tPauseGlyph, 44, soft, softHover, () => { LiveTimer.PauseResume(); Refresh(); });
            tPause.Margin = new Thickness(8, 0, 8, 0);
            var cancel = LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GClose, 15, Brushes.White), 44, grey, greyHover, () => { LiveTimer.Cancel(); Retract(); });
            cancel.ToolTip = "Cancel";
            tRunning.Children.Add(tAdd);
            tRunning.Children.Add(tPause);
            tRunning.Children.Add(cancel);
            g.Children.Add(tRunning);

            tDone = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var again = LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GRepeatAll, 16, orange), 44, soft, softHover, () => { LiveTimer.Repeat(); Refresh(); });
            again.ToolTip = "Repeat";
            again.Margin = new Thickness(0, 0, 8, 0);
            var ok = LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GClose, 15, Brushes.White), 44, grey, greyHover, () => { LiveTimer.Dismiss(); Retract(); });
            ok.ToolTip = "Dismiss";
            tDone.Children.Add(again);
            tDone.Children.Add(ok);
            g.Children.Add(tDone);
            return g;
        }

        /// <summary>Copies the current music / timer state into the card.</summary>
        public void Refresh()
        {
            try
            {
                if (timerMode)
                {
                    bool done = LiveTimer.Done;
                    tLabel.Text = done ? "Timer" : LiveTimer.Paused ? "Paused" : "Timer";
                    tTime.Text = done ? "Done" : LiveTimer.Format(LiveTimer.Remaining);
                    tPauseGlyph.Text = LiveTimer.Paused ? LiveUi.GPlay : LiveUi.GPause;
                    tRunning.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
                    tDone.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
                    return;
                }
                LiveUi.ShowArt(mArt, mArtNote);
                bool any = NowPlaying.HasSession || !NowPlaying.Available;
                mTitle.Text = !NowPlaying.Available ? "Playing audio" : NowPlaying.Title.Length > 0 ? NowPlaying.Title : any ? "Unknown title" : "Not Playing";
                mArtist.Text = !NowPlaying.Available ? "Details aren't available on this PC" : NowPlaying.Artist.Length > 0 ? NowPlaying.Artist : NowPlaying.AppName;
                mPlayGlyph.Text = NowPlaying.IsPlaying || !NowPlaying.Available ? LiveUi.GPause : LiveUi.GPlay;
                double dur = NowPlaying.DurationSec, pos = NowPlaying.PositionSec;
                bool timeline = dur > 0;
                mElapsed.Text = timeline ? LiveTimer.Format(TimeSpan.FromSeconds(Math.Floor(pos))) : "";
                mLeft.Text = timeline ? "-" + LiveTimer.Format(TimeSpan.FromSeconds(Math.Ceiling(dur - pos))) : "";
                mTrack.Opacity = timeline ? 1 : 0.35;
                double tw = mTrack.ActualWidth > 0 ? mTrack.ActualWidth : MusicW - 32 - 76;
                mProgressFill.Width = timeline ? Math.Max(5, tw * LiveUi.Clamp01(pos / dur)) : 0;
                var tint = NowPlaying.HasTint ? new SolidColorBrush(NowPlaying.Tint) : LiveUi.Solid(0xF0, 0xFF, 0xFF, 0xFF);
                mProgressFill.Background = tint;
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }
}
