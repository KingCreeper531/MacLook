using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace MacLook
{
    /// <summary>A floating window made of liquid-glass cards that refract whatever was on screen when it opened.</summary>
    internal class GlassPanel : Window
    {
        protected Pix Snapshot;
        private readonly List<KeyValuePair<Border, GlassStyle>> parts = new List<KeyValuePair<Border, GlassStyle>>();
        private DateTime lastHidden = DateTime.MinValue;

        public GlassPanel()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            FontFamily = new FontFamily(Theme.UiFont);
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "MenuFg");
            Deactivated += (s, e) => HidePanel();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; } };
        }

        protected Border GlassCard(GlassStyle style, double radius)
        {
            var b = new Border { CornerRadius = new CornerRadius(radius), UseLayoutRounding = true };
            b.SetResourceReference(Border.BackgroundProperty, "MenuBg");
            parts.Add(new KeyValuePair<Border, GlassStyle>(b, style));
            b.SizeChanged += (s, e) => { if (IsVisible && Snapshot != null) GlassFx.Apply(b, Snapshot, style); };
            return b;
        }

        protected void ClearGlassCards() { parts.Clear(); }

        protected static FrameworkElement WithShadow(Border glass, double radius)
        {
            var g = new Grid();
            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)),
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.32 }
            });
            g.Children.Add(glass);
            return g;
        }

        public void Toggle()
        {
            if (IsVisible && !closing) { HidePanel(); return; }
            if ((DateTime.Now - lastHidden).TotalMilliseconds < ReopenDebounceMs) return;   // the click that closed us
            Open();
        }

        protected virtual void OnOpening() { }
        protected virtual void Place() { }
        protected virtual void OnShown() { }

        /// <summary>Speaker icon for the current master volume / mute state.</summary>
        protected static string VolumeGlyph()
        {
            float v = Audio.GetVolume();
            return Audio.GetMute() || v < 0.01f ? "" : v < 0.34f ? "" : v < 0.67f ? "" : "";
        }

        /// <summary>Hang from the menu bar at the right edge of the main screen.</summary>
        protected void PlaceTopRight()
        {
            Left = SystemParameters.PrimaryScreenWidth - ActualWidth + 6;
            Top = MenuBarWindow.BarHeight - 2;
        }

        public void Open()
        {
            closing = false;
            OnOpening();
            if (IsVisible) Hide();
            // Hiding an active window raises Deactivated, which may have started a close; cancel it.
            closing = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Show();
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            OnShown();
            // Lay out and place while still invisible, then photograph only the area behind the panel
            // (a fully transparent window isn't in the picture). Much faster than the whole screen.
            UpdateLayout();
            Place();
            UpdateLayout();
            var src = PresentationSource.FromVisual(this);
            double s = src == null ? 1 : src.CompositionTarget.TransformToDevice.M11;
            int x = (int)(Left * s), y = (int)(Top * s), w = (int)(ActualWidth * s), h = (int)(ActualHeight * s);
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int capX = Math.Max(b.Left, x), capY = Math.Max(b.Top, y);
            int capW = Math.Min(x + w, b.Right) - capX;
            int capH = Math.Min(b.Bottom - capY, h + (int)(GrowRoom * s));   // room to grow (e.g. Spotlight results)
            Snapshot = ScreenCapture.CaptureRegion(capX, capY, Math.Max(1, capW), Math.Max(1, capH)) ?? ScreenCapture.Capture();
            foreach (var p in parts) GlassFx.Apply(p.Key, Snapshot, p.Value);
            Entrance();
        }

        private bool closing;
        private const int FadeMs = 110;               // open / close fade length
        private const int ReopenDebounceMs = 350;     // ignore a Toggle this soon after hiding
        private const double GrowRoom = 700;          // extra height (DIPs) photographed below the panel

        /// <summary>Shrink slightly toward the top and fade out, then hide.</summary>
        public void HidePanel()
        {
            if (!IsVisible || closing) return;
            lastHidden = DateTime.Now;
            closing = true;
            var dur = TimeSpan.FromMilliseconds(FadeMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fe = Content as FrameworkElement;
            if (fe != null)
            {
                var st = new ScaleTransform(1, 1);
                fe.RenderTransformOrigin = new Point(0.5, 0);
                fe.RenderTransform = st;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.95, dur) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.95, dur) { EasingFunction = ease });
            }
            var fade = new DoubleAnimation(Opacity, 0, dur) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                if (!closing) return;
                closing = false;
                Hide();
                Snapshot = null;
            };
            BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>A short fade with a small springy drop (no blur effect, which is slow to render).</summary>
        private void Entrance()
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs)));
            var fe = Content as FrameworkElement;
            if (fe == null) return;
            var st = new ScaleTransform(0.96, 0.96);
            var drop = new TranslateTransform(0, -8);
            var group = new TransformGroup();
            group.Children.Add(st);
            group.Children.Add(drop);
            fe.RenderTransformOrigin = new Point(0.5, 0);
            fe.RenderTransform = group;
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(190);
            drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        }

        // ---- small building blocks shared by panels ----
        protected static Border Module(UIElement child, double radius = 18)
        {
            var b = new Border { CornerRadius = new CornerRadius(radius), Padding = new Thickness(12), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
            return b;
        }

        protected static TextBlock Label(string s, double size = 13, bool bold = false, double opacity = 1)
        {
            return new TextBlock
            {
                Text = s, FontSize = size, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        protected static Border Circle(string glyph, bool active, double size = 30)
        {
            var tb = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(Theme.IconFont), FontSize = size * 0.47,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            if (active) tb.Foreground = Brushes.White;
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = tb };
            b.SetResourceReference(Border.BackgroundProperty, active ? "Accent" : "Inactive");
            return b;
        }

        protected static void Clickable(FrameworkElement e, Action a)
        {
            e.Cursor = Cursors.Hand;
            e.MouseLeftButtonDown += (s, ev) => { e.Opacity = 0.7; ev.Handled = true; };
            e.MouseLeave += (s, ev) => e.Opacity = 1;
            e.MouseLeftButtonUp += (s, ev) => { e.Opacity = 1; ev.Handled = true; a(); };
        }
    }

    /// <summary>Slider that rubber-bands past its ends (idea from React Bits' ElasticSlider).</summary>
    internal class ElasticSlider : Grid
    {
        private readonly Border fill, track;
        private readonly ScaleTransform stretch = new ScaleTransform(1, 1);
        private double value;
        public event Action<double> ValueChanged;
        public readonly TextBlock Icon;

        public double Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1, value)); UpdateFill(); }
        }

        public ElasticSlider(string glyph)
        {
            Height = 28;
            track = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true };
            track.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            fill = new Border { CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, "TrackFill");
            Icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(Theme.IconFont), FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x50))
            };
            var inner = new Grid();
            inner.Children.Add(fill);
            inner.Children.Add(Icon);
            track.Child = inner;
            Children.Add(track);
            RenderTransform = stretch;
            Cursor = Cursors.Hand;
            SizeChanged += (s, e) => UpdateFill();
            MouseLeftButtonDown += (s, e) => { CaptureMouse(); Drag(e.GetPosition(this).X); e.Handled = true; };
            MouseMove += (s, e) => { if (IsMouseCaptured) Drag(e.GetPosition(this).X); };
            MouseLeftButtonUp += (s, e) => { ReleaseMouseCapture(); SpringBack(); e.Handled = true; };
            LostMouseCapture += (s, e) => SpringBack();
        }

        private void Drag(double x)
        {
            double w = Math.Max(1, ActualWidth);
            double raw = x / w;
            Value = raw;
            if (ValueChanged != null) ValueChanged(Value);
            double over = raw < 0 ? -raw * w : raw > 1 ? (raw - 1) * w : 0;
            double s = 1 + Math.Min(0.08, Math.Sqrt(over) * 0.008);
            RenderTransformOrigin = new Point(raw < 0 ? 1 : 0, 0.5);
            stretch.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            stretch.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            stretch.ScaleX = s;
            stretch.ScaleY = 1 - (s - 1) * 0.6;
        }

        private void SpringBack()
        {
            var ease = new ElasticEase { Oscillations = 2, Springiness = 6, EasingMode = EasingMode.EaseOut };
            stretch.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
            stretch.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
        }

        private void UpdateFill()
        {
            double w = ActualWidth;
            fill.Width = Math.Max(Height, value * w);
        }
    }

    internal class ControlCenterWindow : GlassPanel
    {
        private ElasticSlider volume;
        private Border darkCircle, wifiCircle;
        private TextBlock wifiStatus;

        public ControlCenterWindow()
        {
            Title = "Control Center";
            var card = GlassCard(GlassStyles.Panel, 26);
            card.Padding = new Thickness(12);
            card.Width = 330;
            card.Child = Build();
            var outer = WithShadow(card, 26);
            outer.Margin = new Thickness(14, 6, 16, 30);
            Content = outer;
        }

        private UIElement Build()
        {
            var stack = new StackPanel();

            // Row 1: connectivity + two toggles
            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition());
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            row1.ColumnDefinitions.Add(new ColumnDefinition());

            var conn = new StackPanel();
            wifiCircle = Circle("", true);
            wifiStatus = Label("", 11, false, 0.6);
            conn.Children.Add(ConnRow(wifiCircle, "Wi-Fi", wifiStatus, () => Launcher.Open("ms-settings:network")));
            conn.Children.Add(ConnRow(Circle("", true), "Bluetooth", Label("Settings", 11, false, 0.6), () => Launcher.Open("ms-settings:bluetooth")));
            conn.Children.Add(ConnRow(Circle("", true), "Nearby Share", Label("Settings", 11, false, 0.6), () => Launcher.Open("ms-settings:crossdevice")));
            var connModule = Module(conn);
            row1.Children.Add(connModule);

            var right = new StackPanel();
            var focus = Module(Tile(Circle("", false), "Focus"));
            Clickable(focus, () => { HidePanel(); Launcher.Open("ms-settings:notifications"); });
            var mirror = Module(Tile(Circle("", false), "Screen Mirroring"));
            mirror.Margin = new Thickness(0, 10, 0, 0);
            Clickable(mirror, () => { HidePanel(); Native.PressKeys(Native.VK_WIN, (byte)'K'); });
            right.Children.Add(focus);
            right.Children.Add(mirror);
            Grid.SetColumn(right, 2);
            row1.Children.Add(right);
            stack.Children.Add(row1);

            // Row 2: small square tiles
            var row2 = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 0) };
            darkCircle = Circle("", Theme.Dark, 34);
            row2.Children.Add(SmallTile(darkCircle, "Dark Mode", () => { ThemeSwitch.SetDark(!Theme.Dark); }, new Thickness(0, 0, 5, 0)));
            row2.Children.Add(SmallTile(Circle("", false, 34), "Screenshot", () => { HidePanel(); Launcher.Later(250, () => Native.PressKeys(Native.VK_WIN, Native.VK_SHIFT, (byte)'S')); }, new Thickness(5, 0, 5, 0)));
            row2.Children.Add(SmallTile(Circle("", false, 34), "Night Light", () => { HidePanel(); Launcher.Open("ms-settings:nightlight"); }, new Thickness(5, 0, 0, 0)));
            stack.Children.Add(row2);

            // Display
            var disp = new StackPanel();
            disp.Children.Add(Label("Display", 13, true));
            var dispRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var dispBtn = Pill("Display Settings…", () => { HidePanel(); Launcher.Open("ms-settings:display"); });
            dispRow.Children.Add(dispBtn);
            disp.Children.Add(dispRow);
            var dispModule = Module(disp);
            dispModule.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(dispModule);

            // Sound
            var sound = new StackPanel();
            sound.Children.Add(Label("Sound", 13, true));
            volume = new ElasticSlider("") { Margin = new Thickness(0, 8, 0, 0) };
            volume.ValueChanged += v => { Audio.SetVolume((float)v); UpdateVolumeGlyph(); };
            // Preview + Handled: the slider captures the mouse on press, so the icon would never see its own button-up.
            volume.Icon.PreviewMouseLeftButtonDown += (s, e) => { Audio.SetMute(!Audio.GetMute()); UpdateVolumeGlyph(); e.Handled = true; };
            sound.Children.Add(volume);
            var soundModule = Module(sound);
            soundModule.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(soundModule);

            // Now playing
            var media = new DockPanel();
            var mlabel = Label("Now Playing", 13, true);
            media.Children.Add(mlabel);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(MediaButton("", 0xB1));
            buttons.Children.Add(MediaButton("", 0xB3));
            buttons.Children.Add(MediaButton("", 0xB0));
            DockPanel.SetDock(buttons, Dock.Right);
            media.Children.Insert(0, buttons);
            var mediaModule = Module(media);
            mediaModule.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(mediaModule);
            return stack;
        }

        private static UIElement ConnRow(Border circle, string title, TextBlock sub, Action a)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
            circle.Margin = new Thickness(0, 0, 9, 0);
            row.Children.Add(circle);
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(Label(title, 13, true));
            t.Children.Add(sub);
            row.Children.Add(t);
            Clickable(row, a);
            return row;
        }

        private static UIElement Tile(Border circle, string title)
        {
            var row = new DockPanel();
            circle.Margin = new Thickness(0, 0, 9, 0);
            row.Children.Add(circle);
            row.Children.Add(Label(title, 13, true));
            return row;
        }

        private static UIElement SmallTile(Border circle, string title, Action a, Thickness margin)
        {
            var st = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            circle.HorizontalAlignment = HorizontalAlignment.Center;
            st.Children.Add(circle);
            var l = Label(title, 11, false, 0.85);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.Margin = new Thickness(0, 6, 0, 0);
            st.Children.Add(l);
            var m = Module(st);
            m.Padding = new Thickness(6, 10, 6, 8);
            m.Margin = margin;
            Clickable(m, a);
            return m;
        }

        private static Border Pill(string text, Action a)
        {
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 5, 12, 6), Child = Label(text, 12) };
            b.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            Clickable(b, a);
            return b;
        }

        private static UIElement MediaButton(string glyph, byte vk)
        {
            var t = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(Theme.IconFont), FontSize = 16,
                Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent
            };
            Clickable(t, () => Native.PressKeys(vk));
            return t;
        }

        private void UpdateVolumeGlyph()
        {
            volume.Icon.Text = VolumeGlyph();
        }

        protected override void OnOpening()
        {
            volume.Value = Audio.GetVolume();
            UpdateVolumeGlyph();
            darkCircle.SetResourceReference(Border.BackgroundProperty, Theme.Dark ? "Accent" : "Inactive");
            wifiStatus.Text = NetStatus();
        }

        private static string NetStatus()
        {
            try
            {
                foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) return "Connected";
                    if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) return "Ethernet";
                }
            }
            catch { }
            return "Not connected";
        }

        protected override void Place() { PlaceTopRight(); }
    }

    internal class NotificationCenterWindow : GlassPanel
    {
        private readonly StackPanel stack = new StackPanel { Width = 350 };

        public NotificationCenterWindow()
        {
            Title = "Notification Center";
            stack.Margin = new Thickness(14, 6, 16, 30);
            Content = stack;
        }

        protected override void OnOpening()
        {
            // Rebuilt each time so the date is always current.
            stack.Children.Clear();
            ClearGlassCards();
            var now = DateTime.Now;
            var ci = CultureInfo.InvariantCulture;

            var dateCard = GlassCard(GlassStyles.Panel, 24);
            dateCard.Padding = new Thickness(18, 14, 18, 16);
            var d = new StackPanel();
            var day = Label(now.ToString("dddd", ci).ToUpperInvariant(), 12, true);
            day.SetResourceReference(TextBlock.ForegroundProperty, "Red");
            d.Children.Add(day);
            d.Children.Add(Label(now.ToString("MMMM d", ci), 30, true));
            d.Children.Add(Label(now.ToString("h:mm tt", ci), 15, false, 0.7));
            dateCard.Child = d;
            stack.Children.Add(Spaced(WithShadow(dateCard, 24)));

            var calCard = GlassCard(GlassStyles.Panel, 24);
            calCard.Padding = new Thickness(16, 14, 16, 14);
            calCard.Child = Calendar(now);
            stack.Children.Add(Spaced(WithShadow(calCard, 24)));

            var actions = GlassCard(GlassStyles.Panel, 24);
            actions.Padding = new Thickness(10);
            var grid = new UniformGrid { Columns = 2 };
            grid.Children.Add(ActionTile("", "Notifications", () => Native.PressKeys(Native.VK_WIN, (byte)'N'), new Thickness(0, 0, 5, 0)));
            grid.Children.Add(ActionTile("", "Widgets", () => Native.PressKeys(Native.VK_WIN, (byte)'W'), new Thickness(5, 0, 0, 0)));
            actions.Child = grid;
            stack.Children.Add(Spaced(WithShadow(actions, 24)));
        }

        private static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(0, 0, 0, 10); return e; }

        private UIElement ActionTile(string glyph, string text, Action a, Thickness margin)
        {
            var row = new DockPanel();
            var c = Circle(glyph, false, 30);
            c.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(c);
            row.Children.Add(Label(text, 13, true));
            var m = Module(row, 16);
            m.Padding = new Thickness(8);
            m.Margin = margin;
            Clickable(m, () => { HidePanel(); Launcher.Later(150, a); });
            return m;
        }

        private static UIElement Calendar(DateTime now)
        {
            var ci = CultureInfo.InvariantCulture;
            var st = new StackPanel();
            var head = Label(now.ToString("MMMM", ci).ToUpperInvariant(), 12, true);
            head.SetResourceReference(TextBlock.ForegroundProperty, "Red");
            head.Margin = new Thickness(4, 0, 0, 8);
            st.Children.Add(head);
            var grid = new UniformGrid { Columns = 7 };
            foreach (var w in new[] { "S", "M", "T", "W", "T", "F", "S" })
            {
                var t = Label(w, 11, true, 0.5);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                grid.Children.Add(t);
            }
            var first = new DateTime(now.Year, now.Month, 1);
            for (int i = 0; i < (int)first.DayOfWeek; i++) grid.Children.Add(new Border());
            int days = DateTime.DaysInMonth(now.Year, now.Month);
            for (int dd = 1; dd <= days; dd++)
            {
                var t = Label(dd.ToString(), 12, dd == now.Day);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                var cell = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(1), Child = t };
                if (dd == now.Day) { cell.SetResourceReference(Border.BackgroundProperty, "Red"); t.Foreground = Brushes.White; }
                grid.Children.Add(cell);
            }
            st.Children.Add(grid);
            return st;
        }

        protected override void Place() { PlaceTopRight(); }
    }

    internal static class ThemeSwitch
    {
        public static void SetDark(bool dark)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            }
            UIntPtr r;
            Native.SendMessageTimeout(new IntPtr(0xFFFF), 0x1A, UIntPtr.Zero, "ImmersiveColorSet", 2, 1000, out r);
        }
    }
}
