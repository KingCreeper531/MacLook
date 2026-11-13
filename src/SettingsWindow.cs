using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>Mac-style on/off switch.</summary>
    internal class MacToggle : Border
    {
        private readonly TranslateTransform knobPos = new TranslateTransform();
        private bool on;
        public event Action<bool> Changed;

        public MacToggle(bool value)
        {
            Width = 40; Height = 24;
            CornerRadius = new CornerRadius(12);
            Cursor = Cursors.Hand;
            Child = new Ellipse
            {
                Width = 20, Height = 20, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 0, 0), RenderTransform = knobPos,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.3, Direction = 270 }
            };
            Set(value, false);
            MouseLeftButtonUp += (s, e) =>
            {
                if (!IsEnabled) return;
                Set(!on, true);
                if (Changed != null) Changed(on);
                e.Handled = true;
            };
            IsEnabledChanged += (s, e) => Opacity = IsEnabled ? 1 : 0.4;
        }

        private void Set(bool v, bool animate)
        {
            on = v;
            SetResourceReference(BackgroundProperty, v ? "Accent" : "Inactive");
            double x = v ? 16 : 0;
            if (animate)
                knobPos.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
            else { knobPos.BeginAnimation(TranslateTransform.XProperty, null); knobPos.X = x; }
        }
    }

    /// <summary>A System Settings-style window, itself made of liquid glass.</summary>
    internal class SettingsWindow : Window
    {
        private static readonly string[][] Sections =
        {
            new[] { "General", "", "#8E8E93" },
            new[] { "Dock", "", "#3478F6" },
            new[] { "Menu Bar", "", "#5856D6" },
            new[] { "Appearance", "", "#FF9500" },
            new[] { "Glass", "", "#30B0C7" },
            new[] { "Launchpad", "", "#AF52DE" },
            new[] { "Hot Corners & Keys", "", "#34C759" },
            new[] { "Icons & Windows", "", "#FF2D55" },
            new[] { "About", "", "#8E8E93" },
        };

        private readonly Border glass;
        private readonly StackPanel sidebar = new StackPanel();
        private readonly StackPanel page = new StackPanel();
        private readonly TextBlock pageTitle;
        private string current = "General";
        private Pix snap;

        public SettingsWindow()
        {
            Title = "Mac Look Settings";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Width = 860; Height = 640;
            ShowInTaskbar = true;
            UseLayoutRounding = true;
            FontFamily = new FontFamily(Theme.UiFont);
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "MenuFg");

            glass = new Border { CornerRadius = new CornerRadius(18) };
            glass.SetResourceReference(Border.BackgroundProperty, "MenuBg");
            var shadowed = new Grid { Margin = new Thickness(24, 16, 24, 32) };
            shadowed.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
                Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.4 }
            });
            shadowed.Children.Add(glass);

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());

            // Sidebar
            var side = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(8), Padding = new Thickness(8, 12, 8, 8) };
            side.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
            var sideStack = new StackPanel();
            sideStack.Children.Add(TrafficLights());
            sideStack.Children.Add(sidebar);
            side.Child = sideStack;
            layout.Children.Add(side);

            // Content
            var right = new Grid { Margin = new Thickness(10, 14, 18, 14) };
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition());
            pageTitle = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 2, 0, 14) };
            right.Children.Add(pageTitle);
            var sv = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            Grid.SetRow(sv, 1);
            right.Children.Add(sv);
            Grid.SetColumn(right, 1);
            layout.Children.Add(right);

            glass.Child = layout;
            Content = shadowed;

            // Drag anywhere on empty glass; re-photograph the background where we land.
            glass.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is TextBox) return;
                try { DragMove(); } catch { }
                Recapture();
            };
            Closing += (s, e) => { e.Cancel = true; FadeOut(); };
        }

        private UIElement TrafficLights()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 18) };
            row.Children.Add(Light("#FF5F57", "", FadeOut));
            row.Children.Add(Light("#FEBC2E", "", () => WindowState = WindowState.Minimized));
            row.Children.Add(Light("#28C840", "", () =>
            {
                Height = Height < 700 ? Math.Min(SystemParameters.WorkArea.Height - 40, 900) : 640;
                Recapture();
            }));
            return row;
        }

        private static UIElement Light(string color, string glyph, Action a)
        {
            var sym = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(Theme.IconFont), FontSize = 7, Opacity = 0,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var b = new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Child = sym, Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => sym.Opacity = 1;
            b.MouseLeave += (s, e) => sym.Opacity = 0;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; a(); };
            return b;
        }

        public void Open(string section)
        {
            current = section;
            BuildSidebar();
            BuildPage();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible)
            {
                snap = ScreenCapture.Capture();
                Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
                Top = Math.Max(40, (SystemParameters.PrimaryScreenHeight - Height) / 2);
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Show();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            }
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            Dispatcher.BeginInvoke(new Action(ApplyGlass), DispatcherPriority.Loaded);
        }

        private void FadeOut()
        {
            var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
            a.Completed += (s, e) => { Hide(); BeginAnimation(OpacityProperty, null); Opacity = 1; };
            BeginAnimation(OpacityProperty, a);
        }

        private void ApplyGlass()
        {
            if (snap != null && IsVisible) GlassFx.Apply(glass, snap, GlassStyles.Window);
        }

        /// <summary>Hide for a blink, photograph what's behind, come back with fresh glass.</summary>
        public void Recapture()
        {
            if (!IsVisible) return;
            Opacity = 0;
            Launcher.Later(70, () =>
            {
                snap = ScreenCapture.Capture();
                Opacity = 1;
                Dispatcher.BeginInvoke(new Action(ApplyGlass), DispatcherPriority.Loaded);
            });
        }

        private void BuildSidebar()
        {
            sidebar.Children.Clear();
            foreach (var s in Sections)
            {
                string name = s[0];
                var icon = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 9, 0),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s[2])),
                    Child = new TextBlock
                    {
                        Text = s[1], FontFamily = new FontFamily(Theme.IconFont), FontSize = 12, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                var row = new DockPanel();
                row.Children.Add(icon);
                var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(label);
                var item = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 1, 0, 1), Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand };
                if (name == current) { item.SetResourceReference(Border.BackgroundProperty, "Accent"); label.Foreground = Brushes.White; }
                item.MouseLeftButtonDown += (o, e) => e.Handled = true;
                item.MouseLeftButtonUp += (o, e) => { e.Handled = true; current = name; BuildSidebar(); BuildPage(); };
                sidebar.Children.Add(item);
            }
        }

        // ------------------------------------------------------------------ building blocks
        private static void Changed() { Program.ApplySettingsSoon(); }

        private static Border Group(params UIElement[] rows)
        {
            var st = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null) continue;
                if (st.Children.Count > 0)
                {
                    var sep = new Border { Height = 1, Margin = new Thickness(14, 0, 14, 0) };
                    sep.SetResourceReference(Border.BackgroundProperty, "MenuSep");
                    st.Children.Add(sep);
                }
                st.Children.Add(rows[i]);
            }
            var b = new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 14), Child = st };
            b.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
            return b;
        }

        private static UIElement Row(string title, string sub, UIElement control)
        {
            var g = new DockPanel { Margin = new Thickness(14, 10, 14, 10), LastChildFill = true };
            if (control != null)
            {
                DockPanel.SetDock(control, Dock.Right);
                var fe = control as FrameworkElement;
                if (fe != null) fe.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(control);
            }
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            t.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(sub)) t.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Opacity = 0.55, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(t);
            return g;
        }

        private static MacToggle Toggle(bool value, Action<bool> set)
        {
            var t = new MacToggle(value);
            t.Changed += v => { set(v); Changed(); };
            return t;
        }

        private static UIElement Slider(double min, double max, double value, string fmt, Action<double> set)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var s = new ElasticSlider("") { Width = 210, Height = 22 };
            var txt = new TextBlock { Width = 52, TextAlignment = TextAlignment.Right, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            Action<double> show = v => txt.Text = v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            s.Value = (value - min) / (max - min);
            show(value);
            s.ValueChanged += f => { double v = min + f * (max - min); show(v); set(v); Changed(); };
            row.Children.Add(s);
            row.Children.Add(txt);
            return row;
        }

        private static UIElement Segmented(string[] labels, string[] values, string current, Action<string> set)
        {
            var host = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
            host.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            var st = new StackPanel { Orientation = Orientation.Horizontal };
            var segs = new List<Border>();
            for (int i = 0; i < labels.Length; i++)
            {
                string v = values[i];
                var seg = new Border
                {
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 4, 14, 5), Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = labels[i], FontSize = 12 }, Tag = v, Background = Brushes.Transparent
                };
                segs.Add(seg);
                seg.MouseLeftButtonDown += (o, e) => e.Handled = true;
                seg.MouseLeftButtonUp += (o, e) =>
                {
                    e.Handled = true;
                    foreach (var x in segs) x.Background = Brushes.Transparent;
                    seg.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
                    set(v);
                    Changed();
                };
                if (v == current) seg.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
                st.Children.Add(seg);
            }
            host.Child = st;
            return host;
        }

        private static Border Button(string text, Action a)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock { Text = text, FontSize = 12.5 }, Cursor = Cursors.Hand
            };
            b.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            b.MouseLeftButtonDown += (o, e) => { b.Opacity = 0.6; e.Handled = true; };
            b.MouseLeave += (o, e) => b.Opacity = 1;
            b.MouseLeftButtonUp += (o, e) => { b.Opacity = 1; e.Handled = true; a(); };
            return b;
        }

        private static UIElement Buttons(params Border[] bs)
        {
            var st = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in bs) st.Children.Add(b);
            return st;
        }

        private static UIElement Caption(string text)
        {
            return new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.6, Margin = new Thickness(6, 0, 0, 6) };
        }

        // ------------------------------------------------------------------ pages
        private void BuildPage()
        {
            pageTitle.Text = current;
            page.Children.Clear();
            switch (current)
            {
                case "General": General(); break;
                case "Dock": DockPage(); break;
                case "Menu Bar": MenuBarPage(); break;
                case "Appearance": Appearance(); break;
                case "Glass": GlassPage(); break;
                case "Icons & Windows": IconsPage(); break;
                case "Launchpad": LaunchpadPage(); break;
                case "Hot Corners & Keys": HotCornersPage(); break;
                default: About(); break;
            }
        }

        private void General()
        {
            bool login = false;
            try { login = Startup.Enabled; } catch { }
            page.Children.Add(Group(
                Row("Hide the Windows taskbar", "Your taskbar comes back when you quit Mac Look.", Toggle(Settings.HideTaskbar, v =>
                {
                    Settings.HideTaskbar = v;
                    if (v) Taskbar.Hide(); else Taskbar.Restore();
                })),
                Row("Windows key opens Launchpad", "Shortcuts like Win+E and Win+D keep working.", Toggle(Settings.WinKeyLaunchpad, v => Settings.WinKeyLaunchpad = v)),
                Row("Open Mac Look at login", null, Toggle(login, v => { try { Startup.Enabled = v; } catch { } }))));
            page.Children.Add(Group(
                Row("Keyboard shortcuts", "Spotlight: Alt+Space    Mission Control: Ctrl+Alt+Up    Launchpad: Windows key", null)));
            page.Children.Add(Group(
                Row("Install updates automatically", "Mac Look checks GitHub for new versions and updates itself.", Toggle(Settings.AutoUpdate, v => Settings.AutoUpdate = v)),
                Row("Version " + Updater.CurrentText, null, Buttons(Button("Check for Updates", () => Updater.Check(true)), Button("GitHub", () => Launcher.Open(Updater.RepoUrl))))));
            page.Children.Add(Group(
                Row("Quit Mac Look", "Restores the Windows taskbar.", Button("Quit", Program.Quit))));
        }

        private void DockPage()
        {
            page.Children.Add(Group(
                Row("Size", null, Slider(32, 96, Settings.IconSize, "0", v => Settings.IconSize = (int)Math.Round(v))),
                Row("Magnification", null, Toggle(Settings.Magnify, v => Settings.Magnify = v)),
                Row("Magnification amount", null, Slider(1.0, 2.5, Settings.MagnifyScale, "0.00x", v => Settings.MagnifyScale = v)),
                Row("Keep magnified icons inside the Dock", "Off: icons pop out above the glass, like on a Mac.", Toggle(Settings.MagnifyInside, v => Settings.MagnifyInside = v)),
                Row("Corner roundness", null, Slider(4, 40, Settings.DockRadius, "0", v => Settings.DockRadius = (int)Math.Round(v)))));

            MacToggle reserve = null;
            reserve = Toggle(Settings.ReserveDockSpace, v => Settings.ReserveDockSpace = v);
            reserve.IsEnabled = !Settings.DockAutoHide;
            page.Children.Add(Group(
                Row("Automatically hide and show the Dock", "The Dock floats over windows and pops up when your mouse touches the bottom of the screen.", Toggle(Settings.DockAutoHide, v => { Settings.DockAutoHide = v; reserve.IsEnabled = !v; })),
                Row("Keep the Dock up when nothing covers it", "With auto-hide on, the Dock only hides once a window overlaps it.", Toggle(Settings.DockShowWhenClear, v => Settings.DockShowWhenClear = v)),
                Row("Keep windows above the Dock", "Only when the Dock is always shown.", reserve),
                Row("Show open apps that aren't in the Dock", null, Toggle(Settings.ShowRunning, v => Settings.ShowRunning = v)),
                Row("Show indicators for open apps", null, Toggle(Settings.ShowIndicators, v => Settings.ShowIndicators = v)),
                Row("Animate opening apps", "Icons bounce while an app starts.", Toggle(Settings.BounceOnLaunch, v => Settings.BounceOnLaunch = v))));

            page.Children.Add(Caption("Apps in the Dock"));
            page.Children.Add(DockEditor());
        }

        private UIElement DockEditor()
        {
            var list = new StackPanel { Margin = new Thickness(6) };
            int selected = -1;
            Action refresh = null;
            refresh = () =>
            {
                list.Children.Clear();
                for (int i = 0; i < Config.Entries.Count; i++)
                {
                    var e = Config.Entries[i];
                    int index = i;
                    var icon = new Image
                    {
                        Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0),
                        Source = IconStyler.Get(e.Target, 64)
                    };
                    RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                    var name = new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center };
                    var row = new DockPanel();
                    row.Children.Add(icon);
                    row.Children.Add(name);
                    var item = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Child = row, Background = Brushes.Transparent };
                    if (index == selected) { item.SetResourceReference(Border.BackgroundProperty, "Accent"); name.Foreground = Brushes.White; }
                    item.MouseLeftButtonDown += (o, ev) => ev.Handled = true;
                    item.MouseLeftButtonUp += (o, ev) => { ev.Handled = true; selected = index; refresh(); };
                    list.Children.Add(item);
                }
            };
            Action save = () =>
            {
                try { Config.SaveDock(); } catch (Exception ex) { Program.Log(ex); }
                Program.Dock.Reload();
                refresh();
            };
            Action<int> move = d =>
            {
                int to = selected + d;
                if (selected < 0 || to < 0 || to >= Config.Entries.Count) return;
                var e = Config.Entries[selected];
                Config.Entries.RemoveAt(selected);
                Config.Entries.Insert(to, e);
                selected = to;
                save();
            };
            refresh();

            var tools = Buttons(
                Button("Add from Apps…", () =>
                {
                    var app = AppPicker.Pick();
                    if (app == null) return;
                    Config.Entries.Add(new DockEntry { Name = AppCatalog.DisplayName(app), Target = app.Key });
                    selected = Config.Entries.Count - 1;
                    save();
                    Recapture();
                }),
                Button("Add File…", () =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "Add to Dock",
                        Filter = "Apps and shortcuts|*.exe;*.lnk;*.url|All files|*.*",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                    };
                    if (dlg.ShowDialog(this) != true) return;
                    var entry = new DockEntry { Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Target = dlg.FileName };
                    if (dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) entry.Match.Add(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName).ToLowerInvariant());
                    Config.Entries.Add(entry);
                    selected = Config.Entries.Count - 1;
                    save();
                    Recapture();
                }),
                Button("Remove", () =>
                {
                    if (selected < 0 || selected >= Config.Entries.Count) return;
                    Config.Entries.RemoveAt(selected);
                    selected = Math.Min(selected, Config.Entries.Count - 1);
                    save();
                }),
                Button("Rename…", () =>
                {
                    if (selected < 0 || selected >= Config.Entries.Count) return;
                    var e = Config.Entries[selected];
                    var n = Prompt.Ask("Rename", "New name for “" + e.Name + "” in the Dock:", e.Name);
                    if (n == null) return;
                    e.Name = n;
                    save();
                }),
                Button("Move Up", () => move(-1)),
                Button("Move Down", () => move(1)));

            var st = new StackPanel();
            st.Children.Add(list);
            var toolRow = new Border { Padding = new Thickness(8, 4, 14, 12), Child = tools };
            st.Children.Add(toolRow);
            var b = new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 14), Child = st };
            b.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
            return b;
        }

        private void MenuBarPage()
        {
            page.Children.Add(Group(
                Row("Show app menus", "File, Edit, View, Window and Help.", Toggle(Settings.ShowAppMenus, v => Settings.ShowAppMenus = v)),
                Row("Show Wi-Fi", null, Toggle(Settings.ShowWifi, v => Settings.ShowWifi = v)),
                Row("Show Sound", null, Toggle(Settings.ShowSound, v => Settings.ShowSound = v)),
                Row("Show Spotlight", null, Toggle(Settings.ShowSearch, v => Settings.ShowSearch = v))));
            page.Children.Add(Caption("Clock"));
            page.Children.Add(Group(
                Row("Show the day of the week", null, Toggle(Settings.ClockDay, v => Settings.ClockDay = v)),
                Row("Show the date", null, Toggle(Settings.ClockDate, v => Settings.ClockDate = v)),
                Row("Use a 24-hour clock", null, Toggle(Settings.Clock24h, v => Settings.Clock24h = v)),
                Row("Display the time with seconds", null, Toggle(Settings.ClockSeconds, v => Settings.ClockSeconds = v))));
        }

        private void Appearance()
        {
            page.Children.Add(Group(
                Row("Appearance", "Auto follows your Windows light/dark setting.",
                    Segmented(new[] { "Auto", "Light", "Dark" }, new[] { "auto", "light", "dark" }, Settings.ThemeMode, v => Settings.ThemeMode = v)),
                Row("Accent color", null, Swatches())));
            page.Children.Add(Group(
                Row("Wallpaper", "An original rolling-hills wallpaper drawn by Mac Look.", Buttons(
                    Button("Use Mac-Style", () => { try { Wallpaper.SetMacStyle(); Program.RecaptureDesktop(); } catch (Exception ex) { Program.Log(ex); } }),
                    Button("Restore Original", () => { Wallpaper.Restore(); Program.RecaptureDesktop(); })))));
        }

        private UIElement Swatches()
        {
            var st = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var a in Theme.Accents)
            {
                string name = a[0];
                var c = (Color)ColorConverter.ConvertFromString(a[1]);
                var dot = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(c) };
                var ring = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(2, 0, 2, 0),
                    BorderThickness = new Thickness(2), Child = dot, Cursor = Cursors.Hand, ToolTip = name,
                    BorderBrush = name == Settings.Accent ? (Brush)new SolidColorBrush(c) : Brushes.Transparent, Background = Brushes.Transparent
                };
                ring.MouseLeftButtonDown += (o, e) => e.Handled = true;
                ring.MouseLeftButtonUp += (o, e) =>
                {
                    e.Handled = true;
                    Settings.Accent = name;
                    Changed();
                    Launcher.Later(150, BuildPage);
                };
                st.Children.Add(ring);
            }
            return st;
        }

        private void GlassPage()
        {
            page.Children.Add(Group(
                Row("Frost", "How blurry things behind the glass look.", Slider(0, 2, Settings.GlassFrost, "0.00", v => Settings.GlassFrost = v)),
                Row("Tint", "Higher is easier to read, lower is clearer glass.", Slider(0, 1.6, Settings.GlassTint, "0.00", v => Settings.GlassTint = v)),
                Row("Refraction", "How strongly the curved edges bend the background.", Slider(0, 2.5, Settings.GlassRefraction, "0.00", v => Settings.GlassRefraction = v)),
                Row("Shine", "Brightness of the light catching the edges.", Slider(0, 2, Settings.GlassShine, "0.00", v => Settings.GlassShine = v))));
            page.Children.Add(Group(
                Row("Refresh glass", "Re-photographs the desktop behind the menu bar and Dock.", Button("Refresh", () => { Program.RecaptureDesktop(); Recapture(); })),
                Row("Reset glass", null, Button("Reset", () =>
                {
                    Settings.GlassFrost = Settings.GlassTint = Settings.GlassRefraction = Settings.GlassShine = 1.0;
                    Changed();
                    BuildPage();
                }))));
        }

        private void IconsPage()
        {
            page.Children.Add(Group(
                Row("App icon style", "Glass puts every icon on a glossy rounded plate, like macOS. Original shows Windows icons as they are.",
                    Segmented(new[] { "Original", "Glass" }, new[] { "original", "glass" }, Settings.IconStyle, v => Settings.IconStyle = v)),
                Row("Custom icons", "Download a PNG (for example from macosicons.com), then right-click an app in the Dock or Launchpad and choose Change Icon…",
                    Buttons(
                        Button("Open macosicons.com", () => Launcher.Open("https://macosicons.com/")),
                        Button("Reset All", () => { IconStyler.ResetAll(); Program.IconsChanged(); })))));
            page.Children.Add(Group(
                Row("Window frames", "Frosted title bars, rounded corners and a soft border on every app window, including File Explorer.",
                    Segmented(new[] { "Off", "Mica", "Glass" }, new[] { "off", "mica", "acrylic" }, Settings.WindowFrames, v => Settings.WindowFrames = v)),
                Row("Animate windows", "Genie effect when a window minimizes into the Dock, and a fade when it closes.",
                    Toggle(Settings.AnimateWindows, v => Settings.AnimateWindows = v)),
                Row("Fully see-through File Explorer", "Mac Look can't safely change what's inside other apps' windows. Windhawk's \"Translucent Windows\" mod does that; see the Windhawk app.",
                    Button("Open Windhawk", () => Launcher.Open("windhawk:")))));
        }
        private void LaunchpadPage()
        {
            page.Children.Add(Group(
                Row("Arrange apps", "Drag apps in Launchpad to put them in your own order.",
                    Segmented(new[] { "A–Z", "Most Used", "My Order" }, new[] { "az", "used", "custom" }, Settings.LaunchpadSort, v => Settings.LaunchpadSort = v)),
                Row("Icon size", null, Slider(56, 128, Settings.LaunchpadIconSize, "0", v => Settings.LaunchpadIconSize = (int)Math.Round(v))),
                Row("Windows key opens Launchpad", "Shortcuts like Win+E and Win+D keep working.", Toggle(Settings.WinKeyLaunchpad, v => Settings.WinKeyLaunchpad = v))));
            page.Children.Add(Group(
                Row("Tips", "Right-click an app in Launchpad to pin it to the top, rename it, change its icon, add it to the Dock or hide it. Drag apps to move them.", null),
                Row("Add an app Windows doesn't list", null, Button("Add App…", () => { if (LaunchpadWindow.AddCustomApp()) { Program.Launchpad.ResetTiles(); BuildPage(); } })),
                Row("Reset Launchpad layout", "Unhides everything and clears pins, renames and your custom order.", Button("Reset", () =>
                {
                    LaunchpadPrefs.Reset();
                    Settings.LaunchpadSort = "az";
                    Changed();
                    BuildPage();
                }))));

            var hidden = AppCatalog.All.Where(a => LaunchpadPrefs.Hidden.Contains(a.Id)).ToList();
            page.Children.Add(Caption("Hidden apps"));
            if (hidden.Count == 0)
            {
                page.Children.Add(Group(Row("No hidden apps", "Right-click an app in Launchpad and choose Hide from Launchpad.", null)));
                return;
            }
            page.Children.Add(Group(hidden.Select(a => Row(AppCatalog.DisplayName(a), null, Button("Show", () =>
            {
                LaunchpadPrefs.Hidden.Remove(a.Id);
                LaunchpadPrefs.Save();
                Program.Launchpad.ResetTiles();
                BuildPage();
            }))).ToArray()));
        }

        private UIElement CornerPicker(string current, Action<string> set)
        {
            var b = Button(HotCorners.Label(current) + "  ▾", () => { });
            b.MouseLeftButtonUp += (o, e) =>
            {
                var m = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
                foreach (var a in HotCorners.Actions)
                {
                    string key = a[0];
                    var mi = new MenuItem { Header = a[1], IsChecked = key == current };
                    mi.Click += (s2, e2) => { set(key); Changed(); BuildPage(); };
                    m.Items.Add(mi);
                }
                m.IsOpen = true;
            };
            return b;
        }

        private void HotCornersPage()
        {
            page.Children.Add(Caption("Hot Corners: push the pointer into a corner of the screen"));
            page.Children.Add(Group(
                Row("Top-left corner", null, CornerPicker(Settings.HotTopLeft, v => Settings.HotTopLeft = v)),
                Row("Top-right corner", null, CornerPicker(Settings.HotTopRight, v => Settings.HotTopRight = v)),
                Row("Bottom-left corner", null, CornerPicker(Settings.HotBottomLeft, v => Settings.HotBottomLeft = v)),
                Row("Bottom-right corner", null, CornerPicker(Settings.HotBottomRight, v => Settings.HotBottomRight = v))));
            page.Children.Add(Caption("Keyboard shortcuts"));
            page.Children.Add(Group(
                Row("Screenshot shortcuts", "Ctrl+Shift+3: whole screen to your Desktop.  Ctrl+Shift+4: snip an area.  Ctrl+Shift+5: record the screen.",
                    Toggle(Settings.ScreenshotShortcuts, v => Settings.ScreenshotShortcuts = v)),
                Row("Force Quit", "Ctrl+Alt+Esc opens Force Quit Applications.", Toggle(Settings.ForceQuitShortcut, v => Settings.ForceQuitShortcut = v)),
                Row("Other shortcuts", "Spotlight: Alt+Space    Mission Control: Ctrl+Alt+Up    Launchpad: Windows key", null)));
        }
        private void About()
        {
            var st = new StackPanel { Margin = new Thickness(16) };
            st.Children.Add(new TextBlock { Text = "Mac Look", FontSize = 24, FontWeight = FontWeights.SemiBold });
            st.Children.Add(new TextBlock { Text = "Version " + Updater.CurrentText, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 12) });
            st.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
                Text = "A macOS-style menu bar, Dock, Launchpad, Mission Control, Control Center, Notification Center and Spotlight for Windows, rendered in liquid glass.\n\nSettings are saved in " + Config.SettingsFile + "."
            });
            var b = new Border { CornerRadius = new CornerRadius(12), Child = st };
            b.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
            page.Children.Add(b);
        }
    }
}
