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
        private const uint PW_RENDERFULLCONTENT = 2;
        private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5, GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000, WS_EX_TRANSPARENT = 0x20;
        private const int LaunchWatchMs = 8000, CloseSnapshotMaxAgeSec = 60;

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
            InstallOpenAnimation();
            ApplySystemAnimation();
        }

        public static void Uninstall()
        {
            foreach (var h in hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            hooks.Clear();
            UninstallOpenAnimation();
            StopLaunchWatch();
            RestoreSystemAnimation();
        }

        /// <summary>
        /// Windows' own minimize animation would fight ours, so it's switched off for this session only
        /// (not saved to your profile) and switched back on when Mac Look quits.
        /// </summary>
        public static void ApplySystemAnimation()
        {
            var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.ANIMATIONINFO)) };
            // The original value is remembered on disk, so a crash or forced close can't make "off" look original.
            if (originalMinAnimate < 0 && Settings.OriginalMinAnimate >= 0) originalMinAnimate = Settings.OriginalMinAnimate;
            if (originalMinAnimate < 0 && Native.SystemParametersInfoAnim(Native.SPI_GETANIMATION, ai.cbSize, ref ai, 0))
            {
                originalMinAnimate = ai.iMinAnimate;
                Settings.OriginalMinAnimate = originalMinAnimate;
                try { Settings.Save(); } catch (Exception ex) { Program.Log(ex); }
            }
            if (originalMinAnimate < 0) return;
            ai.iMinAnimate = Settings.AnimateWindows ? 0 : originalMinAnimate;
            Native.SystemParametersInfoAnim(Native.SPI_SETANIMATION, ai.cbSize, ref ai, 0);
        }

        private static void RestoreSystemAnimation()
        {
            if (originalMinAnimate < 0) originalMinAnimate = Settings.OriginalMinAnimate;
            if (originalMinAnimate < 0) return;
            var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.ANIMATIONINFO)), iMinAnimate = originalMinAnimate };
            Native.SystemParametersInfoAnim(Native.SPI_SETANIMATION, ai.cbSize, ref ai, 0);
            originalMinAnimate = -1;
            Settings.OriginalMinAnimate = -1;
            try { Settings.Save(); } catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>For MacLook.exe --restore after a crash: put Windows' minimize animation back.</summary>
        public static void RestoreAfterCrash() { RestoreSystemAnimation(); }

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
            // Called from inside the message loop: nothing may escape from here.
            try
            {
                if (!Settings.AnimateWindows || idObject != 0 || hwnd == IntPtr.Zero) return;
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
                    if (hwnd != snapHwnd || snap == null || (DateTime.Now - snapTime).TotalSeconds > CloseSnapshotMaxAgeSec) return;
                    snapHwnd = IntPtr.Zero;
                    PlayClose(snap, snapRect);
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Keep a fresh picture of the active window, so closing it can be animated after it's gone.</summary>
        public static void Snapshot(IntPtr h)
        {
            try
            {
                if (!Settings.AnimateWindows || h != Native.GetForegroundWindow() || !IsAppWindow(h) || Native.IsIconic(h)) return;
                Rect r;
                var img = Capture(h, out r);
                if (img == null) return;
                snapHwnd = h; snap = img; snapRect = r; snapTime = DateTime.Now;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static void RefreshForeground() { Snapshot(Native.GetForegroundWindow()); }

        private static BitmapSource Capture(IntPtr h, out Rect frame)
        {
            frame = Rect.Empty;
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return null;
            Native.RECT wr, fr = Native.FrameRect(h);
            if (!Native.GetWindowRect(h, out wr)) return null;
            int w = wr.Right - wr.Left, hh = wr.Bottom - wr.Top;
            if (w < 40 || hh < 40 || w > 8000 || hh > 8000) return null;
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            if (fr.Left <= b.Left && fr.Top <= b.Top && fr.Right >= b.Right && fr.Bottom >= b.Bottom) return null; // fullscreen: leave games alone

            using (var bmp = new System.Drawing.Bitmap(w, hh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok;
                    try { ok = Native.PrintWindow(h, dc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(dc); }
                    if (!ok) return null;
                    // The picture comes straight from the app, so it still has Windows' own buttons:
                    // paint the Mac ones over them, exactly like the live overlay does.
                    try { PaintMacButtons(g, bmp, h); } catch (Exception ex) { Program.Log(ex); }
                }
                // Keep just the visible frame (no invisible resize borders), clipped to what was captured.
                int cx = Math.Max(0, fr.Left - wr.Left), cy = Math.Max(0, fr.Top - wr.Top);
                int cw = Math.Min(w, fr.Right - wr.Left) - cx, ch = Math.Min(hh, fr.Bottom - wr.Top) - cy;
                if (cw < 20 || ch < 20) return null;
                var data = bmp.LockBits(new System.Drawing.Rectangle(cx, cy, cw, ch), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                BitmapSource src;
                try { src = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * ch, data.Stride); }
                finally { bmp.UnlockBits(data); }
                src.Freeze();
                // Screen position of the pixels actually kept (not the frame's, which may start outside the capture).
                frame = new Rect(wr.Left + cx, wr.Top + cy, cw, ch);
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
            if (Native.DwmGetWindowAttribute(h, DWMWA_CAPTION_BUTTON_BOUNDS, out cb, 16) != 0 || cb.Right - cb.Left < 20 || cb.Bottom - cb.Top < 10) return;
            float s;
            using (var sg = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) s = sg.DpiX / 96f;
            int x = Math.Max(0, cb.Left), y = Math.Max(0, cb.Top);
            int w = Math.Min(bmp.Width - x, cb.Right - cb.Left), hh = Math.Min(bmp.Height - y, cb.Bottom - cb.Top);
            if (w <= 0 || hh <= 0) return;

            // Title bar colour, sampled the same way the live overlay does (points clamped into the picture).
            int maxX = bmp.Width - 1, maxY = bmp.Height - 1;
            int rgb;
            MacButtons.SampleTitleBar(x, y + hh / 2, s, (int px, int py, out int c) =>
            {
                c = bmp.GetPixel(Math.Max(0, Math.Min(maxX, px)), Math.Max(0, Math.Min(maxY, py))).ToArgb();
                return true;
            }, out rgb);
            using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(rgb | unchecked((int)0xFF000000))))
                g.FillRectangle(br, x, y, w, hh);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float d = (float)(MacButtons.Diameter * s), gap = (float)(MacButtons.Gap * s);
            float right = x + w - (float)(MacButtons.RightMargin * s), top = y + hh / 2f - d / 2;
            for (int i = 0; i < 3; i++)
            {
                float left = right - (3 - i) * d - (2 - i) * gap;
                var fill = MacButtons.Fill[i];
                using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(fill.R, fill.G, fill.B))) g.FillEllipse(br, left, top, d, d);
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
                showHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, showProc, 0, 0, WINEVENT_SKIPOWNPROCESS);
            }
            var at = launchAt;
            Launcher.Later(LaunchWatchMs, () => { if (launchAt == at) StopLaunchWatch(); });
        }

        private static void StopLaunchWatch()
        {
            if (showHook != IntPtr.Zero) Native.UnhookWinEvent(showHook);
            showHook = IntPtr.Zero;
            launchBg = null;
            launchKnown = null;
        }

        private static void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // Called from inside the message loop: nothing may escape from here.
            try
            {
                if (idObject != 0 || hwnd == IntPtr.Zero || launchBg == null || launchKnown == null || launchKnown.Contains(hwnd)) return;
                // EVENT_OBJECT_SHOW also fires for child controls of windows that are already open.
                if ((Native.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CHILD) != 0) return;
                if (!IsAppWindow(hwnd) || Native.IsIconic(hwnd)) return;
                var r = Native.FrameRect(hwnd);
                if (r.Right - r.Left < 160 || r.Bottom - r.Top < 120) return;
                var bg = launchBg;
                var from = launchFrom;
                StopLaunchWatch();

                // Cover the new window right away with what was behind it, then grow its picture from the icon.
                var to = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                var o = new OpenOverlay(bg, from, to);
                o.Show();
                IntPtr h = hwnd;
                Launcher.Later(110, () =>
                {
                    // Play(null) still fades the cover away: it must never be left over the new window.
                    BitmapSource img = null;
                    try { Rect actual; img = Capture(h, out actual); }
                    catch (Exception ex) { Program.Log(ex); }
                    o.Play(img);
                });
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>
        /// Base of the animation overlays: a click-through, never-focused, topmost window covering both
        /// rectangles (physical pixels). Its frame loop is always unhooked, and Smooth released, however
        /// the animation ends: finished, failed, or the window closed underneath it.
        /// </summary>
        private abstract class FxOverlay : Window
        {
            protected readonly Rect from, to, area;
            protected double scale = 1;
            private DateTime start;
            private bool running;

            protected FxOverlay(Rect from, Rect to)
            {
                this.from = from; this.to = to;
                var u = Rect.Union(from, to);
                u.Inflate(4, 4);
                area = u;
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
                SourceInitialized += (s, e) =>
                {
                    var h = new WindowInteropHelper(this).Handle;
                    scale = HwndSource.FromHwnd(h).CompositionTarget.TransformToDevice.M11;
                    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                    // Click-through, never focused, not in Alt+Tab.
                    Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                    Left = area.X / scale; Top = area.Y / scale; Width = area.Width / scale; Height = area.Height / scale;
                    OnPlaced();
                };
            }

            /// <summary>The window has its handle, DPI scale and position.</summary>
            protected virtual void OnPlaced() { }

            /// <summary>Draws one frame, ms after the start; returns false once the animation is over.</summary>
            protected abstract bool Frame(double ms);

            /// <summary>The animation ran to the end.</summary>
            protected virtual void Finished() { Close(); }

            /// <summary>Show, draw the first frame, then animate.</summary>
            public void Run()
            {
                Show();
                if (!Frame(0)) { Finished(); return; }
                StartFrames();
            }

            protected void StartFrames()
            {
                start = DateTime.Now;
                if (running) return;
                running = true;
                CompositionTarget.Rendering += OnRendering; MacLook.Smooth.Begin();
            }

            private void StopFrames()
            {
                if (!running) return;
                running = false;
                CompositionTarget.Rendering -= OnRendering; MacLook.Smooth.End();
            }

            private void OnRendering(object sender, EventArgs e)
            {
                bool more;
                try { more = Frame((DateTime.Now - start).TotalMilliseconds); }
                catch (Exception ex)
                {
                    Program.Log(ex);
                    StopFrames();
                    try { Close(); } catch { }
                    return;
                }
                if (more) return;
                StopFrames();
                Finished();
            }

            protected override void OnClosed(EventArgs e)
            {
                StopFrames();
                base.OnClosed(e);
            }
        }

        private class OpenOverlay : FxOverlay
        {
            private const double Duration = 300;
            private readonly Image cover = new Image { Stretch = Stretch.Fill };
            private readonly Image win = new Image { Stretch = Stretch.Fill, Opacity = 0 };
            private readonly Rect coverRect;
            private bool finishing;

            public OpenOverlay(Pix bg, Rect from, Rect to) : base(from, to)
            {
                cover.Source = Crop(bg, to, out coverRect);
                var canvas = new Canvas();
                canvas.Children.Add(cover);
                canvas.Children.Add(win);
                Content = canvas;
            }

            protected override void OnPlaced()
            {
                if (coverRect.IsEmpty) return;
                Canvas.SetLeft(cover, (coverRect.X - area.X) / scale); Canvas.SetTop(cover, (coverRect.Y - area.Y) / scale);
                cover.Width = coverRect.Width / scale; cover.Height = coverRect.Height / scale;
            }

            /// <summary>The part of the screen picture under r (r may reach past the captured screen).</summary>
            private static BitmapSource Crop(Pix p, Rect r, out Rect got)
            {
                got = Rect.Empty;
                int x0 = Math.Max(p.X, (int)r.X), y0 = Math.Max(p.Y, (int)r.Y);
                int x1 = Math.Min(p.X + p.W, (int)r.Right), y1 = Math.Min(p.Y + p.H, (int)r.Bottom);
                int w = x1 - x0, h = y1 - y0;
                if (w <= 0 || h <= 0) return null;
                var px = new int[w * h];
                for (int j = 0; j < h; j++) Array.Copy(p.D, (y0 - p.Y + j) * p.W + (x0 - p.X), px, j * w, w);
                var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                bs.Freeze();
                got = new Rect(x0, y0, w, h);
                return bs;
            }

            public void Play(BitmapSource img)
            {
                if (img == null) { Finished(); return; }
                win.Source = img;
                StartFrames();
            }

            protected override bool Frame(double ms)
            {
                double p = Math.Min(1, ms / Duration);
                const double s = 1.25;   // gentle overshoot, like a spring
                double t = p - 1, k = t * t * ((s + 1) * t + s) + 1;
                double x = from.X + (to.X - from.X) * k, y = from.Y + (to.Y - from.Y) * k;
                double w = from.Width + (to.Width - from.Width) * k, h = from.Height + (to.Height - from.Height) * k;
                Canvas.SetLeft(win, (x - area.X) / scale); Canvas.SetTop(win, (y - area.Y) / scale);
                win.Width = Math.Max(1, w / scale); win.Height = Math.Max(1, h / scale);
                win.Opacity = Math.Min(1, p * 3);
                return p < 1;
            }

            /// <summary>Fade the whole overlay out over the real window, then close.</summary>
            protected override void Finished()
            {
                if (finishing) return;
                finishing = true;
                var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
                a.Completed += (s, e) => Close();
                BeginAnimation(OpacityProperty, a);
            }
        }

        // ------------------------------------------------------------------ every new window opens with a zoom
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        private const long WS_EX_LAYERED = 0x80000;
        private const uint LWA_ALPHA = 2;
        private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        private const int OpenCaptureDelayMs = 70, OpenMaxHiddenMs = 900;

        private static IntPtr anyShowHook;
        private static Native.WinEventDelegate anyShowProc;
        private static readonly Dictionary<IntPtr, long> opening = new Dictionary<IntPtr, long>();   // hwnd -> its original ex-style
        private static readonly HashSet<IntPtr> seen = new HashSet<IntPtr>();

        private static void InstallOpenAnimation()
        {
            if (anyShowHook != IntPtr.Zero) return;
            anyShowProc = OnAnyShow;
            anyShowHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, anyShowProc, 0, 0, WINEVENT_SKIPOWNPROCESS);
        }

        private static void UninstallOpenAnimation()
        {
            if (anyShowHook != IntPtr.Zero) Native.UnhookWinEvent(anyShowHook);
            anyShowHook = IntPtr.Zero;
            foreach (var h in opening.Keys.ToList()) Reveal(h);   // never leave a window invisible
        }

        /// <summary>
        /// A window appeared for the first time: keep it invisible for a moment, take its picture (with the
        /// Mac buttons painted in), zoom and fade that picture in like macOS, then reveal the real window.
        /// </summary>
        private static void OnAnyShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (!Settings.AnimateWindows || idObject != 0 || hwnd == IntPtr.Zero) return;
                if (launchBg != null) return;   // an app launched from the Dock / Launchpad: that animation handles it
                if ((Native.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CHILD) != 0) return;
                if (opening.ContainsKey(hwnd) || !IsAppWindow(hwnd) || Native.IsIconic(hwnd)) return;
                if (seen.Count > 2000) seen.RemoveWhere(x => !Native.IsWindow(x));
                if (!seen.Add(hwnd)) return;   // only the first time a window opens
                if (Native.GetClass(hwnd) == "ApplicationFrameWindow") return;   // Store apps draw through a layer that can't be hidden this way

                var r = Native.FrameRect(hwnd);
                if (r.Right - r.Left < 160 || r.Bottom - r.Top < 120) return;
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                if (r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom) return;   // fullscreen / games

                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_LAYERED) != 0) return;   // the app manages its own transparency: leave it alone

                int on = 1;
                Native.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, 4);   // replaces Windows' own open animation
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
                SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
                opening[hwnd] = ex;

                IntPtr h = hwnd;
                Launcher.Later(OpenMaxHiddenMs, () => Reveal(h));   // safety net: never stays hidden
                Launcher.Later(OpenCaptureDelayMs, () =>
                {
                    try
                    {
                        if (!opening.ContainsKey(h)) return;
                        Rect actual;
                        var img = Capture(h, out actual);
                        if (img == null) { Reveal(h); return; }
                        var from = new Rect(actual.X + actual.Width * 0.05, actual.Y + actual.Height * 0.05, actual.Width * 0.9, actual.Height * 0.9);
                        var o = new PopOverlay(img, from, actual, () => Reveal(h));
                        o.Run();
                    }
                    catch (Exception e2) { Program.Log(e2); Reveal(h); }
                });
            }
            catch (Exception e) { Program.Log(e); }
        }

        /// <summary>Make a window visible again exactly as it was (idempotent).</summary>
        private static void Reveal(IntPtr h)
        {
            long ex;
            if (!opening.TryGetValue(h, out ex)) return;
            opening.Remove(h);
            try
            {
                if (!Native.IsWindow(h)) return;
                SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
                long cur = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_LAYERED) == 0) Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(cur & ~WS_EX_LAYERED));
                if (Settings.WindowButtons == "off")
                {
                    int off = 0;
                    Native.DwmSetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, ref off, 4);
                }
            }
            catch (Exception e) { Program.Log(e); }
        }

        /// <summary>The new window's picture grows from 90% to full size while fading in, with a soft spring.</summary>
        private class PopOverlay : FxOverlay
        {
            private const double Duration = 230;
            private readonly Image image;
            private readonly Action done;
            private bool finishing;

            public PopOverlay(BitmapSource img, Rect from, Rect to, Action done) : base(from, to)
            {
                this.done = done;
                image = new Image { Source = img, Stretch = Stretch.Fill, Opacity = 0 };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var canvas = new Canvas();
                canvas.Children.Add(image);
                Content = canvas;
            }

            protected override bool Frame(double ms)
            {
                double p = Math.Min(1, ms / Duration);
                const double s = 1.2;
                double t = p - 1, k = t * t * ((s + 1) * t + s) + 1;   // back-ease out
                double x = from.X + (to.X - from.X) * k, y = from.Y + (to.Y - from.Y) * k;
                double w = from.Width + (to.Width - from.Width) * k, h = from.Height + (to.Height - from.Height) * k;
                Canvas.SetLeft(image, (x - area.X) / scale);
                Canvas.SetTop(image, (y - area.Y) / scale);
                image.Width = Math.Max(1, w / scale);
                image.Height = Math.Max(1, h / scale);
                image.Opacity = Math.Min(1, p * 2.2);
                return p < 1;
            }

            protected override void OnClosed(EventArgs e)
            {
                // However it ends, the real window comes back.
                if (!finishing && done != null) done();
                base.OnClosed(e);
            }

            /// <summary>Show the real window under the finished picture, then remove the picture a frame later.</summary>
            protected override void Finished()
            {
                if (finishing) return;
                finishing = true;
                if (done != null) done();
                Launcher.Later(32, () => { try { Close(); } catch { } });
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

        private class ZoomOverlay : FxOverlay
        {
            private const double Duration = 260;
            private readonly Image image;

            public ZoomOverlay(BitmapSource img, Rect from, Rect to) : base(from, to)
            {
                image = new Image { Source = img, Stretch = Stretch.Fill };
                var canvas = new Canvas();
                canvas.Children.Add(image);
                Content = canvas;
            }

            protected override bool Frame(double ms)
            {
                double p = ms / Duration;
                if (p >= 1) return false;
                double k = 1 - Math.Pow(1 - p, 3);
                double x = from.X + (to.X - from.X) * k, y = from.Y + (to.Y - from.Y) * k;
                double w = from.Width + (to.Width - from.Width) * k, h = from.Height + (to.Height - from.Height) * k;
                Canvas.SetLeft(image, (x - area.X) / scale);
                Canvas.SetTop(image, (y - area.Y) / scale);
                image.Width = Math.Max(1, w / scale);
                image.Height = Math.Max(1, h / scale);
                image.Opacity = p > 0.8 ? (1 - p) / 0.2 : 1;
                return true;
            }
        }

        private class GenieOverlay : FxOverlay
        {
            private const int Strips = 48;
            private const double GenieDuration = 520, CloseDuration = 210;
            private readonly bool close;
            private readonly Canvas canvas = new Canvas();
            private readonly Image[] strips;
            private readonly Image whole;

            public GenieOverlay(BitmapSource img, Rect from, Rect to, bool close) : base(from, to)
            {
                this.close = close;
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
            }

            private static double SmoothStep(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
            private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

            protected override bool Frame(double ms)
            {
                double p = ms / (close ? CloseDuration : GenieDuration);
                if (p >= 1) return false;
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
                    return true;
                }

                // Genie: the bottom of the window gets sucked into the dock icon first, the top follows,
                // and every row narrows toward the icon's width as it travels.
                for (int i = 0; i < Strips; i++)
                {
                    double y0 = i / (double)Strips, y1 = (i + 1) / (double)Strips;
                    double q0 = SmoothStep(p * 1.6 - (1 - y0) * 0.6), q1 = SmoothStep(p * 1.6 - (1 - y1) * 0.6);
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
                return true;
            }
        }
    }
}
