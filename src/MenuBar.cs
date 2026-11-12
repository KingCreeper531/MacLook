using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook
{
    internal class MenuBarWindow : Window
    {
        public const double BarHeight = 26;
        private const int HotkeyId = 0x4D4C;

        private IntPtr hwnd;
        private double scale = 1;
        private IntPtr target;              // last foreground window that isn't ours
        private IntPtr lastForeground;
        private string targetName = "File Explorer";
        private TextBlock appName, clock, network;
        private DispatcherTimer fast, slow;
        private bool fullscreen;
        private uint taskbarCreatedMsg;
        private Border barGlass;
        private const int MissionHotkeyId = 0x4D4D;

        public MenuBarWindow()
        {
            Title = "Mac Look Menu Bar";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            FontFamily = new FontFamily(Theme.UiFont);
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "BarFg");
            Height = BarHeight;
            Build();
            SourceInitialized += OnSourceInitialized;
        }

        // ------------------------------------------------------------------ layout
        private void Build()
        {
            var root = new DockPanel { LastChildFill = true };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(right, Dock.Right);
            root.Children.Add(right);
            root.Children.Add(left);

            left.Children.Add(Item(Logo(), b => Open(b, LogoMenu())));
            appName = Text("File Explorer", true);
            left.Children.Add(Item(appName, b => Open(b, AppMenu())));
            left.Children.Add(Item(Text("File"), b => Open(b, FileMenu())));
            left.Children.Add(Item(Text("Edit"), b => Open(b, EditMenu())));
            left.Children.Add(Item(Text("View"), b => Open(b, ViewMenu())));
            left.Children.Add(Item(Text("Window"), b => Open(b, WindowMenu())));
            left.Children.Add(Item(Text("Help"), b => Open(b, HelpMenu())));

            right.Children.Add(Item(Glyph(""), b => Open(b, MacLookMenu())));
            network = Glyph("");
            right.Children.Add(Item(network, b => Program.WifiMenu.ToggleFrom(b)));
            right.Children.Add(Item(Glyph(""), b => Program.SoundMenu.ToggleFrom(b)));
            right.Children.Add(Item(Glyph(""), b => Flash(b, () => Program.Spotlight.Toggle())));
            right.Children.Add(Item(Glyph(""), b => Flash(b, () => Program.ControlCenter.Toggle())));
            clock = Text("");
            right.Children.Add(Item(clock, b => Flash(b, () => Program.NotificationCenter.Toggle())));

            barGlass = new Border { Child = root, UseLayoutRounding = true };
            barGlass.SizeChanged += (s, e) => ApplyGlass();
            Content = barGlass;

            for (int i = 2; i < left.Children.Count; i++) appMenuItems.Add((UIElement)left.Children[i]);
            wifiItem = (UIElement)right.Children[1];
            soundItem = (UIElement)right.Children[2];
            searchItem = (UIElement)right.Children[3];
            ApplySettings();
            UpdateNetwork();
        }

        private readonly List<UIElement> appMenuItems = new List<UIElement>();
        private UIElement wifiItem, soundItem, searchItem;

        /// <summary>Show/hide menu bar items and re-format the clock from Settings.</summary>
        public void ApplySettings()
        {
            foreach (var e in appMenuItems) e.Visibility = Settings.ShowAppMenus ? Visibility.Visible : Visibility.Collapsed;
            wifiItem.Visibility = Settings.ShowWifi ? Visibility.Visible : Visibility.Collapsed;
            searchItem.Visibility = Settings.ShowSearch ? Visibility.Visible : Visibility.Collapsed;
            soundItem.Visibility = Settings.ShowSound ? Visibility.Visible : Visibility.Collapsed;
            UpdateClock();
            ApplyGlass();
        }

        private static TextBlock Text(string s, bool bold = false)
        {
            return new TextBlock
            {
                Text = s, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
            };
        }

        private static TextBlock Glyph(string g)
        {
            return new TextBlock
            {
                Text = g, FontFamily = new FontFamily(Theme.IconFont), FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static UIElement Logo()
        {
            // A small four-pane mark (it's still Windows underneath).
            var c = new Canvas { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 4; i++)
            {
                var r = new Rectangle { Width = 6, Height = 6, RadiusX = 1.2, RadiusY = 1.2 };
                r.SetResourceReference(Shape.FillProperty, "BarFg");
                Canvas.SetLeft(r, (i % 2) * 7);
                Canvas.SetTop(r, (i / 2) * 7);
                c.Children.Add(r);
            }
            return c;
        }

        private Border Item(UIElement content, Action<Border> click)
        {
            var b = new Border
            {
                Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(1, 3, 1, 3),
                CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Child = content
            };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; click(b); };
            return b;
        }

        private void Open(Border anchor, ContextMenu menu)
        {
            anchor.SetResourceReference(Border.BackgroundProperty, "BarHighlight");
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = -12;
            menu.VerticalOffset = 1;
            menu.Closed += (s, e) => anchor.Background = Brushes.Transparent;
            GlassFx.PrepareMenu(menu);
            menu.IsOpen = true;
        }

        private void Flash(Border b, Action a)
        {
            b.SetResourceReference(Border.BackgroundProperty, "BarHighlight");
            Launcher.Later(180, () => b.Background = Brushes.Transparent);
            a();
        }

        // ------------------------------------------------------------------ menus
        private static MenuItem MI(string header, string gesture, Action act)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (act != null) mi.Click += (s, e) => act();
            else mi.IsEnabled = false;
            return mi;
        }

        private static ContextMenu Menu(params object[] items)
        {
            var m = new ContextMenu();
            foreach (var i in items) m.Items.Add(i ?? new Separator());
            return m;
        }

        /// <summary>Gives focus back to the app the user was in, then sends it a shortcut.</summary>
        private void Send(params byte[] keys)
        {
            IntPtr t = target;
            Launcher.Later(60, () =>
            {
                if (t != IntPtr.Zero) Native.ActivateWindow(t);
                Launcher.Later(40, () => Native.PressKeys(keys));
            });
        }

        private static void SendWin(byte key) { Native.PressKeys(Native.VK_WIN, key); }

        private const byte C = Native.VK_CTRL, S = Native.VK_SHIFT;

        private ContextMenu LogoMenu()
        {
            return Menu(
                MI("About This PC", null, () => Launcher.Open("ms-settings:about")),
                null,
                MI("Mac Look Settings…", null, () => Program.SettingsWin.Open("General")),
                MI("System Settings…", null, () => Launcher.Open("ms-settings:")),
                MI("App Store…", null, () => Launcher.Open("ms-windows-store:")),
                null,
                MI("Force Quit…", "Ctrl+Shift+Esc", () => Launcher.Open("taskmgr.exe")),
                null,
                MI("Sleep", null, Power.Sleep),
                MI("Restart…", null, Power.Restart),
                MI("Shut Down…", null, Power.ShutDown),
                null,
                MI("Lock Screen", "Win+L", Power.Lock),
                MI("Log Out " + Environment.UserName + "…", null, Power.LogOut));
        }

        private ContextMenu AppMenu()
        {
            IntPtr t = target;
            return Menu(
                MI("About " + targetName, null, null),
                null,
                MI("Hide " + targetName, null, () => { if (t != IntPtr.Zero) Native.ShowWindow(t, Native.SW_MINIMIZE); }),
                MI("Hide Others", null, () => HideOthers(t)),
                MI("Show All", null, ShowAll),
                null,
                MI("Quit " + targetName, "Alt+F4", () => QuitApp(t)));
        }

        private ContextMenu FileMenu()
        {
            return Menu(
                MI("New", "Ctrl+N", () => Send(C, (byte)'N')),
                MI("Open…", "Ctrl+O", () => Send(C, (byte)'O')),
                null,
                MI("Close Window", "Ctrl+W", () => Send(C, (byte)'W')),
                MI("Save", "Ctrl+S", () => Send(C, (byte)'S')),
                MI("Save As…", "Ctrl+Shift+S", () => Send(C, S, (byte)'S')),
                null,
                MI("Print…", "Ctrl+P", () => Send(C, (byte)'P')));
        }

        private ContextMenu EditMenu()
        {
            return Menu(
                MI("Undo", "Ctrl+Z", () => Send(C, (byte)'Z')),
                MI("Redo", "Ctrl+Y", () => Send(C, (byte)'Y')),
                null,
                MI("Cut", "Ctrl+X", () => Send(C, (byte)'X')),
                MI("Copy", "Ctrl+C", () => Send(C, (byte)'C')),
                MI("Paste", "Ctrl+V", () => Send(C, (byte)'V')),
                MI("Select All", "Ctrl+A", () => Send(C, (byte)'A')),
                null,
                MI("Find…", "Ctrl+F", () => Send(C, (byte)'F')),
                null,
                MI("Emoji & Symbols", "Win+.", () => Send(Native.VK_WIN, Native.VK_PERIOD)));
        }

        private ContextMenu ViewMenu()
        {
            return Menu(
                MI("Zoom In", "Ctrl+Plus", () => Send(C, Native.VK_PLUS)),
                MI("Zoom Out", "Ctrl+Minus", () => Send(C, Native.VK_MINUS)),
                MI("Actual Size", "Ctrl+0", () => Send(C, (byte)'0')),
                null,
                MI("Enter Full Screen", "F11", () => Send(Native.VK_F11)));
        }

        private ContextMenu WindowMenu()
        {
            IntPtr t = target;
            var items = new List<object>
            {
                MI("Minimize", null, () => { if (t != IntPtr.Zero) Native.ShowWindow(t, Native.SW_MINIMIZE); }),
                MI("Zoom", null, () => { if (t != IntPtr.Zero) Native.ShowWindow(t, Native.IsZoomed(t) ? Native.SW_RESTORE : Native.SW_MAXIMIZE); }),
                null,
                MI("Move Window to Left Side of Screen", "Win+Left", () => Send(Native.VK_WIN, Native.VK_LEFT)),
                MI("Move Window to Right Side of Screen", "Win+Right", () => Send(Native.VK_WIN, Native.VK_RIGHT)),
                null,
                MI("Bring All to Front", null, () => BringAllToFront(t)),
            };
            var desc = t != IntPtr.Zero ? WindowTracker.Describe(t) : null;
            if (desc != null)
            {
                var wins = WindowTracker.Enumerate().Where(w => w.GroupKey == desc.GroupKey).ToList();
                if (wins.Count > 0) items.Add(null);
                foreach (var w in wins)
                {
                    IntPtr h = w.Hwnd;
                    var mi = MI(Trim(w.Title, 60), null, () => Native.ActivateWindow(h));
                    mi.IsCheckable = false;
                    mi.IsChecked = h == t;
                    items.Add(mi);
                }
            }
            return Menu(items.ToArray());
        }

        private ContextMenu HelpMenu()
        {
            return Menu(
                MI("Search", "Alt+Space", () => Program.Spotlight.Toggle()),
                null,
                MI(targetName + " Help", "F1", () => Send(Native.VK_F1)),
                MI("Get Help with Windows", null, () => Launcher.Open("https://support.microsoft.com/windows")));
        }

        private ContextMenu MacLookMenu()
        {
            var hide = MI("Hide Windows Taskbar", null, () =>
            {
                Settings.HideTaskbar = !Settings.HideTaskbar;
                Settings.Save();
                if (Settings.HideTaskbar) Taskbar.Hide(); else Taskbar.Restore();
            });
            hide.IsChecked = Settings.HideTaskbar;

            var magnify = MI("Dock Magnification", null, () => { Settings.Magnify = !Settings.Magnify; Settings.Save(); });
            magnify.IsChecked = Settings.Magnify;

            var reserve = MI("Automatically Hide and Show the Dock", null, () =>
            {
                Settings.DockAutoHide = !Settings.DockAutoHide;
                Settings.Save();
                Program.Dock.Rebuild();
            });
            reserve.IsChecked = Settings.DockAutoHide;

            var winKey = MI("Windows Key Opens Launchpad", null, () => { Settings.WinKeyLaunchpad = !Settings.WinKeyLaunchpad; Settings.Save(); });
            winKey.IsChecked = Settings.WinKeyLaunchpad;

            bool startup = false;
            try { startup = Startup.Enabled; } catch { }
            var login = MI("Open at Login", null, () => { try { Startup.Enabled = !Startup.Enabled; } catch { } });
            login.IsChecked = startup;

            return Menu(
                MI("Mac Look", null, null),
                null,
                MI("Mac Look Settings…", null, () => Program.SettingsWin.Open("General")),
                null,
                MI("Spotlight Search", "Alt+Space", () => Program.Spotlight.Toggle()),
                MI("Launchpad", null, () => Program.Launchpad.Toggle()),
                MI("Mission Control", "Ctrl+Alt+Up", () => Program.MissionControl.Toggle()),
                MI("Refresh Glass", null, Program.RecaptureDesktop),
                MI("Check for Updates…", null, () => Updater.Check(true)),
                null,
                hide, magnify, reserve, winKey, login,
                null,
                MI("Set Mac-Style Wallpaper", null, () => { try { Wallpaper.SetMacStyle(); Program.RecaptureDesktop(); } catch (Exception ex) { Program.Log(ex); } }),
                MI("Restore Original Wallpaper", null, Wallpaper.CanRestore ? new Action(Wallpaper.Restore) : null),
                null,
                MI("Edit Dock Items…", null, () => Launcher.Open("notepad.exe", "\"" + Config.DockFile + "\"")),
                MI("Reload Dock", null, () => Program.Dock.Reload()),
                null,
                MI("Quit Mac Look", null, Program.Quit));
        }

        private static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        private void HideOthers(IntPtr keep)
        {
            var desc = keep != IntPtr.Zero ? WindowTracker.Describe(keep) : null;
            foreach (var w in WindowTracker.Enumerate())
                if (desc == null || w.GroupKey != desc.GroupKey) Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE);
        }

        private static void ShowAll()
        {
            foreach (var w in WindowTracker.Enumerate())
                if (Native.IsIconic(w.Hwnd)) Native.ShowWindow(w.Hwnd, Native.SW_SHOWNA);
        }

        private static void BringAllToFront(IntPtr t)
        {
            var desc = t != IntPtr.Zero ? WindowTracker.Describe(t) : null;
            if (desc == null) return;
            var wins = WindowTracker.Enumerate().Where(w => w.GroupKey == desc.GroupKey).ToList();
            for (int i = wins.Count - 1; i >= 0; i--) Native.ActivateWindow(wins[i].Hwnd);
        }

        public static void QuitApp(IntPtr t)
        {
            var desc = t != IntPtr.Zero ? WindowTracker.Describe(t) : null;
            if (desc == null) return;
            foreach (var w in WindowTracker.Enumerate().Where(w => w.GroupKey == desc.GroupKey))
                Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ window plumbing
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            src.AddHook(WndProc);
            scale = src.CompositionTarget.TransformToDevice.M11;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");

            Reposition();
            if (!Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE))
                Program.Log("Alt+Space is already taken by another app; use the magnifier in the menu bar instead.");
            if (!Native.RegisterHotKey(hwnd, MissionHotkeyId, Native.MOD_ALT | Native.MOD_CONTROL | Native.MOD_NOREPEAT, 0x26))
                Program.Log("Ctrl+Alt+Up is already taken; open Mission Control from the dock instead.");

            fast = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            fast.Tick += (s, a) => FastTick();
            fast.Start();
            slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            slow.Tick += (s, a) => SlowTick();
            slow.Start();
            Closed += (s, a) => { Native.UnregisterHotKey(hwnd, HotkeyId); Native.UnregisterHotKey(hwnd, MissionHotkeyId); };
            IsVisibleChanged += (s, a) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(ApplyGlass), DispatcherPriority.Loaded); };
        }

        public void Reposition()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Left = b.Left / scale;
            Top = b.Top / scale;
            Width = b.Width / scale;
            Height = BarHeight;
            AppBar.Set(hwnd, Native.ABE_TOP, (int)Math.Round(BarHeight * scale));
        }

        /// <summary>Liquid glass from the desktop snapshot; acrylic if there is no snapshot yet.</summary>
        public void ApplyGlass()
        {
            if (hwnd == IntPtr.Zero || !IsVisible) return;
            if (Program.Desktop != null)
            {
                Native.SetBlur(hwnd, 0, 0);
                GlassFx.Apply(barGlass, Program.Desktop, GlassStyles.MenuBar);
            }
            else if (!Native.SetBlur(hwnd, 4, Theme.BarTint))
            {
                barGlass.SetResourceReference(Border.BackgroundProperty, "BarFallbackBg");
            }
        }

        private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                Program.Spotlight.Toggle();
                handled = true;
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == MissionHotkeyId)
            {
                Program.MissionControl.Toggle();
                handled = true;
            }
            else if (msg == AppBar.CallbackMessage && wParam.ToInt32() == Native.ABN_POSCHANGED)
            {
                Dispatcher.BeginInvoke(new Action(Reposition));
            }
            else if (taskbarCreatedMsg != 0 && msg == taskbarCreatedMsg)
            {
                // Explorer restarted: our app bar registrations are gone and the taskbar is back.
                AppBar.Forget();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Reposition();
                    Program.Dock.Reposition();
                    if (Settings.HideTaskbar) Taskbar.Hide();
                }));
            }
            return IntPtr.Zero;
        }

        private void FastTick()
        {
            Taskbar.Enforce();
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != lastForeground)
            {
                lastForeground = fg;
                if (fg != IntPtr.Zero && !WindowTracker.IsOwn(fg))
                {
                    if (WindowTracker.IsShellWindow(fg))
                    {
                        target = IntPtr.Zero;
                        targetName = "File Explorer";
                    }
                    else
                    {
                        IntPtr root = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
                        if (root == IntPtr.Zero) root = fg;
                        var w = WindowTracker.Describe(root);
                        if (w != null) { target = root; targetName = WindowTracker.DisplayName(w); }
                    }
                    appName.Text = targetName;
                }
            }

            bool fs = IsFullscreen(fg);
            if (fs != fullscreen)
            {
                fullscreen = fs;
                Program.SetFullscreen(fs);
            }
        }

        private static bool IsFullscreen(IntPtr fg)
        {
            if (fg == IntPtr.Zero || WindowTracker.IsOwn(fg) || WindowTracker.IsShellWindow(fg)) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        private void SlowTick()
        {
            UpdateClock();
            if (DateTime.Now.Second % 10 == 0) UpdateNetwork();
            if (DateTime.Now.Second % 2 == 0) WindowAnimator.RefreshForeground();
            Program.Dock.Refresh();
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;
            var ci = CultureInfo.InvariantCulture;
            string date = (Settings.ClockDay ? now.ToString("ddd", ci) + " " : "") + (Settings.ClockDate ? now.ToString("MMM d", ci) : "");
            string time = now.ToString(Settings.Clock24h ? (Settings.ClockSeconds ? "HH:mm:ss" : "HH:mm") : (Settings.ClockSeconds ? "h:mm:ss tt" : "h:mm tt"), ci);
            clock.Text = date.Trim().Length > 0 ? date.Trim() + "   " + time : time;
        }

        private void UpdateNetwork()
        {
            bool wifi = false, any = false;
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    any = true;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi = true;
                }
            }
            catch { }
            network.Text = wifi ? "" : any ? "" : "";
        }
    }
}
