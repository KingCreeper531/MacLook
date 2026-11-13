using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MacLook
{
    // ======================================================================== small dialogs
    /// <summary>A small rounded text prompt (used for Rename…).</summary>
    internal class Prompt : Window
    {
        private readonly TextBox box;
        private bool ok;

        private Prompt(string title, string message, string initial)
        {
            Title = title;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
            FontFamily = new FontFamily(Theme.UiFont); FontSize = 13;
            SetResourceReference(ForegroundProperty, "MenuFg");

            var st = new StackPanel { Width = 320 };
            st.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(message)) st.Children.Add(new TextBlock { Text = message, Opacity = 0.65, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8, 5, 8, 5), FontSize = 13.5, BorderThickness = new Thickness(1) };
            box.SetResourceReference(TextBox.BorderBrushProperty, "Accent");
            st.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(Ui.PillButton("Cancel", false, () => Close()));
            buttons.Children.Add(Ui.PillButton("OK", true, () => { ok = true; Close(); }));
            st.Children.Add(buttons);
            Content = Ui.Card(st);
            Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { ok = true; Close(); }
                else if (e.Key == Key.Escape) Close();
            };
        }

        public static string Ask(string title, string message, string initial)
        {
            var p = new Prompt(title, message, initial);
            p.ShowDialog();
            return p.ok && p.box.Text.Trim().Length > 0 ? p.box.Text.Trim() : null;
        }
    }

    /// <summary>Pick an installed app (Dock editor's "Add from Apps…").</summary>
    internal class AppPicker : Window
    {
        private readonly TextBox search;
        private readonly StackPanel list = new StackPanel();
        private AppEntry chosen, selected;
        private Border selectedRow;

        private AppPicker()
        {
            Title = "Add an App";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; Width = 470; Height = 600; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
            FontFamily = new FontFamily(Theme.UiFont); FontSize = 13;
            SetResourceReference(ForegroundProperty, "MenuFg");

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = "Add an App to the Dock", FontSize = 15, FontWeight = FontWeights.SemiBold });
            search = new TextBox { Margin = new Thickness(0, 10, 0, 8), Padding = new Thickness(8, 5, 8, 5), FontSize = 13.5 };
            search.TextChanged += (s, e) => Fill();
            Grid.SetRow(search, 1);
            grid.Children.Add(search);
            var sv = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(sv, 2);
            grid.Children.Add(sv);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(Ui.PillButton("Cancel", false, () => Close()));
            buttons.Children.Add(Ui.PillButton("Add", true, () => { if (selected != null) { chosen = selected; Close(); } }));
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);
            var card = Ui.Card(grid);
            Content = card;
            Loaded += (s, e) => { search.Focus(); Fill(); };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private void Fill()
        {
            list.Children.Clear();
            string q = search.Text.Trim();
            foreach (var a in AppCatalog.All.Where(x => AppCatalog.Score(AppCatalog.DisplayName(x), q) > 0).Take(250))
            {
                var app = a;
                var img = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                var dp = new DockPanel();
                dp.Children.Add(img);
                dp.Children.Add(new TextBlock { Text = AppCatalog.DisplayName(a), VerticalAlignment = VerticalAlignment.Center });
                var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Child = dp, Background = Brushes.Transparent };
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (selectedRow != null) { selectedRow.Background = Brushes.Transparent; ((DockPanel)selectedRow.Child).ClearValue(TextBlock.ForegroundProperty); }
                    selected = app; selectedRow = row;
                    row.SetResourceReference(Border.BackgroundProperty, "Accent");
                    dp.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                    if (e.ClickCount == 2) { chosen = app; Close(); }
                };
                list.Children.Add(row);
                Dispatcher.BeginInvoke(new Action(() => img.Source = IconStyler.Get(app.Key, 32)), DispatcherPriority.Background);
            }
        }

        public static AppEntry Pick()
        {
            var p = new AppPicker();
            p.ShowDialog();
            return p.chosen;
        }
    }

    internal static class Ui
    {
        public static Border Card(UIElement child)
        {
            var b = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(24), Child = child, BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, "MenuBg");
            b.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
            b.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.35 };
            return b;
        }

        public static Border PillButton(string text, bool primary, Action a)
        {
            var t = new TextBlock { Text = text, FontSize = 13 };
            var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 5, 16, 6), Margin = new Thickness(8, 0, 0, 0), Child = t, Cursor = Cursors.Hand };
            if (primary) { b.SetResourceReference(Border.BackgroundProperty, "Accent"); t.Foreground = Brushes.White; }
            else b.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            b.MouseLeftButtonDown += (s, e) => { b.Opacity = 0.7; e.Handled = true; };
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.MouseLeftButtonUp += (s, e) => { b.Opacity = 1; e.Handled = true; a(); };
            return b;
        }
    }

    // ======================================================================== hot corners
    /// <summary>Push the pointer into a screen corner to trigger an action, like macOS Hot Corners.</summary>
    internal static class HotCorners
    {
        public static readonly string[][] Actions =
        {
            new[] { "none", "Nothing" }, new[] { "mission", "Mission Control" }, new[] { "launchpad", "Launchpad" },
            new[] { "desktop", "Show Desktop" }, new[] { "notifications", "Notification Center" }, new[] { "control", "Control Center" },
            new[] { "spotlight", "Spotlight" }, new[] { "screensaver", "Start Screen Saver" }, new[] { "lock", "Lock Screen" },
        };

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private static DispatcherTimer timer;
        private static string armedCorner;
        private static DateTime enteredAt;
        private static bool fired;

        public static string Label(string action)
        {
            foreach (var a in Actions) if (a[0] == action) return a[1];
            return "Nothing";
        }

        public static void Start()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        private static void Tick()
        {
            if (Settings.HotTopLeft == "none" && Settings.HotTopRight == "none" && Settings.HotBottomLeft == "none" && Settings.HotBottomRight == "none") return;
            if (Program.IsFullscreen || (GetAsyncKeyState(0x01) & 0x8000) != 0) { armedCorner = null; return; }
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            const int hot = 2, release = 40;
            string corner = null;
            if (p.X <= b.Left + hot && p.Y <= b.Top + hot) corner = "tl";
            else if (p.X >= b.Right - 1 - hot && p.Y <= b.Top + hot) corner = "tr";
            else if (p.X <= b.Left + hot && p.Y >= b.Bottom - 1 - hot) corner = "bl";
            else if (p.X >= b.Right - 1 - hot && p.Y >= b.Bottom - 1 - hot) corner = "br";

            if (corner == null)
            {
                bool far = p.X > b.Left + release && p.X < b.Right - release || p.Y > b.Top + release && p.Y < b.Bottom - release;
                if (far) { armedCorner = null; fired = false; }
                return;
            }
            if (corner != armedCorner) { armedCorner = corner; enteredAt = DateTime.Now; fired = false; return; }
            if (fired || (DateTime.Now - enteredAt).TotalMilliseconds < 140) return;
            fired = true;
            string action = corner == "tl" ? Settings.HotTopLeft : corner == "tr" ? Settings.HotTopRight : corner == "bl" ? Settings.HotBottomLeft : Settings.HotBottomRight;
            Run(action);
        }

        public static void Run(string action)
        {
            switch (action)
            {
                case "mission": Program.MissionControl.Toggle(); break;
                case "launchpad": Program.Launchpad.Toggle(); break;
                case "desktop": Native.PressKeys(Native.VK_WIN, (byte)'D'); break;
                case "notifications": Program.NotificationCenter.Toggle(); break;
                case "control": Program.ControlCenter.Toggle(); break;
                case "spotlight": Program.Spotlight.Toggle(); break;
                case "screensaver": SendMessage(Native.GetForegroundWindow(), 0x0112, new IntPtr(0xF140), IntPtr.Zero); break;
                case "lock": Power.Lock(); break;
            }
        }
    }

    // ======================================================================== screenshots
    /// <summary>Ctrl+Shift+3 / 4 / 5, like the Mac's Cmd+Shift+3 / 4 / 5.</summary>
    internal static class Screenshots
    {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

        /// <summary>Wait until Ctrl/Shift/Alt are released before sending another shortcut.</summary>
        public static void AfterModifiersReleased(Action a)
        {
            var start = DateTime.Now;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            t.Tick += (s, e) =>
            {
                bool held = (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;
                if (held && (DateTime.Now - start).TotalSeconds < 3) return;
                t.Stop();
                a();
            };
            t.Start();
        }

        public static void Region() { AfterModifiersReleased(() => Native.PressKeys(Native.VK_WIN, Native.VK_SHIFT, (byte)'S')); }
        public static void Record() { AfterModifiersReleased(() => Native.PressKeys(Native.VK_WIN, Native.VK_SHIFT, (byte)'R')); }

        /// <summary>Whole screen to a PNG on the Desktop, with a flash and a floating preview.</summary>
        public static void FullScreen()
        {
            var shot = ScreenCapture.Capture();
            if (shot == null) return;
            var now = DateTime.Now;
            string name = "Screenshot " + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " at " + now.ToString("h.mm.ss tt", CultureInfo.InvariantCulture) + ".png";
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
            var bmp = BitmapSource.Create(shot.W, shot.H, 96, 96, PixelFormats.Bgr32, null, shot.D, shot.W * 4);
            bmp.Freeze();
            try
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch (Exception ex) { Program.Log(ex); return; }
            Flash();
            new ShotPreview(bmp, path).Show();
        }

        private static void Flash()
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.White, Topmost = true,
                ShowInTaskbar = false, ShowActivated = false, Left = 0, Top = 0,
                Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight, Opacity = 0.55
            };
            w.Show();
            var a = new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(260));
            a.Completed += (s, e) => w.Close();
            w.BeginAnimation(UIElement.OpacityProperty, a);
        }

        /// <summary>The little thumbnail that slides in at the bottom-right after a screenshot.</summary>
        private class ShotPreview : Window
        {
            private readonly TranslateTransform slide = new TranslateTransform(320, 0);

            public ShotPreview(BitmapSource img, string path)
            {
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                Topmost = true; ShowInTaskbar = false; ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight;
                var image = new Image { Source = img, Width = 240, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Background = new SolidColorBrush(Color.FromArgb(235, 250, 250, 252)),
                    Child = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = image },
                    Margin = new Thickness(20), Cursor = Cursors.Hand, RenderTransform = slide,
                    Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.4 }
                };
                Content = card;
                card.MouseLeftButtonUp += (s, e) => { Launcher.Open(path); Close(); };
                card.MouseRightButtonUp += (s, e) => { Launcher.Open("explorer.exe", "/select,\"" + path + "\""); Close(); };
                Loaded += (s, e) =>
                {
                    var wa = SystemParameters.WorkArea;
                    Left = SystemParameters.PrimaryScreenWidth - ActualWidth - 8;
                    Top = SystemParameters.PrimaryScreenHeight - ActualHeight - 90;
                    slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(320, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
                    Launcher.Later(5000, () =>
                    {
                        if (!IsVisible) return;
                        var a = new DoubleAnimation(0, 340, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                        a.Completed += (o, ev) => Close();
                        slide.BeginAnimation(TranslateTransform.XProperty, a);
                    });
                };
            }
        }
    }

    // ======================================================================== force quit
    internal class ForceQuitWindow : GlassPanel
    {
        private readonly StackPanel list = new StackPanel();
        private List<AppWindow> selectedGroup;
        private Border selectedRow, quitButton;

        public ForceQuitWindow()
        {
            Title = "Force Quit Applications";
            var card = GlassCard(GlassStyles.Panel, 20);
            card.Padding = new Thickness(20, 16, 20, 18);
            var st = new StackPanel { Width = 380 };
            st.Children.Add(Label("Force Quit Applications", 15, true));
            var sub = Label("If an app doesn't respond for a while, select its name and click Force Quit.", 12, false, 0.65);
            sub.TextWrapping = TextWrapping.Wrap;
            sub.Margin = new Thickness(0, 4, 0, 12);
            st.Children.Add(sub);
            var sv = new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Child = sv };
            listBox.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
            st.Children.Add(listBox);
            var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var tm = Ui.PillButton("Task Manager", false, () => { HidePanel(); Launcher.Open("taskmgr.exe"); });
            tm.Margin = new Thickness(0);
            buttons.Children.Add(tm);
            quitButton = Ui.PillButton("Force Quit", true, ForceQuit);
            quitButton.HorizontalAlignment = HorizontalAlignment.Right;
            quitButton.Opacity = 0.5;
            buttons.Children.Add(quitButton);
            st.Children.Add(buttons);
            card.Child = st;
            var outer = WithShadow(card, 20);
            outer.Margin = new Thickness(30, 20, 30, 40);
            Content = outer;
        }

        protected override void OnOpening()
        {
            list.Children.Clear();
            selectedGroup = null; selectedRow = null; quitButton.Opacity = 0.5;
            foreach (var g in WindowTracker.Enumerate().GroupBy(w => w.GroupKey))
            {
                var group = g.ToList();
                var w0 = group[0];
                var img = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), Source = IconStyler.Get(w0.ShellTarget, 32) };
                var dp = new DockPanel();
                dp.Children.Add(img);
                dp.Children.Add(new TextBlock { Text = WindowTracker.DisplayName(w0), VerticalAlignment = VerticalAlignment.Center });
                var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Child = dp, Background = Brushes.Transparent };
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (selectedRow != null) { selectedRow.Background = Brushes.Transparent; ((DockPanel)selectedRow.Child).ClearValue(TextBlock.ForegroundProperty); }
                    selectedRow = row; selectedGroup = group; quitButton.Opacity = 1;
                    row.SetResourceReference(Border.BackgroundProperty, "Accent");
                    dp.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                    e.Handled = true;
                };
                list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(Label("No apps are open.", 13, false, 0.6));
        }

        private void ForceQuit()
        {
            if (selectedGroup == null) return;
            var group = selectedGroup;
            string name = WindowTracker.DisplayName(group[0]);
            HidePanel();
            if (MessageBox.Show("Do you want to force “" + name + "” to quit?\n\nYou will lose any unsaved changes.", "Force Quit",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            foreach (var pid in group.Select(w => w.Pid).Distinct())
            {
                try
                {
                    var p = Process.GetProcessById((int)pid);
                    if (p.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                }
                catch (Exception ex) { Program.Log(ex); }
            }
        }

        protected override void Place()
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.22;
        }
    }

    // ======================================================================== about this PC
    internal class AboutWindow : GlassPanel
    {
        [DllImport("kernel32.dll")] private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);
        private readonly StackPanel info = new StackPanel { Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        public AboutWindow()
        {
            Title = "About This PC";
            var card = GlassCard(GlassStyles.Panel, 22);
            card.Padding = new Thickness(28, 26, 32, 24);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new Image { Source = AppIcon.Render(256), Width = 128, Height = 128, VerticalAlignment = VerticalAlignment.Top };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            row.Children.Add(icon);
            row.Children.Add(info);
            card.Child = row;
            var outer = WithShadow(card, 22);
            outer.Margin = new Thickness(30, 20, 30, 40);
            Content = outer;
        }

        protected override void OnOpening()
        {
            info.Children.Clear();
            string product = "Windows", version = "", build = "";
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    product = Convert.ToString(k.GetValue("ProductName"));
                    version = Convert.ToString(k.GetValue("DisplayVersion"));
                    build = Convert.ToString(k.GetValue("CurrentBuild"));
                    int b;
                    if (int.TryParse(build, out b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                }
            }
            catch { }
            info.Children.Add(Label(product, 26, true));
            info.Children.Add(Label("Version " + version + " (Build " + build + ")", 12.5, false, 0.6));
            info.Children.Add(new Border { Height = 14 });

            AddRow("Name", Environment.MachineName);
            AddRow("Processor", Cpu());
            ulong kb;
            if (GetPhysicallyInstalledSystemMemory(out kb)) AddRow("Memory", Math.Round(kb / 1048576.0) + " GB");
            AddRow("Graphics", Gpu());
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            AddRow("Display", sb.Width + " × " + sb.Height);
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                AddRow("Startup Disk", d.Name.TrimEnd('\\') + "  " + Gb(d.AvailableFreeSpace) + " available of " + Gb(d.TotalSize));
            }
            catch { }
            AddRow("Mac Look", "Version " + Updater.CurrentText);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            var more = Ui.PillButton("More Info…", false, () => { HidePanel(); Launcher.Open("ms-settings:about"); });
            more.Margin = new Thickness(0);
            buttons.Children.Add(more);
            buttons.Children.Add(Ui.PillButton("Storage…", false, () => { HidePanel(); Launcher.Open("ms-settings:storagesense"); }));
            info.Children.Add(buttons);
        }

        private void AddRow(string k, string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var key = Label(k, 12.5, true);
            key.Width = 100;
            dp.Children.Add(key);
            var val = Label(v, 12.5, false, 0.8);
            val.MaxWidth = 320;
            dp.Children.Add(val);
            info.Children.Add(dp);
        }

        private static string Gb(long bytes) { return Math.Round(bytes / 1e9) + " GB"; }

        private static string Cpu()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    return Convert.ToString(k.GetValue("ProcessorNameString")).Trim();
            }
            catch { return null; }
        }

        private static string Gpu()
        {
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    var names = new List<string>();
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                string d = Convert.ToString(k.GetValue("DriverDesc"));
                                if (string.IsNullOrEmpty(d) || d.Contains("Basic") || d.Contains("Remote") || d.Contains("Virtual") || names.Contains(d)) continue;
                                names.Add(d);
                            }
                        }
                        catch { }
                    }
                    return string.Join(", ", names);
                }
            }
            catch { return null; }
        }

        protected override void Place()
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.2;
        }
    }
}
