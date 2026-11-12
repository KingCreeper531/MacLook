using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Mac Look")]
[assembly: System.Reflection.AssemblyProduct("Mac Look")]
[assembly: System.Reflection.AssemblyVersion("2.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.2.0.0")]

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
                if (Array.IndexOf(args, "--demo") < 0) Updater.Start();
                if (Array.IndexOf(args, "--demo") >= 0) Demo();
            });

            app.Run();
        }

        /// <summary>Hide the bars while a game or video is fullscreen.</summary>
        public static void SetFullscreen(bool fs)
        {
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
                () => Native.SetCursorPos(cx, b.Bottom - 1), () => SaveDockArea("1-dock-icons"),
                () => Native.SetCursorPos(orig.X, orig.Y),
                () => WifiMenu.Toggle(), () => { }, () => { }, () => SaveCorner("2-wifi-menu"), () => WifiMenu.HidePanel(),
                () => SoundMenu.Toggle(), () => SaveCorner("3-sound-menu"), () => SoundMenu.HidePanel(),
                Quit
            };            int i = 0;
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
