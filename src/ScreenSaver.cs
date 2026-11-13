using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MacLook
{
    /// <summary>
    /// The screen saver: an aerial flyover (or the Retro line saver) with a big lock-screen-style clock,
    /// on every monitor. It is only a screen saver: it never locks or asks for a password, and any input
    /// ends it with the "unlock" transition (the flight slows to a stop, the picture settles, then fades
    /// away while the menu bar and Dock glide back in).
    /// </summary>
    internal static class ScreenSaver
    {
        // Exit timeline, in seconds after the input that ends it.
        private const double DecelSec = 0.6;       // the camera eases to a stop
        private const double SettleSec = 0.4;      // colours gain a little punch
        private const double FadeStartSec = 0.30, FadeSec = 0.50;
        private const double IntroAtSec = 0.36;    // menu bar and Dock start coming back
        private const double ExitEndSec = 0.82;
        private const double ClockOutStartSec = 0.08, ClockOutSec = 0.36;
        private const double SpringRate = 14;      // critically damped zoom: crisp, no wobble
        private const double ExitZoom = 0.07;

        private const double FadeInSec = 0.7;
        private const double ArmSec = 0.6;         // ignore input this long after showing (our own focus key press)
        private const double MoveDip = 6;          // mouse must move this far to count
        private const int TickMs = 500;            // clock text, auto lighting, staying on top
        private const int WatchMs = 4000;          // idle check

        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);

        private static readonly List<ScreenSaverWindow> windows = new List<ScreenSaverWindow>();
        private static readonly Stopwatch clock = new Stopwatch();
        private static string style = "aerial", lighting = "auto";
        private static bool looping, exiting, introDone, armed;
        private static double lastFrame, sim, exitAt, scale = 1;
        private static uint armInput;
        private static Native.POINT armPos, lastPos;
        private static DispatcherTimer tick, watch;
        private static uint triggeredFor;
        private static bool locked, sessionHooked;
        private static Dispatcher ui;

        public static bool IsShowing { get { return windows.Count > 0; } }

        /// <summary>Start the screen saver now, as set in Settings.</summary>
        public static void Show() { Open(Settings.ScreenSaverStyle, Settings.ScreenSaverLighting); }

        /// <summary>Show a style ("aerial" | "retro") and lighting ("auto" | "morning" | "day" | "evening" | "night").
        /// Already showing the same style: the lighting cross-fades instead of starting over.</summary>
        public static void Preview(string style, string lighting) { Open(style, lighting); }

        /// <summary>Watch for idle time; shows the saver after Settings.ScreenSaverMinutes (read every check, 0 = never).</summary>
        public static void Start()
        {
            try
            {
                ui = Dispatcher.CurrentDispatcher;
                if (watch == null)
                {
                    watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(WatchMs) };
                    watch.Tick += (s, e) => WatchTick();
                }
                watch.Start();
                if (!sessionHooked) { SystemEvents.SessionSwitch += OnSessionSwitch; sessionHooked = true; }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Stop watching and take the saver down at once (no transition). Safe to call any time.</summary>
        public static void Stop()
        {
            try
            {
                if (watch != null) watch.Stop();
                if (sessionHooked) { SystemEvents.SessionSwitch -= OnSessionSwitch; sessionHooked = false; }
            }
            catch (Exception ex) { Program.Log(ex); }
            CloseNow();
        }

        private static string Style(string s) { return s == "retro" ? "retro" : "aerial"; }

        private static string Lighting(string l)
        {
            return l == "morning" || l == "day" || l == "evening" || l == "night" ? l : "auto";
        }

        private static double Now { get { return clock.Elapsed.TotalSeconds; } }

        private static void Open(string st, string li)
        {
            try
            {
                st = Style(st);
                li = Lighting(li);
                if (windows.Count > 0)
                {
                    if (exiting) return;   // already on its way out: let it finish
                    if (st == style)
                    {
                        lighting = li;
                        foreach (var w in windows) w.SetLighting(li, Now);
                        return;
                    }
                    CloseNow();
                }
                style = st; lighting = li;
                scale = SystemScale();

                int seed = Environment.TickCount;
                var screens = System.Windows.Forms.Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    var w = new ScreenSaverWindow(screens[i].Bounds, scale, st, li, screens[i].Primary, seed + i * 7919);
                    w.Closed += (s, e) => Forget((ScreenSaverWindow)s);
                    windows.Add(w);
                }
                exiting = false; introDone = false; armed = false;
                sim = 0; lastFrame = 0;
                clock.Restart();
                foreach (var w in windows) w.Frame(0, 0, 0, 0, 1, 1, 0);   // first frame ready before it appears
                foreach (var w in windows) w.Show();
                var main = windows.Find(x => x.Primary) ?? windows[0];
                main.TakeFocus();
                StartLoop();
                if (tick == null)
                {
                    tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickMs) };
                    tick.Tick += (s, e) => Tick();
                }
                tick.Start();
            }
            catch (Exception ex) { Program.Log(ex); CloseNow(); }
        }

        /// <summary>WPF here is system-DPI aware: one scale for every window.</summary>
        private static double SystemScale()
        {
            try
            {
                var src = Program.MenuBar == null ? null : PresentationSource.FromVisual(Program.MenuBar);
                if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11;
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
            }
            catch { return 1; }
        }

        /// <summary>Any input ends it: start the exit transition (later calls do nothing).</summary>
        public static void Dismiss()
        {
            if (windows.Count == 0 || exiting) return;
            exiting = true;
            exitAt = Now;
            foreach (var w in windows) w.ShowCursor();
        }

        internal static bool Armed { get { return armed; } }

        private static void StartLoop()
        {
            if (looping) return;
            looping = true;
            CompositionTarget.Rendering += OnRendering;
            Smooth.Begin();
        }

        private static void StopLoop()
        {
            if (!looping) return;
            looping = false;
            CompositionTarget.Rendering -= OnRendering;
            Smooth.End();
        }

        private static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        private static void OnRendering(object sender, EventArgs e)
        {
            try
            {
                if (windows.Count == 0) { StopLoop(); return; }
                double now = Now;
                double dt = Math.Max(0, Math.Min(0.1, now - lastFrame));   // real time, capped after a hitch
                lastFrame = now;

                double speed = 1, settle = 0, alpha = Ease(now / FadeInSec), zoom = 1, clockAlpha = 1, clockLift = 0;
                double ex = 0;
                if (exiting)
                {
                    ex = now - exitAt;
                    double d = Math.Min(1, ex / DecelSec);
                    speed = (1 - d) * (1 - d);                              // velocity eases smoothly to zero
                    settle = Ease(ex / SettleSec);
                    double f = ex - FadeStartSec;
                    alpha *= 1 - Ease(f / FadeSec);
                    if (f > 0) zoom = 1 + ExitZoom * (1 - (1 + SpringRate * f) * Math.Exp(-SpringRate * f));
                    double c = Ease((ex - ClockOutStartSec) / ClockOutSec);
                    clockAlpha = 1 - c;
                    clockLift = c;
                }
                else CheckInput(now);
                sim += dt * speed;

                foreach (var w in windows.ToArray()) w.Frame(sim, now, settle, alpha, zoom, clockAlpha, clockLift);

                if (exiting && !introDone && ex >= IntroAtSec) { introDone = true; BringBarsBack(); }
                if (exiting && ex >= ExitEndSec) CloseNow();
            }
            catch (Exception err)
            {
                Program.Log(err);
                CloseNow();
            }
        }

        /// <summary>
        /// Mouse movement is read from the cursor itself, and input we never saw (keys typed into another
        /// window that stole the focus) from the system's last-input time.
        /// </summary>
        private static void CheckInput(double now)
        {
            if (now < ArmSec) return;
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref lii)) return;
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            if (!armed)
            {
                armed = true;
                armInput = lii.dwTime; armPos = p; lastPos = p;
                return;
            }
            double dx = p.X - armPos.X, dy = p.Y - armPos.Y, far = MoveDip * scale;
            if (dx * dx + dy * dy > far * far) { Dismiss(); return; }
            if (lii.dwTime != armInput)
            {
                bool moved = p.X != lastPos.X || p.Y != lastPos.Y;
                // Not a mouse move and not delivered to us (we'd have seen a key or click): it went elsewhere.
                if (!moved && !OwnsForeground()) { Dismiss(); return; }
                armInput = lii.dwTime;
                lastPos = p;
            }
        }

        private static bool OwnsForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            foreach (var w in windows) if (w.Handle == fg) return true;
            return false;
        }

        private static void BringBarsBack()
        {
            if (Program.IsFullscreen) return;
            try { if (Program.MenuBar != null && Program.MenuBar.IsVisible) Program.MenuBar.Intro(); } catch (Exception ex) { Program.Log(ex); }
            try { if (Program.Dock != null) Program.Dock.Intro(); } catch (Exception ex) { Program.Log(ex); }
        }

        private static void Tick()
        {
            try
            {
                if (windows.Count == 0) { tick.Stop(); return; }
                foreach (var w in windows)
                {
                    w.UpdateClock();
                    if (lighting == "auto" && !exiting) w.SetLighting("auto", Now);   // crossing 5:00, 10:00, 17:00 or 21:00 cross-fades
                    w.KeepOnTop();
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static void Forget(ScreenSaverWindow w)
        {
            windows.Remove(w);
            if (windows.Count > 0) return;
            StopLoop();
            if (tick != null) tick.Stop();
            exiting = false;
        }

        /// <summary>Take every window down now and stop all frame work.</summary>
        private static void CloseNow()
        {
            StopLoop();
            if (tick != null) tick.Stop();
            var list = windows.ToArray();
            windows.Clear();
            exiting = false;
            foreach (var w in list)
            {
                try { w.CloseForGood(); } catch (Exception ex) { Program.Log(ex); }
            }
        }

        private static void WatchTick()
        {
            try
            {
                int minutes = Settings.ScreenSaverMinutes;
                if (minutes <= 0 || windows.Count > 0 || locked || Program.IsFullscreen) return;
                var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (!GetLastInputInfo(ref lii)) return;
                uint idle = unchecked((uint)Environment.TickCount - lii.dwTime);
                if (idle < (uint)minutes * 60000u) return;
                if (lii.dwTime == triggeredFor) return;   // already shown for this idle spell (e.g. then locked)
                triggeredFor = lii.dwTime;
                Show();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Locked or switched away: the saver can't be seen, so stop drawing it.</summary>
        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            var d = ui;
            if (d == null) return;
            d.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                    {
                        locked = true;
                        CloseNow();
                    }
                    else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                        locked = false;
                }
                catch (Exception ex) { Program.Log(ex); }
            }));
        }
    }

    /// <summary>One monitor's screen saver: the scene, the clock over it, and the input that ends it.</summary>
    internal sealed class ScreenSaverWindow : Window
    {
        private const long WS_EX_LAYERED = 0x80000;
        private const uint LWA_ALPHA = 2;
        private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;   // Windows 10 2004+; harmlessly fails before

        public readonly bool Primary;
        private readonly System.Drawing.Rectangle bounds;
        private readonly double w, h;
        private readonly SaverScene scene;
        private readonly Grid root = new Grid();
        private readonly ScaleTransform zoom;
        private readonly FrameworkElement clockBox;
        private readonly TranslateTransform clockShift = new TranslateTransform();
        private readonly TextBlock timeText, dateText;
        private IntPtr hwnd;
        private int alpha = -1;
        private bool closeOk;

        public IntPtr Handle { get { return hwnd; } }

        public ScreenSaverWindow(System.Drawing.Rectangle bounds, double scale, string style, string lighting, bool primary, int seed)
        {
            this.bounds = bounds;
            Primary = primary;
            Title = "Screen Saver";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = primary;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Brushes.Black;
            Cursor = Cursors.None;
            ForceCursor = true;
            FontFamily = new FontFamily(Theme.UiFont);

            w = bounds.Width / scale; h = bounds.Height / scale;
            Left = bounds.X / scale; Top = bounds.Y / scale; Width = w; Height = h;

            scene = style == "retro" ? (SaverScene)new RetroScene(w, h, scale, seed) : new AerialScene(w, h, seed, lighting);
            root.Width = w; root.Height = h;
            root.ClipToBounds = true;
            zoom = new ScaleTransform(1, 1, w / 2, h / 2);
            root.RenderTransform = zoom;
            root.Children.Add(scene.Root);

            if (scene.WantsClock)
            {
                // A soft darkening behind the clock and at the corners: legible on a bright sky, cinematic.
                var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                shade.GradientStops.Add(new GradientStop(Color.FromArgb(46, 0, 0, 0), 0));
                shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
                shade.Freeze();
                // Only as tall as the shade reaches: every full-screen layer costs fill rate at 4K / 180 Hz.
                root.Children.Add(new System.Windows.Shapes.Rectangle { Fill = shade, IsHitTestVisible = false, Height = h * 0.42, VerticalAlignment = VerticalAlignment.Top });
                var vignette = new RadialGradientBrush { RadiusX = 0.78, RadiusY = 0.78 };
                vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.6));
                vignette.GradientStops.Add(new GradientStop(Color.FromArgb(70, 0, 0, 0), 1));
                vignette.Freeze();
                root.Children.Add(new System.Windows.Shapes.Rectangle { Fill = vignette, IsHitTestVisible = false });

                dateText = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe UI Variable Display Semibold, Segoe UI Semibold, Segoe UI"),
                    FontWeight = FontWeights.SemiBold, FontSize = Math.Round(h * 0.024),
                    Foreground = Frozen(Color.FromArgb(235, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                timeText = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe UI Variable Display Light, Segoe UI Light, Segoe UI"),
                    FontWeight = FontWeights.Light, FontSize = Math.Round(h * 0.165),
                    Foreground = Frozen(Color.FromArgb(222, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, -h * 0.022, 0, 0)
                };
                TextOptions.SetTextFormattingMode(timeText, TextFormattingMode.Ideal);
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(dateText);
                stack.Children.Add(timeText);
                stack.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 2, Direction = 270, Opacity = 0.32, Color = Colors.Black };
                // Cached as a bitmap: the shadow is drawn once a minute, not on every frame of the flight.
                var box = new Border { Child = stack, Padding = new Thickness(40), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
                box.Margin = new Thickness(0, h * 0.075 - 40, 0, 0);
                box.CacheMode = new BitmapCache { RenderAtScale = Math.Max(1, scale), SnapsToDevicePixels = true };
                box.RenderTransform = clockShift;
                box.IsHitTestVisible = false;
                clockBox = box;
                root.Children.Add(box);

                var hint = new TextBlock
                {
                    Text = "Move the mouse or press any key", FontSize = Math.Max(12, Math.Round(h * 0.0125)),
                    Foreground = Frozen(Color.FromArgb(150, 255, 255, 255)), IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, h * 0.045)
                };
                root.Children.Add(hint);
                UpdateClock();
            }
            Content = root;

            SourceInitialized += OnSourceInitialized;
            PreviewKeyDown += (s, e) => { e.Handled = true; if (ScreenSaver.Armed) ScreenSaver.Dismiss(); };
            PreviewKeyUp += (s, e) => { e.Handled = true; };
            // Clicks end it but never reach the app underneath: this window takes them.
            PreviewMouseDown += (s, e) => { e.Handled = true; ScreenSaver.Dismiss(); };
            PreviewMouseUp += (s, e) => { e.Handled = true; };
            PreviewMouseWheel += (s, e) => { e.Handled = true; ScreenSaver.Dismiss(); };
            PreviewStylusDown += (s, e) => { ScreenSaver.Dismiss(); };
            Closing += (s, e) =>
            {
                // Alt+F4 or the like: leave the nice way instead.
                if (closeOk) return;
                e.Cancel = true;
                ScreenSaver.Dismiss();
            };
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                hwnd = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                // Not in Alt+Tab, skipped by the window animations and Mac buttons; layered so it can fade.
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | WS_EX_LAYERED));
                SetAlpha(0);
                // Invisible to screen grabs: the Dock and menu bar keep refracting the quiet desktop behind us
                // (not a moving landscape they would re-render all night), and Dock.Intro's photo is clean.
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                // Exact device pixels, whatever the rounding of the DIP size.
                Native.SetWindowPos(hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public void TakeFocus()
        {
            try
            {
                Activate();
                if (hwnd != IntPtr.Zero && Native.GetForegroundWindow() != hwnd) Native.ActivateWindow(hwnd);
                Focus();
                Keyboard.Focus(this);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public void ShowCursor() { Cursor = null; ForceCursor = false; }

        public void KeepOnTop()
        {
            if (hwnd != IntPtr.Zero) Native.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void SetLighting(string lighting, double now) { scene.SetLighting(lighting, now); }

        public void UpdateClock()
        {
            if (timeText == null) return;
            var now = DateTime.Now;
            string t = now.ToString(Settings.Clock24h ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
            string d = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
            if (timeText.Text != t) timeText.Text = t;
            if (dateText.Text != d) dateText.Text = d;
        }

        /// <summary>One frame: the scene, the window's fade, the exit zoom and the clock leaving first.</summary>
        public void Frame(double sim, double now, double settle, double fade, double scaleUp, double clockAlpha, double clockLift)
        {
            scene.Frame(sim, now, settle);
            if (zoom.ScaleX != scaleUp) { zoom.ScaleX = scaleUp; zoom.ScaleY = scaleUp; }
            if (clockBox != null)
            {
                clockBox.Opacity = clockAlpha;
                clockShift.Y = -clockLift * h * 0.03;
            }
            SetAlpha((int)Math.Round(Math.Max(0, Math.Min(1, fade)) * 255));
        }

        private void SetAlpha(int a)
        {
            if (a == alpha || hwnd == IntPtr.Zero) return;
            alpha = a;
            SetLayeredWindowAttributes(hwnd, 0, (byte)a, LWA_ALPHA);
        }

        public void CloseForGood()
        {
            closeOk = true;
            Close();
        }
    }
}
