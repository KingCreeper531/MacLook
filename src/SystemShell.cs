using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MacLook
{
    /// <summary>Registers windows as "app bars" so maximized windows don't cover the menu bar or dock.</summary>
    internal static class AppBar
    {
        public static readonly uint CallbackMessage = Native.RegisterWindowMessage("MacLookAppBarMessage");
        private static readonly HashSet<IntPtr> registered = new HashSet<IntPtr>();

        private static Native.APPBARDATA Data(IntPtr hwnd)
        {
            var d = new Native.APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
            d.hWnd = hwnd;
            return d;
        }

        public static void Set(IntPtr hwnd, uint edge, int thicknessPx)
        {
            if (thicknessPx <= 0) { Remove(hwnd); return; }
            var d = Data(hwnd);
            if (!registered.Contains(hwnd))
            {
                d.uCallbackMessage = CallbackMessage;
                Native.SHAppBarMessage(Native.ABM_NEW, ref d);
                registered.Add(hwnd);
            }
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            d.uEdge = edge;
            d.rc.Left = b.Left;
            d.rc.Right = b.Right;
            if (edge == Native.ABE_TOP) { d.rc.Top = b.Top; d.rc.Bottom = b.Top + thicknessPx; }
            else { d.rc.Bottom = b.Bottom; d.rc.Top = b.Bottom - thicknessPx; }
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
            if (edge == Native.ABE_TOP) d.rc.Bottom = d.rc.Top + thicknessPx;
            else d.rc.Top = d.rc.Bottom - thicknessPx;
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref d);
        }

        public static void Remove(IntPtr hwnd)
        {
            if (!registered.Contains(hwnd)) return;
            var d = Data(hwnd);
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
            registered.Remove(hwnd);
        }

        public static void Forget() { registered.Clear(); }

        public static void RemoveAll()
        {
            foreach (var h in new List<IntPtr>(registered)) Remove(h);
        }
    }

    /// <summary>Hides the Windows taskbar and puts it back exactly as it was.</summary>
    internal static class Taskbar
    {
        private static readonly string StateFile = Path.Combine(Config.Dir, "taskbar-original-state.txt");
        public static bool Hidden { get; private set; }

        private const string TrayClass = "Shell_TrayWnd", SecondaryTrayClass = "Shell_SecondaryTrayWnd";
        private const int OBJID_WINDOW = 0;

        private static bool IsTrayClass(string cls) { return cls == TrayClass || cls == SecondaryTrayClass; }

        private static List<IntPtr> TrayWindows()
        {
            var list = new List<IntPtr>();
            IntPtr main = Native.FindWindow(TrayClass, null);
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, SecondaryTrayClass, null)) != IntPtr.Zero) list.Add(h);
            return list;
        }

        private static int GetState()
        {
            var d = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)) };
            return (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref d);
        }

        private static void SetState(int state)
        {
            var d = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)) };
            d.hWnd = Native.FindWindow(TrayClass, null);
            d.lParam = (IntPtr)state;
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref d);
        }

        public static void Hide()
        {
            if (Native.FindWindow(TrayClass, null) == IntPtr.Zero) return;
            if (!File.Exists(StateFile))
            {
                Directory.CreateDirectory(Config.Dir);
                File.WriteAllText(StateFile, GetState().ToString());
            }
            // Auto-hide frees the screen space the taskbar normally reserves; hiding the window removes it visually.
            SetState(Native.ABS_AUTOHIDE);
            foreach (var h in TrayWindows())
            {
                Native.ShowWindow(h, Native.SW_HIDE);
                // An empty window region makes it invisible even in the instant before we re-hide it.
                // The window owns the region once SetWindowRgn succeeds; otherwise it is ours to free.
                IntPtr rgn = Native.CreateRectRgn(0, 0, 0, 0);
                if (rgn != IntPtr.Zero && Native.SetWindowRgn(h, rgn, true) == 0) Native.DeleteObject(rgn);
            }
            Hidden = true;
            HookShowEvents();
        }

        private static IntPtr showHook;
        private static Native.WinEventDelegate showProc;

        /// <summary>Re-hide the taskbar the moment Windows tries to show it (Windows key, Start, notifications).</summary>
        private static void HookShowEvents()
        {
            if (showHook != IntPtr.Zero) { Native.UnhookWinEvent(showHook); showHook = IntPtr.Zero; }
            IntPtr tray = Native.FindWindow(TrayClass, null);
            if (tray == IntPtr.Zero) return;
            uint pid;
            Native.GetWindowThreadProcessId(tray, out pid);
            showProc = (hook, ev, hwnd, idObject, idChild, thread, time) =>
            {
                // A WinEvent callback must never throw, and must not block on Explorer (hence ShowWindowAsync).
                try
                {
                    if (!Hidden || idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
                    if (IsTrayClass(Native.GetClass(hwnd))) Native.ShowWindowAsync(hwnd, Native.SW_HIDE);
                }
                catch (Exception ex) { Program.Log(ex); }
            };
            showHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, showProc, pid, 0, 0);
        }

        /// <summary>
        /// Safety net on a timer, and called from the keyboard hook thread the instant the Windows key goes down,
        /// so it only posts the hide: it must never wait on a busy Explorer.
        /// </summary>
        public static void Enforce()
        {
            if (!Hidden) return;
            foreach (var h in TrayWindows())
                if (Native.IsWindowVisible(h)) Native.ShowWindowAsync(h, Native.SW_HIDE);
        }

        public static void Restore()
        {
            Hidden = false;
            if (showHook != IntPtr.Zero) { Native.UnhookWinEvent(showHook); showHook = IntPtr.Zero; }
            foreach (var h in TrayWindows())
            {
                Native.SetWindowRgn(h, IntPtr.Zero, true);
                Native.ShowWindow(h, Native.SW_SHOWNA);
            }
            if (File.Exists(StateFile))
            {
                int state;
                string saved = null;
                try { saved = File.ReadAllText(StateFile).Trim(); } catch (Exception ex) { Program.Log(ex); return; }   // keep the file for next time
                if (int.TryParse(saved, out state)) SetState(state);
                try { File.Delete(StateFile); } catch (Exception ex) { Program.Log(ex); }
            }
        }
    }

    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "MacLook";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(Name, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else if (k.GetValue(Name) != null) k.DeleteValue(Name);
                }
            }
        }
    }

    internal static class Wallpaper
    {
        public static void SetMacStyle()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            string path = Path.Combine(Config.Dir, "MacLook-wallpaper.png");
            Generate(b.Width, b.Height, path);

            if (string.IsNullOrEmpty(Settings.OriginalWallpaper))
            {
                var sb = new StringBuilder(1024);
                Native.SystemParametersInfoGet(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
                string current = sb.ToString();
                if (!string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                {
                    Settings.OriginalWallpaper = current;
                    Settings.Save();
                }
            }
            Native.SystemParametersInfoSet(Native.SPI_SETDESKWALLPAPER, 0, path, 3);
        }

        /// <summary>Use any picture as the wallpaper (remembering the original so it can be restored).</summary>
        public static void SetImage(string path)
        {
            if (string.IsNullOrEmpty(Settings.OriginalWallpaper))
            {
                var sb = new StringBuilder(1024);
                Native.SystemParametersInfoGet(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
                Settings.OriginalWallpaper = sb.ToString();
                Settings.Save();
            }
            Native.SystemParametersInfoSet(Native.SPI_SETDESKWALLPAPER, 0, path, 3);
        }

        public static bool CanRestore { get { return !string.IsNullOrEmpty(Settings.OriginalWallpaper); } }

        public static void Restore()
        {
            if (!CanRestore) return;
            Native.SystemParametersInfoSet(Native.SPI_SETDESKWALLPAPER, 0, Settings.OriginalWallpaper, 3);
            Settings.OriginalWallpaper = "";
            Settings.Save();
        }

        private static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        /// <summary>Original abstract "rolling hills at dusk" wallpaper, drawn from scratch.</summary>
        public static void Generate(int w, int h, string path)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sky.GradientStops.Add(new GradientStop(C("#16205C"), 0));
                sky.GradientStops.Add(new GradientStop(C("#4D47C2"), 0.42));
                sky.GradientStops.Add(new GradientStop(C("#D987B4"), 0.70));
                sky.GradientStops.Add(new GradientStop(C("#FFC19A"), 0.95));
                dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

                var glow = new RadialGradientBrush();
                glow.GradientStops.Add(new GradientStop(C("#99FFE0B8"), 0));
                glow.GradientStops.Add(new GradientStop(C("#00FFE0B8"), 1));
                dc.DrawEllipse(glow, null, new Point(w * 0.64, h * 0.58), w * 0.42, h * 0.30);

                Hill(dc, w, h, 0.50, new[] { 0.06, -0.05, 0.03, -0.07, 0.04, -0.02 }, "#9C8BEB", "#5E4CC2");
                Hill(dc, w, h, 0.60, new[] { -0.04, 0.07, -0.06, 0.03, -0.05, 0.06 }, "#D27BCB", "#7A3AA6");
                Hill(dc, w, h, 0.72, new[] { 0.05, -0.06, 0.06, -0.03, 0.07, -0.04 }, "#F59A73", "#B1446F");
                Hill(dc, w, h, 0.85, new[] { -0.04, 0.04, -0.06, 0.05, -0.03, 0.05 }, "#3C2F92", "#120F45");
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        private static void Hill(DrawingContext dc, int w, int h, double baseY, double[] waves, string top, string bottom)
        {
            int n = waves.Length;
            var pts = new Point[n];
            for (int i = 0; i < n; i++) pts[i] = new Point(w * i / (double)(n - 1), h * (baseY + waves[i]));
            var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
            for (int i = 0; i < n - 1; i++)
            {
                double dx = (pts[i + 1].X - pts[i].X) / 2;
                fig.Segments.Add(new BezierSegment(new Point(pts[i].X + dx, pts[i].Y), new Point(pts[i + 1].X - dx, pts[i + 1].Y), pts[i + 1], true));
            }
            fig.Segments.Add(new LineSegment(new Point(w, h), false));
            fig.Segments.Add(new LineSegment(new Point(0, h), false));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var fill = new LinearGradientBrush(C(top), C(bottom), new Point(0, 0), new Point(0, 1));
            dc.DrawGeometry(fill, null, geo);
        }
    }

    internal static class Power
    {
        private static void Run(string args)
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
        }

        private static bool Confirm(string text)
        {
            return MessageBox.Show(text, "Mac Look", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        public static void Sleep() { System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); }
        public static void Restart() { if (Confirm("Are you sure you want to restart your computer now?")) Run("/r /t 0"); }
        public static void ShutDown() { if (Confirm("Are you sure you want to shut down your computer now?")) Run("/s /t 0"); }
        public static void LogOut() { if (Confirm("Are you sure you want to quit all apps and log out now?")) Run("/l"); }
        public static void Lock() { Native.LockWorkStation(); }
    }

    internal static class Launcher
    {
        public static void Open(string target, string args = "")
        {
            try
            {
                switch (target.ToLowerInvariant())
                {
                    case "maclook:launchpad": Program.Launchpad.Toggle(); return;
                    case "maclook:missioncontrol": Program.MissionControl.Toggle(); return;
                    case "maclook:controlcenter": Program.ControlCenter.Toggle(); return;
                    case "maclook:spotlight": Program.Spotlight.Toggle(); return;
                }
                if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("::"))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
                    return;
                }
                var psi = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "" };
                string dir = null;
                try { if (File.Exists(target)) dir = Path.GetDirectoryName(target); } catch { }
                if (dir != null) psi.WorkingDirectory = dir;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't open " + target + "\n\n" + ex.Message, "Mac Look", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void Later(int ms, Action a)
        {
            // Normal priority: the default (Background) waits behind every animation frame, and while
            // something animates at 180 fps a "50 ms" timer could fire a second late.
            var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); a(); };
            t.Start();
        }
    }
}
