using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook
{
    internal class AppEntry { public string Name, Path; }

    /// <summary>Every installed app (desktop and Store), read from the shell's Apps folder.</summary>
    internal static class AppCatalog
    {
        private static readonly object sync = new object();
        private static List<AppEntry> apps = new List<AppEntry>();
        public static bool Loaded { get; private set; }

        public static List<AppEntry> Apps { get { lock (sync) return apps; } }

        public static void Load()
        {
            var t = new Thread(() =>
            {
                var list = new List<AppEntry>();
                try
                {
                    Type st = Type.GetTypeFromProgID("Shell.Application");
                    dynamic shell = Activator.CreateInstance(st);
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    foreach (dynamic item in folder.Items())
                    {
                        string name = item.Name;
                        string path = item.Path;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                        if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new AppEntry { Name = name, Path = path });
                    }
                }
                catch (Exception ex) { Program.Log(ex); }
                list = list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                           .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
                lock (sync) apps = list;
                Loaded = true;
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        public static int Score(string name, string q)
        {
            if (q.Length == 0) return 1;
            if (name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 100;
            if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 80;
            foreach (var word in name.Split(' ', '-', '.', '(', ')'))
                if (word.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 60;
            string initials = new string(name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());
            if (q.Length >= 2 && initials.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 50;
            if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return 30;
            return 0;
        }

        public static void Launch(AppEntry a) { Launcher.Open("shell:AppsFolder\\" + a.Path); }
    }

    /// <summary>Full-screen app grid over a frosted copy of the desktop (the Mac stand-in for Start).</summary>
    internal class LaunchpadWindow : Window
    {
        private readonly Grid root;
        private readonly Grid content;
        private readonly TextBox search;
        private readonly TextBlock placeholder;
        private readonly WrapPanel grid;
        private readonly Dictionary<string, FrameworkElement> tiles = new Dictionary<string, FrameworkElement>();
        private List<AppEntry> visible = new List<AppEntry>();
        private DateTime lastHidden;

        public LaunchpadWindow()
        {
            Title = "Launchpad";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            FontFamily = new FontFamily(Theme.UiFont);
            Foreground = Brushes.White;

            root = new Grid { Background = Brushes.Transparent };
            content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition());

            // Search field: a frosted pill.
            var pill = new Border
            {
                Width = 260, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 46, 0, 30),
                Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var pg = new Grid();
            var glass = new TextBlock
            {
                Text = "", FontFamily = new FontFamily(Theme.IconFont), FontSize = 13, Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
            };
            search = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
                CaretBrush = Brushes.White, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(32, 0, 12, 0), FocusVisualStyle = null
            };
            placeholder = new TextBlock { Text = "Search", FontSize = 14, Opacity = 0.6, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(34, 0, 0, 0) };
            search.TextChanged += (s, e) => { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Filter(); };
            pg.Children.Add(glass);
            pg.Children.Add(search);
            pg.Children.Add(placeholder);
            pill.Child = pg;
            content.Children.Add(pill);

            grid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 1500 };
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = grid, Margin = new Thickness(60, 0, 60, 40), Focusable = false
            };
            Grid.SetRow(sv, 1);
            content.Children.Add(sv);
            root.Children.Add(content);
            Content = root;

            root.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource == root || e.OriginalSource == grid || e.OriginalSource is ScrollViewer || e.OriginalSource == content) Dismiss();
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
                else if (e.Key == Key.Enter && visible.Count > 0) { var a = visible[0]; Dismiss(); AppCatalog.Launch(a); e.Handled = true; }
            };
            Deactivated += (s, e) => Dismiss();
        }

        public void Toggle()
        {
            if (IsVisible) { Dismiss(); return; }
            if ((DateTime.Now - lastHidden).TotalMilliseconds < 300) return;
            Open();
        }

        private void Open()
        {
            var snap = ScreenCapture.Capture();
            var backdrop = GlassFx.Backdrop(snap, 6, 6, 0.32);
            Background = backdrop != null ? (Brush)new ImageBrush(backdrop) { Stretch = Stretch.Fill } : new SolidColorBrush(Color.FromArgb(255, 30, 30, 34));
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            search.Text = "";
            Filter();
            closing = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            search.Focus();
            Keyboard.Focus(search);

            content.Opacity = 0;
            var st = new ScaleTransform(1.08, 1.08);
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = st;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.08, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            if (!AppCatalog.Loaded) Launcher.Later(500, () => { if (IsVisible) Filter(); });
        }

        public void ResetTiles() { tiles.Clear(); if (IsVisible) Filter(); }

        private void TileMenu(AppEntry a)
        {
            string key = "shell:AppsFolder\\" + a.Path;
            var m = new ContextMenu();
            var open = new MenuItem { Header = "Open" };
            open.Click += (s, e) => { Dismiss(); AppCatalog.Launch(a); };
            var add = new MenuItem { Header = "Add to Dock" };
            add.Click += (s, e) =>
            {
                Config.Entries.Add(new DockEntry { Name = a.Name, Target = key });
                try { Config.SaveDock(); } catch (Exception ex) { Program.Log(ex); }
                Program.Dock.Reload();
            };
            var change = new MenuItem { Header = "Change Icon…" };
            change.Click += (s, e) => IconStyler.PickFor(key);
            m.Items.Add(open);
            m.Items.Add(new Separator());
            m.Items.Add(add);
            m.Items.Add(change);
            if (IconStyler.HasCustom(key))
            {
                var reset = new MenuItem { Header = "Reset Icon" };
                reset.Click += (s, e) => { IconStyler.ResetCustom(key); Program.IconsChanged(); };
                m.Items.Add(reset);
            }
            m.IsOpen = true;
        }

        private bool closing;

        /// <summary>Fade out while the grid zooms slightly toward you, like closing Launchpad on a Mac.</summary>
        private void Dismiss()
        {
            if (!IsVisible || closing) return;
            lastHidden = DateTime.Now;
            closing = true;
            var dur = TimeSpan.FromMilliseconds(180);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var st = new ScaleTransform(1, 1);
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = st;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.06, dur) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.06, dur) { EasingFunction = ease });
            var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                if (!closing) return;
                closing = false;
                Hide();
                Background = Brushes.Black;
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void Filter()
        {
            string q = search.Text.Trim();
            visible = AppCatalog.Apps
                .Select(a => new { a, s = AppCatalog.Score(a.Name, q) })
                .Where(x => x.s > 0)
                .OrderByDescending(x => q.Length == 0 ? 0 : x.s).ThenBy(x => x.a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.a).ToList();
            grid.Children.Clear();
            foreach (var a in visible) grid.Children.Add(Tile(a));
            if (!AppCatalog.Loaded && visible.Count == 0)
                grid.Children.Add(new TextBlock { Text = "Loading apps…", FontSize = 16, Opacity = 0.7, Margin = new Thickness(40) });
        }

        private FrameworkElement Tile(AppEntry a)
        {
            FrameworkElement existing;
            if (tiles.TryGetValue(a.Path, out existing)) return existing;

            var img = new Image { Width = 84, Height = 84 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            // Glare sweep on hover (idea from React Bits' GlareHover).
            var glareBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            glareBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35));
            glareBrush.GradientStops.Add(new GradientStop(Color.FromArgb(120, 255, 255, 255), 0.5));
            glareBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65));
            var shift = new TranslateTransform(-120, 0);
            var glare = new Rectangle { Width = 84, Height = 84, Fill = glareBrush, RenderTransform = shift, IsHitTestVisible = false };
            var iconBox = new Grid { Width = 84, Height = 84, ClipToBounds = true };
            iconBox.Clip = new RectangleGeometry(new Rect(0, 0, 84, 84), 20, 20);
            iconBox.Children.Add(img);
            iconBox.Children.Add(glare);

            var name = new TextBlock
            {
                Text = a.Name, FontSize = 13, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
            };
            var st = new StackPanel { Width = 150, Margin = new Thickness(8, 6, 8, 18), Background = Brushes.Transparent };
            st.Children.Add(iconBox);
            st.Children.Add(name);
            var scale = new ScaleTransform(1, 1);
            iconBox.RenderTransformOrigin = new Point(0.5, 0.5);
            iconBox.RenderTransform = scale;

            st.MouseEnter += (s, e) =>
            {
                var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.07, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.07, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-120, 120, TimeSpan.FromMilliseconds(550)));
            };
            st.MouseLeave += (s, e) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            };
            st.MouseLeftButtonDown += (s, e) => { iconBox.Opacity = 0.7; e.Handled = true; };
            st.MouseLeftButtonUp += (s, e) => { iconBox.Opacity = 1; e.Handled = true; Dismiss(); AppCatalog.Launch(a); };
            st.MouseRightButtonUp += (s, e) => { e.Handled = true; TileMenu(a); };

            // Icons load a few at a time so the grid appears instantly.
            Dispatcher.BeginInvoke(new Action(() => { img.Source = IconStyler.Get("shell:AppsFolder\\" + a.Path, 128); }), DispatcherPriority.Background);
            tiles[a.Path] = st;
            return st;
        }
    }
}
