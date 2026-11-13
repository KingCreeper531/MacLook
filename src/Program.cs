using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Mac Look")]
[assembly: System.Reflection.AssemblyProduct("Mac Look")]
[assembly: System.Reflection.AssemblyDescription("Mac Look: a macOS-style desktop for Windows.")]
[assembly: System.Reflection.AssemblyCompany("Mac Look (github.com/KingCreeper531/MacLook)")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 KingCreeper531")]
[assembly: System.Reflection.AssemblyVersion("2.5.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.5.1.0")]

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

        public static bool IsFullscreen;
        private static System.Windows.Threading.DispatcherTimer applyTimer;

        /// <summary>What the desktop looks like behind the menu bar and dock (source for their glass).</summary>
        public static Pix Desktop;

        private static Mutex mutex;
        private static bool cleaned, started, recapturing, recapturePending;
        private static System.Windows.Threading.DispatcherTimer recaptureTimer;

        private const int StartupDelayMs = 700;          // let the app bars push windows aside before photographing the desktop
        private const int RecaptureDelayMs = 350;        // bars hidden this long before re-photographing the desktop
        private const int SystemChangeDebounceMs = 600;  // wallpaper / display changes arrive in bursts
        private const int ApplySettingsDebounceMs = 120;

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--restore", StringComparison.OrdinalIgnoreCase))
            {
                // Emergency: put the Windows taskbar (and Windows' minimize animation) back if Mac Look was killed.
                Taskbar.Restore();
                try { Settings.Load(); WindowAnimator.RestoreAfterCrash(); } catch { }
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
            Smooth.Init();
            Log("Animations run at " + Smooth.RefreshRate + " fps");

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
                    if (e.Category == UserPreferenceCategory.Desktop) RecaptureDesktopSoon();   // wallpaper changed
                }));
            SystemEvents.DisplaySettingsChanged += (s, e) =>
                app.Dispatcher.BeginInvoke(new Action(() => { MenuBar.Reposition(); Dock.Reposition(); RecaptureDesktopSoon(); }));

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

            // Reserve the menu bar and dock areas first (windows move out of the way), then photograph
            // the empty desktop there before showing anything, so the glass has something real to refract.
            new WindowInteropHelper(MenuBar).EnsureHandle();
            new WindowInteropHelper(Dock).EnsureHandle();
            if (Settings.HideTaskbar) Taskbar.Hide(); else Taskbar.Restore();   // also undoes a hidden taskbar left by a crash
            bool demo = Array.IndexOf(args, "--demo") >= 0;
            Launcher.Later(StartupDelayMs, () =>
            {
                started = true;
                if (IsFullscreen)
                {
                    // A game or video went fullscreen while we were starting: stay hidden (SetFullscreen(false)
                    // shows the bars later) and photograph the desktop once it is visible again.
                    recapturePending = true;
                }
                else
                {
                    Try(() => Desktop = ScreenCapture.Capture());   // no photo yet: the bars fall back to plain acrylic
                    MenuBar.Show();
                    MenuBar.Intro();
                    Dock.Show();
                }
                // Each piece starts on its own, so one failing (e.g. a bad Launchpad cache) can't silently
                // leave the Windows key, window animations or Mac buttons switched off.
                Try(AppCatalog.Load);
                Try(WinKey.Install);
                Try(WindowAnimator.Install);
                Try(HotCorners.Start);
                Try(WindowButtons.Start);
                if (demo) Demo(); else Try(Updater.Start);
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
            try { Settings.Load(); if (Wallpaper.CanRestore) Wallpaper.Restore(); } catch (Exception ex) { Log(ex); }
            Try(Taskbar.Restore);
            InstallInfo.Unregister(wipe, Config.Dir);
            if (!quiet) MessageBox.Show("Mac Look has been removed.", "Uninstall Mac Look", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Hide the bars while a game or video is fullscreen.</summary>
        public static void SetFullscreen(bool fs)
        {
            IsFullscreen = fs;
            if (!started || recapturing) return;   // RecaptureDesktop checks IsFullscreen when it finishes
            if (fs) { MenuBar.Hide(); Dock.Hide(); }
            else if (recapturePending) { recapturePending = false; RecaptureDesktop(); }
            else { MenuBar.Show(); Dock.Show(); }
        }

        /// <summary>Save settings and apply them everywhere, debounced so dragging a slider stays smooth.</summary>
        public static void ApplySettingsSoon()
        {
            if (applyTimer == null)
            {
                applyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ApplySettingsDebounceMs) };
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
                WindowButtons.Refresh();
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
            if (IsFullscreen)
            {
                // The screen shows a game or video, not the desktop: photograph it once fullscreen ends.
                recapturePending = true;
                return;
            }
            recapturing = true;
            MenuBar.Hide();
            Dock.Hide();
            Launcher.Later(RecaptureDelayMs, () =>
            {
                recapturing = false;
                if (IsFullscreen)
                {
                    // Went fullscreen while we were waiting: stay hidden and try again afterwards.
                    recapturePending = true;
                    return;
                }
                // Show the bars again even if the capture fails (they keep the previous photo then).
                try { Desktop = ScreenCapture.Capture(); } catch (Exception ex) { Log(ex); }
                MenuBar.Show();
                Dock.Show();
            });
        }

        /// <summary>Recapture once a burst of wallpaper / display change notifications has settled.</summary>
        private static void RecaptureDesktopSoon()
        {
            if (recaptureTimer == null)
            {
                recaptureTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SystemChangeDebounceMs) };
                recaptureTimer.Tick += (s, e) => { recaptureTimer.Stop(); RecaptureDesktop(); };
            }
            recaptureTimer.Stop();
            recaptureTimer.Start();
        }

        /// <summary>Self-test (--demo): logs which apps the Launchpad keeps and hides, then quits.</summary>
        private static void Demo()
        {
            var steps = new Action[]
            {
                () => { },
                () =>
                {
                    var all = AppCatalog.All;
                    var kept = all.Where(AppCatalog.IsRealApp).ToList();
                    Log("LAUNCHPAD kept " + kept.Count + " of " + all.Count);
                    Log("LAUNCHPAD hidden: " + string.Join(" | ", all.Where(a => !AppCatalog.IsRealApp(a)).Select(a => a.Name).ToArray()));
                },
                Quit
            };
            int i = 0;
            Action next = null;
            next = () =>
            {
                try { steps[i](); } catch (Exception ex) { Log(ex); }
                i++;
                if (i < steps.Length) Launcher.Later(1200, next);
            };
            Launcher.Later(1500, next);
        }

        public static void Quit()
        {
            Cleanup();
            Application.Current.Shutdown();
        }

        /// <summary>Put the user's desktop back. Every step runs even if an earlier one fails.</summary>
        public static void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            Try(WinKey.Uninstall);
            Try(WindowAnimator.Uninstall);
            Try(WindowButtons.Stop);
            Try(WindowGlass.RestoreAll);
            Try(AppBar.RemoveAll);
            Try(Taskbar.Restore);
        }

        /// <summary>Run one independent step, logging (not propagating) any failure.</summary>
        private static void Try(Action a)
        {
            try { a(); } catch (Exception ex) { Log(ex); }
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
