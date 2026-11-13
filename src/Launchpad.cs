using System;
using System.Collections.Generic;
using System.IO;
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
    internal class AppEntry
    {
        public string Name, Path;
        public bool Custom;     // added by the user (an .exe / .lnk), not from the shell's Apps folder

        /// <summary>Shell target used to launch it and to look up its icon.</summary>
        public string Key { get { return Custom ? Path : "shell:AppsFolder\\" + Path; } }
        public string Id { get { return Key.ToLowerInvariant(); } }
    }

    /// <summary>Your Launchpad layout: pinned apps, hidden apps, custom order, renames, usage and extra apps.</summary>
    internal static class LaunchpadPrefs
    {
        private static readonly string FilePath = System.IO.Path.Combine(Config.Dir, "launchpad.txt");
        public static readonly HashSet<string> Hidden = new HashSet<string>();
        public static readonly List<string> Pinned = new List<string>();
        public static readonly List<string> Order = new List<string>();
        public static readonly Dictionary<string, string> Names = new Dictionary<string, string>();
        public static readonly Dictionary<string, int> Uses = new Dictionary<string, int>();
        public static readonly List<AppEntry> Customs = new List<AppEntry>();

        public static void Load()
        {
            Hidden.Clear(); Pinned.Clear(); Order.Clear(); Names.Clear(); Uses.Clear(); Customs.Clear();
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                switch (p[0])
                {
                    case "hide": Hidden.Add(p[1]); break;
                    case "pin": Pinned.Add(p[1]); break;
                    case "order": Order.Add(p[1]); break;
                    case "name": if (p.Length > 2) Names[p[1]] = p[2]; break;
                    case "uses": int n; if (p.Length > 2 && int.TryParse(p[2], out n)) Uses[p[1]] = n; break;
                    case "custom": if (p.Length > 2) Customs.Add(new AppEntry { Path = p[1], Name = p[2], Custom = true }); break;
                }
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                var lines = new List<string>();
                lines.AddRange(Hidden.Select(k => "hide\t" + k));
                lines.AddRange(Pinned.Select(k => "pin\t" + k));
                lines.AddRange(Order.Select(k => "order\t" + k));
                lines.AddRange(Names.Select(kv => "name\t" + kv.Key + "\t" + kv.Value));
                lines.AddRange(Uses.Select(kv => "uses\t" + kv.Key + "\t" + kv.Value));
                lines.AddRange(Customs.Select(c => "custom\t" + c.Path + "\t" + c.Name));
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static void Reset()
        {
            Hidden.Clear(); Pinned.Clear(); Order.Clear(); Names.Clear();
            Save();
        }
    }

    /// <summary>Every installed app (desktop and Store), read from the shell's Apps folder, plus ones you add.</summary>
    internal static class AppCatalog
    {
        private static readonly object sync = new object();
        private static List<AppEntry> apps = new List<AppEntry>();
        public static bool Loaded { get; private set; }

        public static List<AppEntry> Apps { get { lock (sync) return apps; } }

        /// <summary>Installed apps plus custom ones, hidden ones included.</summary>
        public static List<AppEntry> All { get { return Apps.Concat(LaunchpadPrefs.Customs).ToList(); } }

        public static string DisplayName(AppEntry a)
        {
            string n;
            return LaunchpadPrefs.Names.TryGetValue(a.Id, out n) ? n : a.Name;
        }

        public static void Load()
        {
            LaunchpadPrefs.Load();
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

        public static void Launch(AppEntry a)
        {
            int n;
            LaunchpadPrefs.Uses.TryGetValue(a.Id, out n);
            LaunchpadPrefs.Uses[a.Id] = n + 1;
            LaunchpadPrefs.Save();
            Launcher.Open(a.Key);
        }

        /// <summary>Visible apps in Launchpad order: pinned first, then by the chosen sort.</summary>
        public static void Arrange(out List<AppEntry> pinned, out List<AppEntry> rest)
        {
            var all = All.Where(a => !LaunchpadPrefs.Hidden.Contains(a.Id)).ToList();
            var byId = new Dictionary<string, AppEntry>();
            foreach (var a in all) byId[a.Id] = a;
            pinned = LaunchpadPrefs.Pinned.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var pinnedSet = new HashSet<string>(pinned.Select(a => a.Id));
            var others = all.Where(a => !pinnedSet.Contains(a.Id));
            switch (Settings.LaunchpadSort)
            {
                case "used":
                    rest = others.OrderByDescending(a => { int n; LaunchpadPrefs.Uses.TryGetValue(a.Id, out n); return n; })
                                 .ThenBy(a => DisplayName(a), StringComparer.OrdinalIgnoreCase).ToList();
                    break;
                case "custom":
                    var idx = new Dictionary<string, int>();
                    for (int i = 0; i < LaunchpadPrefs.Order.Count; i++) idx[LaunchpadPrefs.Order[i]] = i;
                    rest = others.OrderBy(a => { int i; return idx.TryGetValue(a.Id, out i) ? i : int.MaxValue; })
                                 .ThenBy(a => DisplayName(a), StringComparer.OrdinalIgnoreCase).ToList();
                    break;
                default:
                    rest = others.OrderBy(a => DisplayName(a), StringComparer.OrdinalIgnoreCase).ToList();
                    break;
            }
        }
    }

    /// <summary>Full-screen app grid over a frosted copy of the desktop (the Mac stand-in for Start).</summary>
    internal class LaunchpadWindow : Window
    {
        private readonly Grid root;
        private readonly Grid content;
        private readonly TextBox search;
        private readonly TextBlock placeholder;
        private readonly WrapPanel pinnedGrid, grid;
        private readonly TextBlock pinnedHeader, allHeader;
        private readonly Dictionary<string, FrameworkElement> tiles = new Dictionary<string, FrameworkElement>();
        private List<AppEntry> visible = new List<AppEntry>();
        private DateTime lastHidden;
        private bool closing;

        // drag to rearrange
        private AppEntry pressed;
        private Point pressPoint;
        private bool dragged;

        private double IconSize { get { return Settings.LaunchpadIconSize; } }

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
                Width = 260, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 46, 0, 26),
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

            pinnedHeader = SectionHeader("Pinned");
            allHeader = SectionHeader("All Apps");
            pinnedGrid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 1500, AllowDrop = true, Background = Brushes.Transparent, MinHeight = 20 };
            grid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 1500, AllowDrop = true, Background = Brushes.Transparent };
            pinnedGrid.Drop += (s, e) => { DropOn(e, null, true); };
            grid.Drop += (s, e) => { DropOn(e, null, false); };
            var stack = new StackPanel();
            stack.Children.Add(pinnedHeader);
            stack.Children.Add(pinnedGrid);
            stack.Children.Add(allHeader);
            stack.Children.Add(grid);
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack, Margin = new Thickness(60, 0, 60, 40), Focusable = false
            };
            Grid.SetRow(sv, 1);
            content.Children.Add(sv);
            root.Children.Add(content);
            Content = root;

            root.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource == root || e.OriginalSource == grid || e.OriginalSource == pinnedGrid || e.OriginalSource is ScrollViewer || e.OriginalSource == content || e.OriginalSource == stack) Dismiss();
            };
            root.MouseRightButtonUp += (s, e) =>
            {
                if (e.OriginalSource == root || e.OriginalSource == grid || e.OriginalSource == stack || e.OriginalSource is ScrollViewer) BackgroundMenu();
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
                else if (e.Key == Key.Enter && visible.Count > 0) { var a = visible[0]; Dismiss(); AppCatalog.Launch(a); e.Handled = true; }
            };
            Deactivated += (s, e) => { if (!busy) Dismiss(); };
        }

        private bool busy;   // true while a dialog of ours is open on top of Launchpad

        private static TextBlock SectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10)
            };
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
            if (!AppCatalog.Loaded) Launcher.Later(600, () => { if (IsVisible) Filter(); });
        }

        public void ResetTiles() { tiles.Clear(); if (IsVisible) Filter(); }

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

        public void Refresh() { if (IsVisible) Filter(); }

        private void Filter()
        {
            string q = search.Text.Trim();
            pinnedGrid.Children.Clear();
            grid.Children.Clear();
            if (q.Length > 0)
            {
                visible = AppCatalog.All
                    .Where(a => !LaunchpadPrefs.Hidden.Contains(a.Id))
                    .Select(a => new { a, s = AppCatalog.Score(AppCatalog.DisplayName(a), q) })
                    .Where(x => x.s > 0)
                    .OrderByDescending(x => x.s).ThenBy(x => AppCatalog.DisplayName(x.a), StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.a).ToList();
                pinnedHeader.Visibility = Visibility.Collapsed;
                pinnedGrid.Visibility = Visibility.Collapsed;
                allHeader.Visibility = Visibility.Collapsed;
                foreach (var a in visible) grid.Children.Add(Tile(a, false));
            }
            else
            {
                List<AppEntry> pinned, rest;
                AppCatalog.Arrange(out pinned, out rest);
                visible = pinned.Concat(rest).ToList();
                bool hasPins = pinned.Count > 0;
                pinnedHeader.Visibility = hasPins ? Visibility.Visible : Visibility.Collapsed;
                pinnedGrid.Visibility = hasPins ? Visibility.Visible : Visibility.Collapsed;
                allHeader.Visibility = hasPins ? Visibility.Visible : Visibility.Collapsed;
                foreach (var a in pinned) pinnedGrid.Children.Add(Tile(a, true));
                foreach (var a in rest) grid.Children.Add(Tile(a, false));
            }
            if (!AppCatalog.Loaded && visible.Count == 0)
                grid.Children.Add(new TextBlock { Text = "Loading apps…", FontSize = 16, Opacity = 0.7, Margin = new Thickness(40) });
        }

        private FrameworkElement Tile(AppEntry a, bool inPinned)
        {
            string cacheKey = a.Id + "|" + IconSize;
            FrameworkElement existing;
            if (tiles.TryGetValue(cacheKey, out existing))
            {
                ((TextBlock)((StackPanel)existing).Children[1]).Text = AppCatalog.DisplayName(a);
                existing.Tag = inPinned;
                return existing;
            }

            double size = IconSize;
            var img = new Image { Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            // Glare sweep on hover (idea from React Bits' GlareHover).
            var glareBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            glareBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35));
            glareBrush.GradientStops.Add(new GradientStop(Color.FromArgb(120, 255, 255, 255), 0.5));
            glareBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65));
            var shift = new TranslateTransform(-size * 1.4, 0);
            var glare = new Rectangle { Width = size, Height = size, Fill = glareBrush, RenderTransform = shift, IsHitTestVisible = false };
            var iconBox = new Grid { Width = size, Height = size, ClipToBounds = true };
            iconBox.Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.24, size * 0.24);
            iconBox.Children.Add(img);
            iconBox.Children.Add(glare);

            var name = new TextBlock
            {
                Text = AppCatalog.DisplayName(a), FontSize = 13, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = size + 46, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
            };
            var st = new StackPanel { Width = size + 66, Margin = new Thickness(8, 6, 8, 18), Background = Brushes.Transparent, AllowDrop = true, Tag = inPinned };
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
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-size * 1.4, size * 1.4, TimeSpan.FromMilliseconds(550)));
            };
            st.MouseLeave += (s, e) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            };
            st.MouseLeftButtonDown += (s, e) => { iconBox.Opacity = 0.7; pressed = a; pressPoint = e.GetPosition(this); dragged = false; e.Handled = true; };
            st.MouseMove += (s, e) =>
            {
                if (pressed != a || e.LeftButton != MouseButtonState.Pressed || dragged || search.Text.Length > 0) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - pressPoint.X) < 8 && Math.Abs(p.Y - pressPoint.Y) < 8) return;
                dragged = true;
                iconBox.Opacity = 0.35;
                DragDrop.DoDragDrop(st, a.Id, DragDropEffects.Move);
                iconBox.Opacity = 1;
                pressed = null;
            };
            st.MouseLeftButtonUp += (s, e) =>
            {
                iconBox.Opacity = 1;
                e.Handled = true;
                if (dragged || pressed != a) { pressed = null; return; }
                pressed = null;
                Dismiss();
                AppCatalog.Launch(a);
            };
            st.MouseRightButtonUp += (s, e) => { e.Handled = true; TileMenu(a); };
            st.Drop += (s, e) => { e.Handled = true; DropOn(e, a, (bool)st.Tag); };

            // Icons load a few at a time so the grid appears instantly.
            Dispatcher.BeginInvoke(new Action(() => { img.Source = IconStyler.Get(a.Key, 128); }), DispatcherPriority.Background);
            tiles[cacheKey] = st;
            return st;
        }

        /// <summary>Drop an app onto another app (or an empty area) to move it there.</summary>
        private void DropOn(DragEventArgs e, AppEntry target, bool intoPinned)
        {
            var id = e.Data.GetData(typeof(string)) as string;
            if (id == null || (target != null && target.Id == id)) return;
            List<AppEntry> pinned, rest;
            AppCatalog.Arrange(out pinned, out rest);

            LaunchpadPrefs.Pinned.Remove(id);
            if (intoPinned)
            {
                int at = target == null ? LaunchpadPrefs.Pinned.Count : Math.Max(0, LaunchpadPrefs.Pinned.IndexOf(target.Id));
                LaunchpadPrefs.Pinned.Insert(Math.Min(at, LaunchpadPrefs.Pinned.Count), id);
            }
            else
            {
                // Dragging inside "All Apps" switches to your own order.
                var order = rest.Select(a => a.Id).Where(x => x != id).ToList();
                int at = target == null ? order.Count : Math.Max(0, order.IndexOf(target.Id));
                order.Insert(Math.Min(at, order.Count), id);
                LaunchpadPrefs.Order.Clear();
                LaunchpadPrefs.Order.AddRange(order);
                if (Settings.LaunchpadSort != "custom") { Settings.LaunchpadSort = "custom"; Settings.Save(); }
            }
            LaunchpadPrefs.Save();
            Filter();
        }

        private static MenuItem Item(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => a();
            return mi;
        }

        private void TileMenu(AppEntry a)
        {
            bool isPinned = LaunchpadPrefs.Pinned.Contains(a.Id);
            var m = new ContextMenu();
            m.Items.Add(Item("Open", () => { Dismiss(); AppCatalog.Launch(a); }));
            m.Items.Add(new Separator());
            m.Items.Add(Item(isPinned ? "Unpin from Top" : "Pin to Top", () =>
            {
                if (isPinned) LaunchpadPrefs.Pinned.Remove(a.Id); else LaunchpadPrefs.Pinned.Add(a.Id);
                LaunchpadPrefs.Save();
                Filter();
            }));
            m.Items.Add(Item("Add to Dock", () =>
            {
                Config.Entries.Add(new DockEntry { Name = AppCatalog.DisplayName(a), Target = a.Key });
                try { Config.SaveDock(); } catch (Exception ex) { Program.Log(ex); }
                Program.Dock.Reload();
            }));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Rename…", () =>
            {
                busy = true;
                var n = Prompt.Ask("Rename", "New name for “" + AppCatalog.DisplayName(a) + "” in Launchpad:", AppCatalog.DisplayName(a));
                busy = false;
                if (n == null) return;
                if (n == a.Name) LaunchpadPrefs.Names.Remove(a.Id); else LaunchpadPrefs.Names[a.Id] = n;
                LaunchpadPrefs.Save();
                Filter();
            }));
            m.Items.Add(Item("Change Icon…", () => { busy = true; IconStyler.PickFor(a.Key); busy = false; }));
            if (IconStyler.HasCustom(a.Key)) m.Items.Add(Item("Reset Icon", () => { IconStyler.ResetCustom(a.Key); Program.IconsChanged(); }));
            m.Items.Add(new Separator());
            if (a.Custom)
                m.Items.Add(Item("Remove from Launchpad", () =>
                {
                    LaunchpadPrefs.Customs.RemoveAll(c => c.Id == a.Id);
                    LaunchpadPrefs.Pinned.Remove(a.Id);
                    LaunchpadPrefs.Save();
                    Filter();
                }));
            else
                m.Items.Add(Item("Hide from Launchpad", () =>
                {
                    LaunchpadPrefs.Hidden.Add(a.Id);
                    LaunchpadPrefs.Pinned.Remove(a.Id);
                    LaunchpadPrefs.Save();
                    Filter();
                }));
            m.IsOpen = true;
        }

        private void BackgroundMenu()
        {
            var m = new ContextMenu();
            m.Items.Add(Item("Add an App…", () => { busy = true; AddCustomApp(); busy = false; Filter(); }));
            m.Items.Add(Item("Launchpad Settings…", () => { Dismiss(); Program.SettingsWin.Open("Launchpad"); }));
            m.IsOpen = true;
        }

        /// <summary>Add any .exe / shortcut to Launchpad (for apps Windows doesn't list).</summary>
        public static bool AddCustomApp()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add to Launchpad",
                Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return false;
            var a = new AppEntry { Path = dlg.FileName, Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Custom = true };
            LaunchpadPrefs.Customs.RemoveAll(c => c.Id == a.Id);
            LaunchpadPrefs.Customs.Add(a);
            LaunchpadPrefs.Hidden.Remove(a.Id);
            LaunchpadPrefs.Save();
            return true;
        }
    }
}
