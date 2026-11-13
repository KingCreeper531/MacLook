using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MacLook
{
    /// <summary>
    /// Glass title bars and rounded corners on other apps' windows, using only public DWM attributes
    /// (no code injection). Everything is put back when Mac Look quits.
    /// </summary>
    internal static class WindowGlass
    {
        private static readonly HashSet<IntPtr> applied = new HashSet<IntPtr>();
        private static string appliedMode;

        public static void Apply(List<AppWindow> wins)
        {
            string mode = Settings.WindowFrames;
            if (mode != appliedMode) { RestoreAll(); appliedMode = mode; }
            applied.RemoveWhere(h => !Native.IsWindow(h));
            if (mode == "off") return;
            foreach (var w in wins)
                if (applied.Add(w.Hwnd)) Set(w.Hwnd, mode);
        }

        public static void Reapply() { RestoreAll(); appliedMode = null; }

        private static void Set(IntPtr h, string mode)
        {
            int corner = 2;                                   // round
            Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            int backdrop = mode == "mica" ? 2 : 3;            // 2 = Mica, 3 = Acrylic
            Native.DwmSetWindowAttribute(h, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
            int border = Theme.Dark ? 0x00505050 : 0x00DADADA; // COLORREF 0x00BBGGRR: a soft hairline
            Native.DwmSetWindowAttribute(h, Native.DWMWA_BORDER_COLOR, ref border, 4);
        }

        public static void RestoreAll()
        {
            foreach (var h in applied)
            {
                if (!Native.IsWindow(h)) continue;
                int zero = 0, def = unchecked((int)0xFFFFFFFF);
                Native.DwmSetWindowAttribute(h, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref zero, 4);
                Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref zero, 4);
                Native.DwmSetWindowAttribute(h, Native.DWMWA_BORDER_COLOR, ref def, 4);
            }
            applied.Clear();
        }
    }

    /// <summary>Genie effect when a window minimizes into the Dock, and a scale-and-fade when one closes.</summary>
    internal static class WindowAnimator
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_SYSTEM_MOVESIZEEND = 0x0B, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_OBJECT_HIDE = 0x8003;
        private const uint WINEVENT_SKIPOWNPROCESS = 2;

        private static Native.WinEventDelegate proc;
        private static readonly List<IntPtr> hooks = new List<IntPtr>();
        private static IntPtr snapHwnd;
        private static BitmapSource snap;
        private static Rect snapRect;
        private static DateTime snapTime;
        private static int originalMinAnimate = -1;

        public static void Install()
        {
            if (hooks.Count > 0) return;
            proc = OnEvent;
            foreach (var ev in new[] { EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MINIMIZESTART, EVENT_OBJECT_HIDE })
                hooks.Add(Native.SetWinEventHook(ev, ev, IntPtr.Zero, proc, 0, 0, WINEVENT_SKIPOWNPROCESS));
            ApplySystemAnimation();
        }

        public static void Uninstall()
        {
            foreach (var h in hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            hooks.Clear();
            RestoreSystemAnimation();
        }

        /// <summary>
        /// Windows' own minimize animation would fight ours, so it's switched off for this session only
        /// (not saved to your profile) and switched back on when Mac Look quits.
        /// </summary>
        public static void ApplySystemAnimation()
        {
            var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.ANIMATIONINFO)) };
            if (originalMinAnimate < 0 && Native.SystemParametersInfoAnim(Native.SPI_GETANIMATION, ai.cbSize, ref ai, 0))
                originalMinAnimate = ai.iMinAnimate;
            if (originalMinAnimate < 0) return;
            ai.iMinAnimate = Settings.AnimateWindows ? 0 : originalMinAnimate;
            Native.SystemParametersInfoAnim(Native.SPI_SETANIMATION, ai.cbSize, ref ai, 0);
        }

        private static void RestoreSystemAnimation()
        {
            if (originalMinAnimate < 0) return;
            var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.ANIMATIONINFO)), iMinAnimate = originalMinAnimate };
            Native.SystemParametersInfoAnim(Native.SPI_SETANIMATION, ai.cbSize, ref ai, 0);
        }

        private static bool IsAppWindow(IntPtr h)
        {
            if (h == IntPtr.Zero || WindowTracker.IsOwn(h) || WindowTracker.IsShellWindow(h)) return false;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            return Native.GetWindowTextLength(h) > 0;
        }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (!Settings.AnimateWindows || idObject != 0 || hwnd == IntPtr.Zero) return;
            try
            {
                if (ev == EVENT_SYSTEM_FOREGROUND || ev == EVENT_SYSTEM_MOVESIZEEND)
                {
                    IntPtr h = hwnd;
                    Launcher.Later(400, () => Snapshot(h));
                }
                else if (ev == EVENT_SYSTEM_MINIMIZESTART)
                {
                    if (!IsAppWindow(hwnd)) return;
                    BitmapSource img = null;
                    Rect from = Rect.Empty;
                    if (!Native.IsIconic(hwnd)) img = Capture(hwnd, out from);
                    if (img == null && hwnd == snapHwnd) { img = snap; from = snapRect; }
                    if (img == null) return;
                    PlayGenie(img, from, Program.Dock.IconRectFor(hwnd));
                }
                else if (ev == EVENT_OBJECT_HIDE)
                {
                    if (hwnd != snapHwnd || snap == null || (DateTime.Now - snapTime).TotalSeconds > 60) return;
                    snapHwnd = IntPtr.Zero;
                    PlayClose(snap, snapRect);
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Keep a fresh picture of the active window, so closing it can be animated after it's gone.</summary>
        public static void Snapshot(IntPtr h)
        {
            if (!Settings.AnimateWindows || h != Native.GetForegroundWindow() || !IsAppWindow(h) || Native.IsIconic(h)) return;
            Rect r;
            var img = Capture(h, out r);
            if (img == null) return;
            snapHwnd = h; snap = img; snapRect = r; snapTime = DateTime.Now;
        }

        public static void RefreshForeground() { Snapshot(Native.GetForegroundWindow()); }

        private static BitmapSource Capture(IntPtr h, out Rect frame)
        {
            frame = Rect.Empty;
            Native.RECT wr, fr = Native.FrameRect(h);
            Native.GetWindowRect(h, out wr);
            int w = wr.Right - wr.Left, hh = wr.Bottom - wr.Top;
            if (w < 40 || hh < 40 || w > 8000 || hh > 8000) return null;
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            if (fr.Left <= b.Left && fr.Top <= b.Top && fr.Right >= b.Right && fr.Bottom >= b.Bottom) return null; // fullscreen: leave games alone

            using (var bmp = new System.Drawing.Bitmap(w, hh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok = Native.PrintWindow(h, dc, 2);   // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(dc);
                    if (!ok) return null;
                    // The picture comes straight from the app, so it still has Windows' own buttons:
                    // paint the Mac ones over them, exactly like the live overlay does.
                    try { PaintMacButtons(g, bmp, h); } catch (Exception ex) { Program.Log(ex); }
                }
                int cx = Math.Max(0, fr.Left - wr.Left), cy = Math.Max(0, fr.Top - wr.Top);
                int cw = Math.Min(w - cx, fr.Right - fr.Left), ch = Math.Min(hh - cy, fr.Bottom - fr.Top);
                if (cw < 20 || ch < 20) return null;
                var data = bmp.LockBits(new System.Drawing.Rectangle(cx, cy, cw, ch), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var src = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * ch, data.Stride);
                bmp.UnlockBits(data);
                src.Freeze();
                frame = new Rect(fr.Left, fr.Top, cw, ch);
                return src;
            }
        }

        // ------------------------------------------------------------------ animations
        private static void PlayGenie(BitmapSource img, Rect from, Rect to)
        {
            new GenieOverlay(img, from, to, false).Run();
        }

        private static void PlayClose(BitmapSource img, Rect from)
        {
            new GenieOverlay(img, from, from, true).Run();
        }

        private static void PaintMacButtons(System.Drawing.Graphics g, System.Drawing.Bitmap bmp, IntPtr h)
        {
            if (Settings.WindowButtons != "replace") return;
            Native.RECT cb;
            if (Native.DwmGetWindowAttribute(h, 5, out cb, 16) != 0 || cb.Right - cb.Left < 20 || cb.Bottom - cb.Top < 10) return;
            float s;
            using (var sg = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) s = sg.DpiX / 96f;
            int x = Math.Max(0, cb.Left), y = Math.Max(0, cb.Top);
            int w = Math.Min(bmp.Width - x, cb.Right - cb.Left), hh = Math.Min(bmp.Height - y, cb.Bottom - cb.Top);
            if (w <= 0 || hh <= 0) return;

            // Title bar colour: most common of a few pixels just left of the buttons.
            var counts = new Dictionary<int, int>();
            foreach (int ox in new[] { 4, 16, 34 })
                foreach (int oy in new[] { -8, 0, 8 })
                {
                    int px = Math.Max(0, x - (int)(ox * s)), py = Math.Max(0, Math.Min(bmp.Height - 1, y + hh / 2 + (int)(oy * s)));
                    int c = bmp.GetPixel(px, py).ToArgb() | unchecked((int)0xFF000000);
                    int n; counts.TryGetValue(c, out n); counts[c] = n + 1;
                }
            int best = counts.OrderByDescending(kv => kv.Value).First().Key;
            using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(best)))
                g.FillRectangle(br, x, y, w, hh);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float d = 12 * s, gap = 8 * s, right = x + w - 14 * s, top = y + hh / 2f - d / 2;
            var colors = new[] { System.Drawing.Color.FromArgb(0xFF, 0x5F, 0x57), System.Drawing.Color.FromArgb(0xFE, 0xBC, 0x2E), System.Drawing.Color.FromArgb(0x28, 0xC8, 0x40) };
            for (int i = 0; i < 3; i++)
            {
                float left = right - (3 - i) * d - (2 - i) * gap;
                using (var br = new System.Drawing.SolidBrush(colors[i])) g.FillEllipse(br, left, top, d, d);
            }
        }

        // ------------------------------------------------------------------ opening animation
        private static Pix launchBg;
        private static Rect launchFrom;
        private static DateTime launchAt;
        private static HashSet<IntPtr> launchKnown;
        private static IntPtr showHook;
        private static Native.WinEventDelegate showProc;

        /// <summary>
        /// Called just before launching an app from the Dock / Launchpad / Spotlight: remember what the
        /// screen looks like now, so the app's first window can grow out of where you clicked.
        /// </summary>
        public static void ExpectLaunch(Rect from)
        {
            if (!Settings.AnimateWindows) return;
            launchBg = ScreenCapture.Capture();
            if (launchBg == null) return;
            launchFrom = from;
            launchAt = DateTime.Now;
            launchKnown = new HashSet<IntPtr>(WindowTracker.Enumerate().Select(w => w.Hwnd));
            if (showHook == IntPtr.Zero)
            {
                showProc = OnShow;
                showHook = Native.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, showProc, 0, 0, WINEVENT_SKIPOWNPROCESS);
            }
            var at = launchAt;
            Launcher.Later(8000, () => { if (launchAt == at) StopLaunchWatch(); });
        }

        private static void StopLaunchWatch()
        {
            if (showHook != IntPtr.Zero) Native.UnhookWinEvent(showHook);
            showHook = IntPtr.Zero;
            launchBg = null;
        }

        private static void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || launchBg == null || launchKnown == null || launchKnown.Contains(hwnd)) return;
            if (!IsAppWindow(hwnd) || Native.IsIconic(hwnd)) return;
            var r = Native.FrameRect(hwnd);
            if (r.Right - r.Left < 160 || r.Bottom - r.Top < 120) return;
            var bg = launchBg;
            var from = launchFrom;
            StopLaunchWatch();
            try
            {
                // Cover the new window right away with what was behind it, then grow its picture from the icon.
                var to = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                var o = new OpenOverlay(bg, from, to);
                o.Show();
                IntPtr h = hwnd;
                Launcher.Later(110, () =>
                {
                    Rect actual;
                    var img = Capture(h, out actual);
                    o.Play(img);
                });
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private class OpenOverlay : Window
        {
            private readonly Rect from, to, area;
            private readonly Image cover = new Image { Stretch = Stretch.Fill };
            private readonly Image win = new Image { Stretch = Stretch.Fill, Opacity = 0 };
            private DateTime start;
            private double scale = 1;

            public OpenOverlay(Pix bg, Rect from, Rect to)
            {
                this.from = from; this.to = to;
                var u = Rect.Union(from, to);
                u.Inflate(4, 4);
                area = u;
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
                cover.Source = Crop(bg, to);
                var canvas = new Canvas();
                canvas.Children.Add(cover);
                canvas.Children.Add(win);
                Content = canvas;
                SourceInitialized += (s, e) =>
                {
                    var h = new WindowInteropHelper(this).Handle;
                    scale = HwndSource.FromHwnd(h).CompositionTarget.TransformToDevice.M11;
                    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                    Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | 0x20 | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                    Left = area.X / scale; Top = area.Y / scale; Width = area.Width / scale; Height = area.Height / scale;
                    Canvas.SetLeft(cover, (to.X - area.X) / scale); Canvas.SetTop(cover, (to.Y - area.Y) / scale);
                    cover.Width = to.Width / scale; cover.Height = to.Height / scale;
                };
            }

            private static BitmapSource Crop(Pix p, Rect r)
            {
                int x = Math.Max(0, (int)r.X - p.X), y = Math.Max(0, (int)r.Y - p.Y);
                int w = Math.Min(p.W - x, (int)r.Width), h = Math.Min(p.H - y, (int)r.Height);
                if (w <= 0 || h <= 0) return null;
                var px = new int[w * h];
                for (int j = 0; j < h; j++) Array.Copy(p.D, (y + j) * p.W + x, px, j * w, w);
                var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                bs.Freeze();
                return bs;
            }

            public void Play(BitmapSource img)
            {
                if (img == null) { Finish(); return; }
                win.Source = img;
                start = DateTime.Now;
                CompositionTarget.Rendering += OnFrame;
            }

            private void OnFrame(object sender, EventArgs e)
            {
                double p = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 300.0);
                const double s = 1.25;   // gentle overshoot, like a spring
                double t = p - 1, k = t * t * ((s + 1) * t + s) + 1;
                double x = from.X + (to.X - from.X) * k, y = from.Y + (to.Y - from.Y) * k;
                double w = from.Width + (to.Width - from.Width) * k, h = from.Height + (to.Height - from.Height) * k;
                Canvas.SetLeft(win, (x - area.X) / scale); Canvas.SetTop(win, (y - area.Y) / scale);
                win.Width = Math.Max(1, w / scale); win.Height = Math.Max(1, h / scale);
                win.Opacity = Math.Min(1, p * 3);
                if (p >= 1) { CompositionTarget.Rendering -= OnFrame; Finish(); }
            }

            private void Finish()
            {
                var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
                a.Completed += (s, e) => Close();
                BeginAnimation(OpacityProperty, a);
            }
        }

        /// <summary>Window switcher: the chosen window grows from its card to its real size.</summary>
        public static void Zoom(IntPtr hwnd, Rect from)
        {
            try
            {
                Rect to;
                var img = Capture(hwnd, out to);
                if (img == null) return;
                new ZoomOverlay(img, from, to).Run();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private class ZoomOverlay : Window
        {
            private readonly Rect from, to, area;
            private readonly Image image;
            private DateTime start;
            private double scale = 1;

            public ZoomOverlay(BitmapSource img, Rect from, Rect to)
            {
                this.from = from; this.to = to;
                var u = Rect.Union(from, to);
                u.Inflate(4, 4);
                area = u;
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
                image = new Image { Source = img, Stretch = Stretch.Fill };
                var canvas = new Canvas();
                canvas.Children.Add(image);
                Content = canvas;
                SourceInitialized += (s, e) =>
                {
                    var h = new WindowInteropHelper(this).Handle;
                    scale = HwndSource.FromHwnd(h).CompositionTarget.TransformToDevice.M11;
                    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                    Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | 0x20 | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                    Left = area.X / scale; Top = area.Y / scale; Width = area.Width / scale; Height = area.Height / scale;
                };
            }

            public void Run()
            {
                Show();
                start = DateTime.Now;
                Frame(0);
                CompositionTarget.Rendering += OnFrame;
            }

            private void OnFrame(object sender, EventArgs e)
            {
                double p = (DateTime.Now - start).TotalMilliseconds / 260.0;
                if (p >= 1) { CompositionTarget.Rendering -= OnFrame; Close(); return; }
                Frame(p);
            }

            private void Frame(double p)
            {
                double k = 1 - Math.Pow(1 - p, 3);
                double x = from.X + (to.X - from.X) * k, y = from.Y + (to.Y - from.Y) * k;
                double w = from.Width + (to.Width - from.Width) * k, h = from.Height + (to.Height - from.Height) * k;
                Canvas.SetLeft(image, (x - area.X) / scale);
                Canvas.SetTop(image, (y - area.Y) / scale);
                image.Width = Math.Max(1, w / scale);
                image.Height = Math.Max(1, h / scale);
                image.Opacity = p > 0.8 ? (1 - p) / 0.2 : 1;
            }
        }

        private class GenieOverlay : Window
        {
            private const int Strips = 48;
            private readonly BitmapSource img;
            private readonly Rect from, to, area;
            private readonly bool close;
            private readonly Canvas canvas = new Canvas();
            private readonly Image[] strips;
            private Image whole;
            private DateTime start;
            private double scale = 1;

            public GenieOverlay(BitmapSource img, Rect from, Rect to, bool close)
            {
                this.img = img; this.from = from; this.to = to; this.close = close;
                var u = Rect.Union(from, to);
                u.Inflate(4, 4);
                area = u;
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ShowInTaskbar = false;
                ShowActivated = false;
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
                Content = canvas;
                if (close)
                {
                    whole = new Image { Source = img, Stretch = Stretch.Fill };
                    canvas.Children.Add(whole);
                }
                else
                {
                    strips = new Image[Strips];
                    for (int i = 0; i < Strips; i++)
                    {
                        int y0 = img.PixelHeight * i / Strips, y1 = img.PixelHeight * (i + 1) / Strips;
                        strips[i] = new Image { Source = new CroppedBitmap(img, new Int32Rect(0, y0, img.PixelWidth, Math.Max(1, y1 - y0))), Stretch = Stretch.Fill };
                        RenderOptions.SetBitmapScalingMode(strips[i], BitmapScalingMode.LowQuality);
                        canvas.Children.Add(strips[i]);
                    }
                }
                SourceInitialized += (s, e) =>
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    scale = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;
                    long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                    // Click-through, never focused, not in Alt+Tab.
                    Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | 0x20 | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                    Left = area.X / scale; Top = area.Y / scale; Width = area.Width / scale; Height = area.Height / scale;
                };
            }

            public void Run()
            {
                Show();
                start = DateTime.Now;
                Frame(0);
                CompositionTarget.Rendering += OnFrame;
            }

            private void OnFrame(object sender, EventArgs e)
            {
                double p = (DateTime.Now - start).TotalMilliseconds / (close ? 210.0 : 520.0);
                if (p >= 1)
                {
                    CompositionTarget.Rendering -= OnFrame;
                    Close();
                    return;
                }
                Frame(p);
            }

            private static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
            private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

            private void Frame(double p)
            {
                if (close)
                {
                    // Shrink a little toward the centre and fade, like closing on a Mac.
                    double k = 1 - Math.Pow(1 - p, 3);
                    double s = 1 - 0.1 * k;
                    double w = from.Width * s, h = from.Height * s;
                    Canvas.SetLeft(whole, (from.X + (from.Width - w) / 2 - area.X) / scale);
                    Canvas.SetTop(whole, (from.Y + (from.Height - h) / 2 - area.Y) / scale);
                    whole.Width = w / scale; whole.Height = h / scale;
                    whole.Opacity = 1 - k;
                    return;
                }

                // Genie: the bottom of the window gets sucked into the dock icon first, the top follows,
                // and every row narrows toward the icon's width as it travels.
                for (int i = 0; i < Strips; i++)
                {
                    double y0 = i / (double)Strips, y1 = (i + 1) / (double)Strips;
                    double q0 = Smooth(p * 1.6 - (1 - y0) * 0.6), q1 = Smooth(p * 1.6 - (1 - y1) * 0.6);
                    double top = Lerp(from.Top + y0 * from.Height, to.Top + y0 * to.Height, q0);
                    double bottom = Lerp(from.Top + y1 * from.Height, to.Top + y1 * to.Height, q1);
                    double q = (q0 + q1) / 2;
                    double left = Lerp(from.Left, to.Left, q), right = Lerp(from.Right, to.Right, q);
                    var s = strips[i];
                    Canvas.SetLeft(s, (left - area.X) / scale);
                    Canvas.SetTop(s, (top - area.Y) / scale);
                    s.Width = Math.Max(0.5, (right - left) / scale);
                    s.Height = Math.Max(0.5, (bottom - top) / scale + 0.6);
                    s.Opacity = p > 0.85 ? (1 - p) / 0.15 : 1;
                }
            }
        }
    }
}
