using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>
    /// Mac-style close / minimize / zoom buttons on every app window.
    /// Each window gets its own small overlay that is *owned* by that window, so it sits right above it,
    /// moves up and down the stack with it, and is hidden by whatever covers the window.
    /// No code goes into other apps; clicks send the window its own close / minimize / maximize command.
    /// </summary>
    internal static class WindowButtons
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17;
        private const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const uint WINEVENT_SKIPOWNPROCESS = 2;
        private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        private const int GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000;
        private const int SyncEveryMs = 300, SyncSoonMs = 15;

        private static readonly Dictionary<IntPtr, ButtonsOverlay> overlays = new Dictionary<IntPtr, ButtonsOverlay>();
        private static DispatcherTimer timer, soon;
        private static Native.WinEventDelegate proc;
        private static IntPtr sysHook, showHook, moveHook;
        private static bool running;

        public static void Start()
        {
            if (running) Stop();
            running = true;
            proc = OnEvent;
            sysHook = Native.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, proc, 0, 0, WINEVENT_SKIPOWNPROCESS);
            showHook = Native.SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, proc, 0, 0, WINEVENT_SKIPOWNPROCESS);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SyncEveryMs) };
            timer.Tick += (s, e) => Sync();
            timer.Start();
            soon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SyncSoonMs) };
            soon.Tick += (s, e) => { soon.Stop(); Sync(); };
            SetActive(Native.GetForegroundWindow());
            Sync();
        }

        public static void Stop()
        {
            // After this nothing may touch other apps' windows again (a queued Sync would re-add overlays).
            running = false;
            if (timer != null) timer.Stop();
            if (soon != null) soon.Stop();
            foreach (var h in new[] { sysHook, showHook, moveHook }) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            sysHook = showHook = moveHook = IntPtr.Zero;
            foreach (var h in overlays.Keys.ToList()) Remove(h);
        }

        /// <summary>Settings changed (Replace / Left / Off).</summary>
        public static void Refresh()
        {
            foreach (var h in overlays.Keys.ToList()) Remove(h);
            Sync();
        }

        private static void SyncSoon() { if (soon != null) { soon.Stop(); soon.Start(); } }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr h, int idObject, int idChild, uint thread, uint time)
        {
            if (!running || idObject != 0 || h == IntPtr.Zero) return;
            // Called from inside the message loop: nothing may escape from here.
            try
            {
                ButtonsOverlay o;
                switch (ev)
                {
                    case EVENT_SYSTEM_FOREGROUND:
                        SetActive(h);
                        SyncSoon();
                        break;
                    case EVENT_SYSTEM_MINIMIZESTART:
                        if (overlays.TryGetValue(h, out o)) o.HideOverlay();
                        break;
                    case EVENT_OBJECT_SHOW:
                        // Child controls appear all the time in every app; only top-level windows matter here.
                        if ((Native.GetWindowLongPtr(h, GWL_STYLE).ToInt64() & WS_CHILD) != 0) break;
                        SyncSoon();   // a new window: cover its buttons right away
                        break;
                    case EVENT_SYSTEM_MINIMIZEEND:
                        SyncSoon();   // a restored window: cover its buttons right away
                        break;
                    case EVENT_OBJECT_HIDE:
                    case EVENT_OBJECT_DESTROY:
                        if (overlays.ContainsKey(h)) Remove(h);
                        break;
                    case EVENT_OBJECT_LOCATIONCHANGE:
                        if (overlays.TryGetValue(h, out o)) o.Position();
                        break;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static void SetActive(IntPtr h)
        {
            if (h == IntPtr.Zero || WindowTracker.IsOwn(h)) return;   // our own menus / panels don't count
            foreach (var kv in overlays) kv.Value.SetActive(kv.Key == h);
            // Follow the active window's moves smoothly (its process only, to stay light).
            if (moveHook != IntPtr.Zero) { Native.UnhookWinEvent(moveHook); moveHook = IntPtr.Zero; }
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid != 0) moveHook = Native.SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, proc, pid, 0, WINEVENT_SKIPOWNPROCESS);
        }

        private static void Sync()
        {
            if (!running) return;
            if (Settings.WindowButtons == "off" || Program.IsFullscreen)
            {
                foreach (var h in overlays.Keys.ToList()) Remove(h);
                return;
            }
            List<AppWindow> wins;
            try { wins = WindowTracker.Enumerate(); } catch { return; }
            IntPtr fg = Native.GetForegroundWindow();
            var alive = new HashSet<IntPtr>();
            foreach (var w in wins)
            {
                if (Native.IsIconic(w.Hwnd) || !Eligible(w.Hwnd)) continue;
                alive.Add(w.Hwnd);
                ButtonsOverlay o;
                if (!overlays.TryGetValue(w.Hwnd, out o))
                {
                    try
                    {
                        o = new ButtonsOverlay(w.Hwnd);
                        overlays[w.Hwnd] = o;
                        // Windows' own close animation would replay a picture with its old buttons; Mac Look's shows the Mac ones.
                        int on = 1;
                        Native.DwmSetWindowAttribute(w.Hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, 4);
                        IntPtr key = w.Hwnd;
                        // Closed on its own (e.g. its app window was destroyed, which destroys owned windows too):
                        // forget it, unless a newer overlay has already taken its place.
                        o.Closed += (s, e) =>
                        {
                            ButtonsOverlay cur;
                            if (overlays.TryGetValue(key, out cur) && cur == s) { overlays.Remove(key); RestoreTransitions(key); }
                        };
                    }
                    catch (Exception ex) { Program.Log(ex); continue; }
                }
                o.SetActive(w.Hwnd == fg);
                o.Position();
            }
            foreach (var h in overlays.Keys.ToList()) if (!alive.Contains(h)) Remove(h);
        }

        private static void Remove(IntPtr h)
        {
            ButtonsOverlay o;
            if (!overlays.TryGetValue(h, out o)) return;
            overlays.Remove(h);
            RestoreTransitions(h);
            try { o.Close(); } catch (Exception ex) { Program.Log(ex); }
        }

        private static void RestoreTransitions(IntPtr h)
        {
            if (!Native.IsWindow(h)) return;
            int off = 0;
            Native.DwmSetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, ref off, 4);
        }

        public static bool Eligible(IntPtr h)
        {
            if (WindowTracker.IsOwn(h) || WindowTracker.IsShellWindow(h) || !Native.IsWindowVisible(h)) return false;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            long style = Native.GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            const long WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000;
            if ((style & WS_SYSMENU) == 0 && (style & WS_CAPTION) != WS_CAPTION) return false;
            return Native.GetWindowTextLength(h) > 0;
        }
    }

    /// <summary>The red / yellow / green buttons for one window (gray when that window isn't active, like macOS).</summary>
    /// <summary>
    /// What the Mac window buttons look like and where they go, shared by the live overlay and by the
    /// window pictures used for animations (WindowAnimator), so both always match.
    /// </summary>
    internal static class MacButtons
    {
        public const double Diameter = 12, Gap = 8, RightMargin = 14;
        public static readonly Color[] Fill = { Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0x28, 0xC8, 0x40) };
        public static readonly Color[] Rim = { Color.FromRgb(0xE0, 0x44, 0x3E), Color.FromRgb(0xDE, 0xA1, 0x23), Color.FromRgb(0x1A, 0xAB, 0x29) };

        // Title bar samples: this far left of the buttons (DIPs) ...
        private static readonly int[] SampleLeft = { 4, 16, 34 };
        // ... and this far above / below their middle.
        private static readonly int[] SampleDown = { -8, 0, 8 };
        public const int SampleReach = 34, SampleSpread = 8;

        /// <summary>Reads one pixel (any packed 0x??RRGGBB-style int); false when it can't be read.</summary>
        public delegate bool PixelReader(int x, int y, out int rgb);

        /// <summary>
        /// Most common of nine samples just left of the buttons (x = their left edge, y = their middle,
        /// scale = DPI factor), so an icon or hover highlight can't throw it off. The top byte is dropped.
        /// Returns false when no sample could be read.
        /// </summary>
        public static bool SampleTitleBar(int x, int y, double scale, PixelReader read, out int rgb)
        {
            rgb = 0;
            var counts = new Dictionary<int, int>();
            foreach (int ox in SampleLeft)
                foreach (int oy in SampleDown)
                {
                    int c;
                    if (!read(x - (int)(ox * scale), y + (int)(oy * scale), out c)) continue;
                    c &= 0xFFFFFF;
                    int n; counts.TryGetValue(c, out n); counts[c] = n + 1;
                }
            if (counts.Count == 0) return false;
            rgb = counts.OrderByDescending(kv => kv.Value).First().Key;
            return true;
        }
    }

    /// <summary>The red / yellow / green buttons for one window (gray when that window isn't active, like macOS).</summary>
    internal class ButtonsOverlay : Window
    {
        private const int SC_CLOSE = 0xF060, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5, GWLP_HWNDPARENT = -8;

        private readonly IntPtr target;
        private readonly double scale = 1;
        private readonly Ellipse[] dots = new Ellipse[3];
        private readonly TextBlock[] symbols = new TextBlock[3];
        private readonly StackPanel row;
        private readonly Border patch;
        private bool isActive, hover, sampled, closed;
        private Native.RECT lastRect;
        private DateTime lastSample = DateTime.MinValue, lastMove = DateTime.MinValue;

        public ButtonsOverlay(IntPtr target)
        {
            this.target = target;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;

            row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 3; i++) row.Children.Add(Light(i));
            row.MouseEnter += (s, e) => { hover = true; Paint(); };
            row.MouseLeave += (s, e) => { hover = false; Paint(); };
            patch = new Border { Child = row };
            Content = patch;

            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            scale = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            // Owned by the app window: always directly above it, never above other windows.
            Native.SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, target);
            Paint();
        }

        protected override void OnClosed(EventArgs e)
        {
            closed = true;
            base.OnClosed(e);
        }

        /// <summary>Hide() that is safe after the window was closed (e.g. destroyed together with its app window).</summary>
        public void HideOverlay()
        {
            if (!closed && IsVisible) Hide();
        }

        private UIElement Light(int i)
        {
            var g = new Grid { Width = MacButtons.Diameter, Height = MacButtons.Diameter, Margin = new Thickness(i == 0 ? 0 : MacButtons.Gap, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            dots[i] = new Ellipse { StrokeThickness = 0.6 };
            g.Children.Add(dots[i]);
            symbols[i] = new TextBlock
            {
                Text = i == 0 ? "×" : i == 1 ? "−" : "+", FontSize = i == 2 ? 10.5 : 11, FontWeight = FontWeights.Bold, Opacity = 0,
                Margin = new Thickness(0, -2, 0, 0), Foreground = new SolidColorBrush(Color.FromArgb(150, 60, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            g.Children.Add(symbols[i]);
            int index = i;
            g.MouseLeftButtonDown += (s, e) => e.Handled = true;
            g.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                int sc = index == 0 ? SC_CLOSE : index == 1 ? SC_MINIMIZE : (Native.IsZoomed(target) ? SC_RESTORE : SC_MAXIMIZE);
                Native.PostMessage(target, WM_SYSCOMMAND, new IntPtr(sc), IntPtr.Zero);
            };
            return g;
        }

        /// <summary>Colored on the active window (or while hovered), gray otherwise, like macOS.</summary>
        private void Paint()
        {
            bool color = isActive || hover;
            for (int i = 0; i < 3; i++)
            {
                dots[i].Fill = new SolidColorBrush(color ? MacButtons.Fill[i] : (Theme.Dark ? Color.FromRgb(0x4A, 0x4A, 0x4D) : Color.FromRgb(0xCF, 0xCF, 0xD2)));
                dots[i].Stroke = new SolidColorBrush(color ? MacButtons.Rim[i] : (Theme.Dark ? Color.FromRgb(0x3C, 0x3C, 0x3F) : Color.FromRgb(0xBE, 0xBE, 0xC2)));
                symbols[i].Opacity = hover ? 1 : 0;
            }
        }

        public void SetActive(bool a)
        {
            if (a == isActive) return;
            isActive = a;
            Paint();
            // The title bar changes colour when a window gains / loses focus; re-sample once it has repainted.
            Launcher.Later(80, () => { sampled = false; Position(); });
        }

        public void Position()
        {
            if (closed) return;   // e.g. a delayed re-sample arriving after Remove()
            if (!Native.IsWindow(target) || Native.IsIconic(target) || !Native.IsWindowVisible(target)) { HideOverlay(); return; }
            var r = Native.FrameRect(target);
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 80) { HideOverlay(); return; }
            bool resized = (r.Right - r.Left) != (lastRect.Right - lastRect.Left) || (r.Bottom - r.Top) != (lastRect.Bottom - lastRect.Top);
            if (r.Left != lastRect.Left || r.Top != lastRect.Top || resized) lastMove = DateTime.Now;
            lastRect = r;

            if (Settings.WindowButtons == "left")
            {
                patch.Background = Brushes.Transparent;
                row.HorizontalAlignment = HorizontalAlignment.Left;
                row.Margin = new Thickness(2);
                Width = 70; Height = 18;
                Left = (r.Left + 10 * scale) / scale;
                Top = (r.Top + (Native.IsZoomed(target) ? 8 : 9) * scale) / scale;
                if (!IsVisible) Show();
                return;
            }

            // Replace: cover exactly where Windows draws minimize / maximize / close for this window.
            Native.RECT wr, cb;
            Native.GetWindowRect(target, out wr);
            double bx, by, bw, bh;
            if (Native.DwmGetWindowAttribute(target, DWMWA_CAPTION_BUTTON_BOUNDS, out cb, 16) == 0 && cb.Right - cb.Left > 20 && cb.Bottom - cb.Top > 10)
            {
                bx = wr.Left + cb.Left; by = wr.Top + cb.Top; bw = cb.Right - cb.Left; bh = cb.Bottom - cb.Top;
                if (bx + bw > r.Right) bw = r.Right - bx;
                if (by < r.Top) { bh -= r.Top - by; by = r.Top; }
            }
            else
            {
                bw = 138 * scale; bh = 32 * scale;
                bx = r.Right - bw; by = r.Top;
            }

            // Re-check the title bar colour now and then (apps finish painting, switch themes, gain or lose focus),
            // but never mid-drag, when the pixels next to the buttons may not be the window yet.
            bool still = (DateTime.Now - lastMove).TotalMilliseconds > 200;
            double every = isActive ? 0.5 : 1.5;
            if (!sampled || (still && (DateTime.Now - lastSample).TotalSeconds > every))
            {
                var cur = patch.Background as SolidColorBrush;
                var col = SampleTitleBar((int)(bx - 6 * scale), (int)(by + bh / 2));
                // Unreadable (e.g. the screen is locked): keep what we had rather than flashing gray.
                if (col == null) col = cur != null ? cur.Color : DefaultPatch;
                if (cur == null || cur.Color != col.Value) patch.Background = new SolidColorBrush(col.Value);
                sampled = true;
                lastSample = DateTime.Now;
            }
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, 0, MacButtons.RightMargin, 0);
            Width = Math.Max(76, bw / scale);
            Height = Math.Max(20, bh / scale);
            Left = bx / scale;
            Top = by / scale;
            if (!IsVisible) Show();
        }

        private static readonly Color DefaultPatch = Color.FromRgb(32, 32, 32);

        /// <summary>
        /// The title bar colour just left of the buttons, or null if the screen can't be read.
        /// The small area around all nine sample points is copied in one go: GetPixel on the screen
        /// costs a full screen read per pixel, and this runs for every window several times a second.
        /// </summary>
        private Color? SampleTitleBar(int x, int y)
        {
            int reach = (int)(MacButtons.SampleReach * scale), spread = (int)(MacButtons.SampleSpread * scale);
            var area = new System.Drawing.Rectangle(x - reach, y - spread, reach + 1, 2 * spread + 1);
            area.Intersect(System.Windows.Forms.SystemInformation.VirtualScreen);   // samples off-screen are skipped
            if (area.Width <= 0 || area.Height <= 0) return DefaultPatch;
            try
            {
                using (var bmp = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                    int rgb;
                    bool ok = MacButtons.SampleTitleBar(x, y, scale, (int px, int py, out int c) =>
                    {
                        c = 0;
                        if (!area.Contains(px, py)) return false;
                        c = bmp.GetPixel(px - area.Left, py - area.Top).ToArgb();
                        return true;
                    }, out rgb);
                    if (!ok) return DefaultPatch;
                    return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
                }
            }
            catch (Exception) { return null; }   // locked / secure desktop: not worth logging several times a second
        }
    }
}
