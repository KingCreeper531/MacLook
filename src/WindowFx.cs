using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
