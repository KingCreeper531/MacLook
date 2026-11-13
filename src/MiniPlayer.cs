using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MacLook
{
    /// <summary>The Music mini player: a small floating window for whatever is playing.</summary>
    internal static class MiniPlayer
    {
        private static MiniPlayerWindow win;

        /// <summary>Shows the mini player, or hides it if it is already showing.</summary>
        public static void Toggle()
        {
            try
            {
                NowPlaying.Start();
                if (win == null) win = new MiniPlayerWindow();
                if (win.IsVisible && !win.Hiding) win.HideAnimated();
                else win.ShowAnimated();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static bool IsOpen { get { return win != null && win.IsVisible && !win.Hiding; } }
    }

    /// <summary>
    /// Album art on a glowing backdrop made from the art itself (blurred and colour-matched), title and artist
    /// that scroll when too long, a scrubbable progress bar and the transport controls. Clicking the art
    /// morphs it into a corner and opens a details panel beneath it.
    /// </summary>
    internal class MiniPlayerWindow : Window
    {
        private const double W = 300, H = 440, Margin_ = 24, Radius = 22;
        private const double ArtBig = 260, ArtSmall = 64, Side = 20;
        private const int RefreshMs = 250;

        private readonly Canvas stage = new Canvas { Width = W, Height = H };
        private readonly Grid frame;
        private readonly Image ambBack = new Image { Stretch = Stretch.UniformToFill }, ambFront = new Image { Stretch = Stretch.UniformToFill };
        private readonly Border artHost, artShadow, chrome, details;
        private readonly TextBlock artNote, playGlyph, shuffleGlyph, repeatGlyph, pinGlyph, elapsed, remaining;
        private readonly Marquee title, artist;
        private readonly StackPanel titleBox;
        private readonly TranslateTransform titleShift = new TranslateTransform(), artistShift = new TranslateTransform();
        private readonly ScaleTransform showScale = new ScaleTransform(1, 1), detailsScale = new ScaleTransform(1, 1);
        private readonly Grid track;
        private readonly Border trackBg, trackFill, playBtn, shuffleBtn, repeatBtn;
        private readonly Spring morph = new Spring(0) { Stiffness = 320, DampingRatio = 0.72, Epsilon = 0.0015 };
        private readonly Spring trackThick = new Spring(4) { Stiffness = 500, DampingRatio = 0.7, Epsilon = 0.02 };
        private readonly System.Windows.Threading.DispatcherTimer refresh;
        private TextBlock dFrom, dAlbum, dTrack, dPosition, dShuffle, dRepeat;
        private int shownVersion = -1;
        private bool scrubbing, hiding, placed;
        private double scrubFrac;
        private Point pressAt;
        private object ambientShown;

        public bool Hiding { get { return hiding; } }

        public MiniPlayerWindow()
        {
            Title = "Mini Player";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            FontFamily = new FontFamily(Theme.UiFont);
            FontSize = 13;
            Foreground = Brushes.White;
            var dim = LiveUi.Solid(0xB3, 0xFF, 0xFF, 0xFF);
            var faint = LiveUi.Solid(0x80, 0xFF, 0xFF, 0xFF);

            // ---- backdrop: the art, blurred into soft colour, with a darker floor for the controls
            var backdrop = new Grid();
            RenderOptions.SetBitmapScalingMode(ambBack, BitmapScalingMode.Linear);
            RenderOptions.SetBitmapScalingMode(ambFront, BitmapScalingMode.Linear);
            backdrop.Children.Add(new Border { Background = LiveUi.Solid(0xFF, 0x26, 0x26, 0x2A) });
            backdrop.Children.Add(ambBack);
            backdrop.Children.Add(ambFront);
            var floor = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            floor.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0, 0, 0), 0));
            floor.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0, 0, 0), 0.5));
            floor.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 1));
            floor.Freeze();
            backdrop.Children.Add(new Border { Background = floor });

            // ---- art (click: details; drag: move the window)
            artShadow = new Border { Background = LiveUi.Solid(0x70, 0, 0, 0), Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.45 } };
            artHost = new Border { CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand };
            artNote = LiveUi.Glyph(LiveUi.GNote, 40, LiveUi.Solid(0x99, 0xFF, 0xFF, 0xFF));
            artHost.Child = new Viewbox { Child = new Border { Width = 100, Height = 100, Child = artNote }, Stretch = Stretch.Uniform };
            artHost.MouseLeftButtonDown += (s, e) => { pressAt = e.GetPosition(this); artHost.CaptureMouse(); e.Handled = true; };
            artHost.MouseMove += (s, e) =>
            {
                if (!artHost.IsMouseCaptured) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - pressAt.X) + Math.Abs(p.Y - pressAt.Y) < 5) return;
                artHost.ReleaseMouseCapture();
                Drag();
            };
            artHost.MouseLeftButtonUp += (s, e) =>
            {
                if (!artHost.IsMouseCaptured) return;
                artHost.ReleaseMouseCapture();
                e.Handled = true;
                morph.To(morph.Target > 0.5 ? 0 : 1);
                if (morph.Target > 0.5) details.Visibility = Visibility.Visible;
            };
            stage.Children.Add(artShadow);
            stage.Children.Add(artHost);

            // ---- title / artist
            title = new Marquee(15, FontWeights.SemiBold, Brushes.White);
            artist = new Marquee(13, FontWeights.Normal, dim);
            title.RenderTransform = titleShift;
            artist.RenderTransform = artistShift;
            titleBox = new StackPanel();
            titleBox.Children.Add(title);
            titleBox.Children.Add(artist);
            stage.Children.Add(titleBox);

            // ---- details (morphs out from under the art)
            details = new Border { Width = W - Side * 2, Visibility = Visibility.Collapsed, RenderTransform = detailsScale, RenderTransformOrigin = new Point(0.5, 0) };
            details.Child = BuildDetails(dim, faint);
            Canvas.SetLeft(details, Side);
            Canvas.SetTop(details, Side + ArtSmall + 14);
            stage.Children.Add(details);

            // ---- progress (drag to scrub)
            var prog = new StackPanel { Width = W - Side * 2 };
            track = new Grid { Height = 16, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            trackBg = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = LiveUi.Solid(0x40, 0xFF, 0xFF, 0xFF), VerticalAlignment = VerticalAlignment.Center };
            trackFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(trackBg);
            track.Children.Add(trackFill);
            track.MouseEnter += (s, e) => trackThick.To(7);
            track.MouseLeave += (s, e) => { if (!scrubbing) trackThick.To(4); };
            track.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (!NowPlaying.CanSeek || NowPlaying.DurationSec <= 0) return;
                scrubbing = true;
                track.CaptureMouse();
                Scrub(e.GetPosition(track).X);
            };
            track.MouseMove += (s, e) => { if (scrubbing) Scrub(e.GetPosition(track).X); };
            track.MouseLeftButtonUp += (s, e) =>
            {
                if (!scrubbing) return;
                e.Handled = true;
                track.ReleaseMouseCapture();
            };
            track.LostMouseCapture += (s, e) =>
            {
                if (!scrubbing) return;
                scrubbing = false;
                NowPlaying.Seek(scrubFrac * NowPlaying.DurationSec);
                if (!track.IsMouseOver) trackThick.To(4);
                Refresh();
            };
            trackThick.Step = () => { trackBg.Height = trackThick.Value; trackFill.Height = trackThick.Value; trackBg.CornerRadius = trackFill.CornerRadius = new CornerRadius(trackThick.Value / 2); };
            prog.Children.Add(track);
            var times = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
            elapsed = LiveUi.Text("0:00", 10.5, FontWeights.Medium, faint);
            remaining = LiveUi.Text("-0:00", 10.5, FontWeights.Medium, faint);
            DockPanel.SetDock(remaining, Dock.Right);
            times.Children.Add(remaining);
            times.Children.Add(elapsed);
            prog.Children.Add(times);
            Canvas.SetLeft(prog, Side);
            Canvas.SetTop(prog, 336);
            stage.Children.Add(prog);

            // ---- transport
            var clear = Brushes.Transparent;
            var hover = LiveUi.Solid(0x22, 0xFF, 0xFF, 0xFF);
            var row = new Grid { Width = W - Side * 2, Height = 52 };
            for (int i = 0; i < 5; i++) row.ColumnDefinitions.Add(new ColumnDefinition());
            shuffleGlyph = LiveUi.Glyph(LiveUi.GShuffle, 14, dim);
            shuffleBtn = LiveUi.RoundButton(shuffleGlyph, 34, clear, hover, () => { NowPlaying.ToggleShuffle(); Refresh(); });
            shuffleBtn.ToolTip = "Shuffle";
            var prev = LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GPrev, 20, Brushes.White), 42, clear, hover, () => { if (NowPlaying.Available) NowPlaying.Previous(); else Native.PressKeys(0xB1); });
            playGlyph = LiveUi.Glyph(LiveUi.GPlay, 24, Brushes.White);
            playBtn = LiveUi.RoundButton(playGlyph, 52, LiveUi.Solid(0x2E, 0xFF, 0xFF, 0xFF), LiveUi.Solid(0x48, 0xFF, 0xFF, 0xFF),
                () => { if (NowPlaying.Available) NowPlaying.PlayPause(); else Native.PressKeys(0xB3); Refresh(); });
            var next = LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GNext, 20, Brushes.White), 42, clear, hover, () => { if (NowPlaying.Available) NowPlaying.Next(); else Native.PressKeys(0xB0); });
            repeatGlyph = LiveUi.Glyph(LiveUi.GRepeatAll, 14, dim);
            repeatBtn = LiveUi.RoundButton(repeatGlyph, 34, clear, hover, () => { NowPlaying.CycleRepeat(); Refresh(); });
            repeatBtn.ToolTip = "Repeat";
            var buttons = new UIElement[] { shuffleBtn, prev, playBtn, next, repeatBtn };
            for (int i = 0; i < buttons.Length; i++) { Grid.SetColumn(buttons[i], i); row.Children.Add(buttons[i]); }
            Canvas.SetLeft(row, Side);
            Canvas.SetTop(row, 374);
            stage.Children.Add(row);

            // ---- window chrome that appears on hover: keep on top, close
            var chromeRow = new StackPanel { Orientation = Orientation.Horizontal };
            pinGlyph = LiveUi.Glyph(LiveUi.GPinned, 11, Brushes.White);
            var pin = LiveUi.RoundButton(pinGlyph, 26, LiveUi.Solid(0x66, 0, 0, 0), LiveUi.Solid(0x99, 0, 0, 0), () => { Topmost = !Topmost; Refresh(); });
            pin.ToolTip = "Keep on Top";
            var close = LiveUi.RoundButton(LiveUi.Glyph(LiveUi.GClose, 10, Brushes.White), 26, LiveUi.Solid(0x66, 0, 0, 0), LiveUi.Solid(0x99, 0, 0, 0), HideAnimated);
            close.ToolTip = "Close";
            close.Margin = new Thickness(6, 0, 0, 0);
            chromeRow.Children.Add(pin);
            chromeRow.Children.Add(close);
            chrome = new Border { Child = chromeRow, Opacity = 0 };
            Canvas.SetRight(chrome, 12);
            Canvas.SetTop(chrome, 12);
            stage.Children.Add(chrome);

            // ---- assemble: shadow, card (clipped to its rounded shape), glass rim
            var card = new Grid { Width = W, Height = H, Clip = new RectangleGeometry(new Rect(0, 0, W, H), Radius, Radius) };
            card.Children.Add(backdrop);
            card.Children.Add(stage);
            card.Children.Add(new Border { CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), BorderBrush = LiveUi.Solid(0x38, 0xFF, 0xFF, 0xFF), IsHitTestVisible = false });
            card.MouseLeftButtonDown += (s, e) => { if (!e.Handled) Drag(); };
            frame = new Grid { Margin = new Thickness(Margin_), RenderTransform = showScale, RenderTransformOrigin = new Point(0.5, 0.5) };
            frame.Children.Add(new Border { CornerRadius = new CornerRadius(Radius), Background = LiveUi.Solid(0x50, 0, 0, 0), Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 10, Direction = 270, Opacity = 0.45 } });
            frame.Children.Add(card);
            Content = frame;

            morph.Step = Frame;
            morph.Settled = () =>
            {
                if (morph.Value <= 0) details.Visibility = Visibility.Collapsed;
                title.Restart(); artist.Restart();   // the space for the words changed
            };
            Frame();

            refresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshMs) };
            refresh.Tick += (s, e) => Refresh();
            NowPlaying.Changed += () => { if (IsVisible) Refresh(); };
            MouseEnter += (s, e) => { Fade(chrome, 1); title.Restart(); artist.Restart(); };
            MouseLeave += (s, e) => Fade(chrome, 0);
            PreviewKeyDown += OnKey;
            Closing += (s, e) => { if (!Dispatcher.HasShutdownStarted) { e.Cancel = true; HideAnimated(); } };
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            };
        }

        private UIElement BuildDetails(Brush dim, Brush faint)
        {
            var st = new StackPanel();
            var head = LiveUi.Text("PLAYING FROM", 10.5, FontWeights.SemiBold, faint);
            st.Children.Add(head);
            dFrom = LiveUi.Text("", 15, FontWeights.SemiBold, Brushes.White);
            dFrom.Margin = new Thickness(0, 1, 0, 10);
            st.Children.Add(dFrom);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            dAlbum = AddRow(g, "Album", dim);
            dTrack = AddRow(g, "Track", dim);
            dPosition = AddRow(g, "Time", dim);
            dShuffle = AddRow(g, "Shuffle", dim);
            dRepeat = AddRow(g, "Repeat", dim);
            st.Children.Add(g);
            // Windows doesn't hand the play queue or lyrics to other apps, so say so instead of inventing them.
            var note = new TextBlock
            {
                Text = "Lyrics and Up Next aren't available here: Windows doesn't share them with other apps.",
                FontSize = 11, Foreground = faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            };
            st.Children.Add(note);
            return st;
        }

        private static TextBlock AddRow(Grid g, string label, Brush dim)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            var l = LiveUi.Text(label, 12, FontWeights.Normal, dim);
            Grid.SetRow(l, r);
            g.Children.Add(l);
            var v = LiveUi.Text("", 12, FontWeights.Medium, Brushes.White);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return v;
        }

        // ------------------------------------------------------------------ show / hide
        public void ShowAnimated()
        {
            hiding = false;
            if (!placed)
            {
                placed = true;
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - (W + Margin_ * 2) - 8;
                Top = wa.Top + 30;
            }
            Refresh();
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Show();
            Activate();
            refresh.Start();
            var d = TimeSpan.FromMilliseconds(240);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            showScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, d) { EasingFunction = ease });
            showScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, d) { EasingFunction = ease });
            title.Restart();
            artist.Restart();
        }

        public void HideAnimated()
        {
            if (!IsVisible || hiding) return;
            hiding = true;
            var d = TimeSpan.FromMilliseconds(140);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            showScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.94, d) { EasingFunction = ease });
            showScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.94, d) { EasingFunction = ease });
            var fade = new DoubleAnimation(Opacity, 0, d) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                if (!hiding) return;
                hiding = false;
                refresh.Stop();
                title.Stop(); artist.Stop();
                morph.Snap(morph.Target); trackThick.Snap(4);
                Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void Drag()
        {
            try { if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove(); } catch (InvalidOperationException) { }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { HideAnimated(); e.Handled = true; }
            else if (e.Key == Key.Space) { if (NowPlaying.Available) NowPlaying.PlayPause(); else Native.PressKeys(0xB3); Refresh(); e.Handled = true; }
            else if (e.Key == Key.Left && NowPlaying.CanSeek) { NowPlaying.Seek(NowPlaying.PositionSec - 10); e.Handled = true; }
            else if (e.Key == Key.Right && NowPlaying.CanSeek) { NowPlaying.Seek(NowPlaying.PositionSec + 10); e.Handled = true; }
        }

        private static void Fade(UIElement e, double to)
        {
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));
        }

        // ------------------------------------------------------------------ the art <-> details morph
        private void Frame()
        {
            double p = morph.Value, q = LiveUi.Clamp01(p);
            double size = Lerp(ArtBig, ArtSmall, p);
            Canvas.SetLeft(artHost, Side); Canvas.SetTop(artHost, Side);
            artHost.Width = Math.Max(20, size); artHost.Height = Math.Max(20, size);
            artHost.CornerRadius = new CornerRadius(Lerp(14, 10, q));
            Canvas.SetLeft(artShadow, Side + 3); Canvas.SetTop(artShadow, Side + 3);
            artShadow.Width = Math.Max(0, size - 6); artShadow.Height = Math.Max(0, size - 6);
            artShadow.CornerRadius = artHost.CornerRadius;

            // The words travel from under the big art to beside the small one.
            Canvas.SetLeft(titleBox, Lerp(Side, Side + ArtSmall + 14, p));
            Canvas.SetTop(titleBox, Lerp(Side + ArtBig + 14, Side + 12, p));
            titleBox.Width = Math.Max(40, Lerp(W - Side * 2, W - Side * 3 - ArtSmall + 6, q));

            // The panel grows down out of the header.
            details.Opacity = LiveUi.Clamp01((p - 0.25) / 0.6);
            detailsScale.ScaleY = 0.82 + 0.18 * q;
            detailsScale.ScaleX = 0.96 + 0.04 * q;
        }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        // ------------------------------------------------------------------ progress
        private void Scrub(double x)
        {
            double w = Math.Max(1, track.ActualWidth);
            scrubFrac = LiveUi.Clamp01(x / w);
            ShowProgress(scrubFrac * NowPlaying.DurationSec, NowPlaying.DurationSec);
        }

        private void ShowProgress(double pos, double dur)
        {
            bool timeline = dur > 0;
            double w = track.ActualWidth > 0 ? track.ActualWidth : W - Side * 2;
            trackFill.Width = timeline ? Math.Max(trackThick.Value, w * LiveUi.Clamp01(pos / dur)) : 0;
            elapsed.Text = timeline ? LiveTimer.Format(TimeSpan.FromSeconds(Math.Floor(pos))) : "";
            remaining.Text = timeline ? "-" + LiveTimer.Format(TimeSpan.FromSeconds(Math.Ceiling(dur - pos))) : "";
            track.Opacity = timeline && NowPlaying.CanSeek ? 1 : 0.4;
        }

        // ------------------------------------------------------------------ state -> screen
        private void Refresh()
        {
            try
            {
                bool smtc = NowPlaying.Available, has = NowPlaying.HasSession;
                LiveUi.ShowArt(artHost, artNote);
                ShowAmbient();

                string t = !smtc ? "Now Playing" : !has ? "Not Playing" : NowPlaying.Title.Length > 0 ? NowPlaying.Title : "Unknown title";
                string a = !smtc ? "Track details aren't available on this PC" : !has ? "Play something in any music app" : NowPlaying.Artist.Length > 0 ? NowPlaying.Artist : NowPlaying.AppName;
                title.Text = t;
                artist.Text = a;
                if (NowPlaying.TrackVersion != shownVersion)
                {
                    if (shownVersion >= 0 && IsVisible) Jiggle();
                    shownVersion = NowPlaying.TrackVersion;
                }

                if (!scrubbing) ShowProgress(NowPlaying.PositionSec, NowPlaying.DurationSec);
                playGlyph.Text = NowPlaying.IsPlaying ? LiveUi.GPause : LiveUi.GPlay;

                // The art's own colour tints the progress bar and the active toggles.
                var tint = new SolidColorBrush(NowPlaying.HasTint ? NowPlaying.Tint : Colors.White);
                tint.Freeze();
                trackFill.Background = tint;
                var dim = LiveUi.Solid(0xB3, 0xFF, 0xFF, 0xFF);
                bool sh = NowPlaying.Shuffle ?? false;
                shuffleGlyph.Foreground = sh ? tint : dim;
                shuffleBtn.Opacity = NowPlaying.CanShuffle ? 1 : 0.35;
                repeatGlyph.Text = NowPlaying.Repeat == 1 ? LiveUi.GRepeatOne : LiveUi.GRepeatAll;
                repeatGlyph.Foreground = NowPlaying.Repeat > 0 ? tint : dim;
                repeatBtn.Opacity = NowPlaying.CanRepeat ? 1 : 0.35;
                pinGlyph.Text = Topmost ? LiveUi.GPinned : LiveUi.GPin;

                string app = has ? NowPlaying.AppName : "—";
                dFrom.Text = app;
                dAlbum.Text = NowPlaying.Album.Length > 0 ? NowPlaying.Album : "—";
                dTrack.Text = NowPlaying.TrackNumber > 0 ? (NowPlaying.TrackCount > 0 ? NowPlaying.TrackNumber + " of " + NowPlaying.TrackCount : NowPlaying.TrackNumber.ToString()) : "—";
                dPosition.Text = NowPlaying.DurationSec > 0
                    ? LiveTimer.Format(TimeSpan.FromSeconds(Math.Floor(NowPlaying.PositionSec))) + " of " + LiveTimer.Format(TimeSpan.FromSeconds(NowPlaying.DurationSec))
                    : "—";
                dShuffle.Text = !NowPlaying.CanShuffle && NowPlaying.Shuffle == null ? "Not offered by " + app : sh ? "On" : "Off";
                dRepeat.Text = !NowPlaying.CanRepeat && NowPlaying.Repeat < 0 ? "Not offered by " + app
                             : NowPlaying.Repeat == 2 ? "All" : NowPlaying.Repeat == 1 ? "One" : "Off";
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Cross-fades the glowing backdrop to the new art's colours.</summary>
        private void ShowAmbient()
        {
            var amb = NowPlaying.Ambient;
            object key = amb != null ? (object)amb : "none";
            if (ReferenceEquals(ambientShown, key)) return;
            ambientShown = key;
            ambBack.Source = ambFront.Source;
            ambFront.Source = amb;
            if (IsVisible) ambFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        }

        /// <summary>New track: the words slide in with a small elastic overshoot.</summary>
        private void Jiggle()
        {
            var ease = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut };
            titleShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = ease });
            artistShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(50) });
            title.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            artist.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        /// <summary>One line of text that scrolls a few times when it doesn't fit (then rests until hovered).</summary>
        private class Marquee : Canvas
        {
            private const double Gap = 36, Speed = 30, Hold = 2.2;
            private const int Loops = 3;
            private readonly TextBlock a, b;
            private readonly TranslateTransform tx = new TranslateTransform();
            private string text = "";

            public Marquee(double size, FontWeight weight, Brush fg)
            {
                ClipToBounds = true;
                Height = Math.Ceiling(size * 1.4);
                a = LiveUi.Text("", size, weight, fg);
                b = LiveUi.Text("", size, weight, fg);
                a.RenderTransform = tx;
                b.RenderTransform = tx;
                b.Visibility = Visibility.Collapsed;
                Children.Add(a);
                Children.Add(b);
                IsVisibleChanged += (s, e) => Restart();
            }

            public string Text
            {
                get { return text; }
                set
                {
                    if (value == text) return;
                    text = value ?? "";
                    a.Text = b.Text = text;
                    Restart();
                }
            }

            public void Stop()
            {
                tx.BeginAnimation(TranslateTransform.XProperty, null);
                tx.X = 0;
            }

            public void Restart()
            {
                Stop();
                b.Visibility = Visibility.Collapsed;
                double avail = Width > 0 ? Width : ActualWidth;
                var parent = Parent as FrameworkElement;
                if (parent != null && parent.Width > 0) avail = parent.Width;
                a.MaxWidth = double.PositiveInfinity;
                a.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double tw = a.DesiredSize.Width;
                if (!IsVisible || avail < 10 || tw <= avail + 0.5)
                {
                    a.MaxWidth = Math.Max(10, avail);   // fits (or hidden): plain, with an ellipsis
                    return;
                }
                double dist = tw + Gap;
                Canvas.SetLeft(b, dist);
                b.Visibility = Visibility.Visible;
                var k = new DoubleAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(Loops) };
                k.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Hold))));
                k.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Hold + dist / Speed))));
                k.FillBehavior = FillBehavior.Stop;   // ends where it began (the copy sits exactly there)
                Timeline.SetDesiredFrameRate(k, 60);  // slow scrolling text doesn't need 180 fps
                tx.BeginAnimation(TranslateTransform.XProperty, k);
            }
        }
    }
}
