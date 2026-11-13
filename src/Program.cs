using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Mac Look")]
[assembly: System.Reflection.AssemblyProduct("Mac Look")]
[assembly: System.Reflection.AssemblyDescription("Mac Look: a macOS-style desktop for Windows.")]
[assembly: System.Reflection.AssemblyCompany("Mac Look (github.com/KingCreeper531/MacLook)")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 KingCreeper531")]
[assembly: System.Reflection.AssemblyVersion("2.4.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.4.1.0")]

namespace MacLook
{
    internal static class Program
    {
        public static MenuBarWindow MenuBar;
        public static DockWindow Dock;
        public static SpotlightWindow Spotlight;
        public static ControlCenterWindow ControlCenter;
        public static NotificationCenterWindow NotificationCenter;
        public static LaunchpadWindow Launchpad;
        public static MissionControlWindow MissionControl;
        public static SettingsWindow SettingsWin;
        public static WifiMenuWindow WifiMenu;
        public static SoundMenuWindow SoundMenu;
        public static ForceQuitWindow ForceQuitWin;
        public static AboutWindow AboutWin;
        public static SwitcherWindow Switcher;
        public static TrafficLightsWindow Lights;
        public static bool IsFullscreen;
        private static System.Windows.Threading.DispatcherTimer applyTimer;

        /// <summary>What the desktop looks like behind the menu bar and dock (source for their glass).</summary>
        public static Pix Desktop;

        private static Mutex mutex;
        private static bool cleaned, started, recapturing;

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--restore", StringComparison.OrdinalIgnoreCase))
            {
                // Emergency: put the Windows taskbar back if Mac Look was killed while it was hidden.
                Taskbar.Restore();
                return;
            }
            if (args.Length > 1 && args[0].Equals("--make-icon", StringComparison.OrdinalIgnoreCase))
            {
                AppIcon.WriteIco(args[1]);   // used by build.ps1
                return;
            }
            if (args.Length > 0 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstall(Array.IndexOf(args, "--quiet") >= 0);
                return;
            }

            bool created;
            mutex = new Mutex(true, "MacLook.SingleInstance", out created);
            if (!created) return;

            Directory.CreateDirectory(Config.Dir);
            Settings.Load();

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Load();
            Theme.Apply(app.Resources);
            app.Resources.MergedDictionaries.Add(Theme.Styles());

            AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log(e.ExceptionObject); Cleanup(); };
            app.DispatcherUnhandledException += (s, e) => { Log(e.Exception); e.Handled = true; };
            app.SessionEnding += (s, e) => Cleanup();
            app.Exit += (s, e) => Cleanup();
            SystemEvents.UserPreferenceChanged += (s, e) =>
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    bool wasDark = Theme.Dark;
                    Theme.Load();
                    if (wasDark != Theme.Dark) { Theme.Apply(app.Resources); RefreshGlass(); }
                    if (e.Category == UserPreferenceCategory.Desktop) RecaptureDesktop();   // wallpaper changed
                }));
            SystemEvents.DisplaySettingsChanged += (s, e) =>
                app.Dispatcher.BeginInvoke(new Action(() => { MenuBar.Reposition(); Dock.Reposition(); RecaptureDesktop(); }));

            MenuBar = new MenuBarWindow();
            Dock = new DockWindow();
            Spotlight = new SpotlightWindow();
            ControlCenter = new ControlCenterWindow();
            NotificationCenter = new NotificationCenterWindow();
            Launchpad = new LaunchpadWindow();
            MissionControl = new MissionControlWindow();
            SettingsWin = new SettingsWindow();
            WifiMenu = new WifiMenuWindow();
            SoundMenu = new SoundMenuWindow();
            ForceQuitWin = new ForceQuitWindow();
            AboutWin = new AboutWindow();
            Switcher = new SwitcherWindow();
            Lights = new TrafficLightsWindow();

            // Reserve the menu bar and dock areas first (windows move out of the way), then photograph
            // the empty desktop there before showing anything, so the glass has something real to refract.
            new WindowInteropHelper(MenuBar).EnsureHandle();
            new WindowInteropHelper(Dock).EnsureHandle();
            if (Settings.HideTaskbar) Taskbar.Hide();
            Launcher.Later(700, () =>
            {
                Desktop = ScreenCapture.Capture();
                MenuBar.Show();
                Dock.Show();
                started = true;
                AppCatalog.Load();
                WinKey.Install();
                WindowAnimator.Install();
                HotCorners.Start();
                Lights.Start();
                if (Array.IndexOf(args, "--demo") < 0) Updater.Start();
                if (Array.IndexOf(args, "--demo") >= 0) Demo();
            });

            app.Run();
        }

        /// <summary>Started from Windows' Installed apps (or MacLook.exe --uninstall).</summary>
        private static void Uninstall(bool quiet)
        {
            if (!quiet && MessageBox.Show("Uninstall Mac Look?\n\nYour Windows taskbar, wallpaper and window settings will be put back.",
                    "Uninstall Mac Look", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            bool wipe = !quiet && MessageBox.Show("Also delete your Mac Look settings, dock list and custom icons?",
                    "Uninstall Mac Look", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            InstallInfo.StopRunning();
            try { Settings.Load(); if (Wallpaper.CanRestore) Wallpaper.Restore(); } catch { }
            try { Taskbar.Restore(); } catch { }
            InstallInfo.Unregister(wipe, Config.Dir);
            if (!quiet) MessageBox.Show("Mac Look has been removed.", "Uninstall Mac Look", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Hide the bars while a game or video is fullscreen.</summary>
        public static void SetFullscreen(bool fs)
        {
            IsFullscreen = fs;
            if (!started || recapturing) return;
            if (fs) { MenuBar.Hide(); Dock.Hide(); }
            else { MenuBar.Show(); Dock.Show(); }
        }

        /// <summary>Save settings and apply them everywhere, debounced so dragging a slider stays smooth.</summary>
        public static void ApplySettingsSoon()
        {
            if (applyTimer == null)
            {
                applyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                applyTimer.Tick += (s, e) => { applyTimer.Stop(); ApplySettings(); };
            }
            applyTimer.Stop();
            applyTimer.Start();
        }

        public static void ApplySettings()
        {
            try
            {
                Settings.Save();
                Theme.Load();
                Theme.Apply(Application.Current.Resources);
                MenuBar.ApplySettings();
                IconStyler.ClearCache();
                Launchpad.ResetTiles();
                Dock.Rebuild();
                RefreshGlass();
                WindowGlass.Reapply();
                WindowAnimator.ApplySystemAnimation();
                MenuBar.RegisterShortcuts();
                Lights.Refresh();
                Launchpad.ResetTiles();
            }
            catch (Exception ex) { Log(ex); }
        }

        public static void IconsChanged()
        {
            IconStyler.ClearCache();
            Launchpad.ResetTiles();
            Dock.Rebuild();
        }

        public static void RefreshGlass()
        {
            if (MenuBar != null) MenuBar.ApplyGlass();
            if (Dock != null) Dock.ApplyGlass();
        }

        /// <summary>Briefly hide the bars and re-photograph the desktop behind them (e.g. after a wallpaper change).</summary>
        public static void RecaptureDesktop()
        {
            if (!started || recapturing) return;
            recapturing = true;
            MenuBar.Hide();
            Dock.Hide();
            Launcher.Later(350, () =>
            {
                Desktop = ScreenCapture.Capture();
                recapturing = false;
                MenuBar.Show();
                Dock.Show();
            });
        }

        /// <summary>Self-test: opens each surface, saves a screenshot of it, then quits.</summary>
        private static void Demo()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int cx = b.Left + b.Width / 2;
            Native.POINT orig;
            Native.GetCursorPos(out orig);
            var steps = new Action[]
            {
                () => SaveButtons("1-window-buttons"),
                () => Switcher.Open(false), () => { }, () => SaveCenter("2-switcher"), () => Switcher.Next(), () => SaveCenter("3-switcher-next"), () => Switcher.Cancel(),
                Quit
            };       int i = 0;
            Action next = null;
            next = () =>
            {
                try { steps[i](); } catch (Exception ex) { Log(ex); }
                i++;
                if (i < steps.Length) Launcher.Later(1200, next);
            };
            Launcher.Later(1500, next);
        }

        private static void SaveDockArea(string name)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int w = Math.Min(b.Width, 1800), h = 260;
            Save(name, ScreenCapture.CaptureRegion(b.Left + (b.Width - w) / 2, b.Bottom - h, w, h));
        }

        private static void SaveCenter(string name)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Save(name, ScreenCapture.CaptureRegion(b.Left + b.Width / 2 - 700, b.Top + 120, 1400, 900));
        }

        private static void SaveButtons(string name)
        {
            var r = Native.FrameRect(Native.GetForegroundWindow());
            Save(name, ScreenCapture.CaptureRegion(r.Right - 360, r.Top, 360, 60));
        }

        private static void SaveLights(string name)
        {
            var r = Native.FrameRect(Native.GetForegroundWindow());
            Save(name, ScreenCapture.CaptureRegion(r.Left, r.Top, 420, 60));
        }

        private static void SaveCorner(string name)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Save(name, ScreenCapture.CaptureRegion(b.Right - 520, b.Top, 520, 620));
        }

        private static void Save(string name, Pix p)
        {
            if (p == null) return;
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(p.W, p.H, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, p.D, p.W * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            Directory.CreateDirectory(Path.Combine(Config.Dir, "demo"));
            using (var fs = File.Create(Path.Combine(Config.Dir, "demo", name + ".png"))) enc.Save(fs);
        }

        public static void Quit()
        {
            Cleanup();
            Application.Current.Shutdown();
        }

        public static void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            try { WinKey.Uninstall(); } catch { }
            try { WindowAnimator.Uninstall(); } catch { }
            try { if (Lights != null) Lights.Stop(); } catch { }
            try { WindowGlass.RestoreAll(); } catch { }
            try { AppBar.RemoveAll(); } catch { }
            try { Taskbar.Restore(); } catch { }
        }

        public static void Log(object o)
        {
            try
            {
                File.AppendAllText(Path.Combine(Config.Dir, "log.txt"), DateTime.Now.ToString("s") + "  " + o + Environment.NewLine);
            }
            catch { }
        }
    }
}
