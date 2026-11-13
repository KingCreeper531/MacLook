using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>
    /// Keeps a glass surface (the Dock, the menu bar) refracting what is really behind it right now,
    /// instead of a photo of the desktop taken when it appeared.
    ///
    /// The trick: for the few milliseconds of each screen grab, our window is excluded from capture
    /// (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE), so the grab shows the windows underneath it.
    /// It never leaves the screen, so nothing flickers. The grab and the glass rendering run on a worker
    /// thread; a new picture is only rendered when what's behind actually changed.
    /// </summary>
    internal class LiveGlass
    {
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        private const int FastMs = 50;        // ~20 updates a second while things move behind the glass
        private const int SlowMs = 300;       // when nothing has changed for a while
        private const int QuietTicks = 12;    // unchanged grabs before slowing down
        private const int Margin = 48;        // extra pixels around the surface, for the blur and the bent edge

        private readonly Window win;
        private readonly Border target;
        private readonly Func<GlassStyle> style;
        private readonly Func<bool> active;
        private readonly DispatcherTimer timer;
        private IntPtr hwnd;
        private bool busy, broken;
        private long lastSig;
        private int quiet;

        public LiveGlass(Window win, Border target, Func<GlassStyle> style, Func<bool> active)
        {
            this.win = win; this.target = target; this.style = style; this.active = active;
            timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(FastMs) };
            timer.Tick += (s, e) => Tick();
        }

        /// <summary>False if this version of Windows can't exclude a window from capture (before Windows 10 2004).</summary>
        public bool Working { get { return !broken; } }

        public void Start() { timer.Start(); }
        public void Stop() { timer.Stop(); }

        /// <summary>Something changed on our side (theme, glass settings): render again on the next tick.</summary>
        public void Invalidate() { lastSig = 0; quiet = 0; timer.Interval = TimeSpan.FromMilliseconds(FastMs); }

        private void Tick()
        {
            try
            {
                if (busy || broken || !Settings.LiveGlass || !win.IsVisible || !active()) return;
                if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(win).Handle;
                if (hwnd == IntPtr.Zero) return;
                var src = PresentationSource.FromVisual(target);
                if (src == null || src.CompositionTarget == null || target.ActualWidth < 2 || target.ActualHeight < 2) return;
                var m = src.CompositionTarget.TransformToDevice;
                Point tl;
                try { tl = target.PointToScreen(new Point(0, 0)); } catch { return; }
                int x = (int)Math.Round(tl.X), y = (int)Math.Round(tl.Y);
                int w = (int)Math.Round(target.ActualWidth * m.M11), h = (int)Math.Round(target.ActualHeight * m.M22);
                double radius = target.CornerRadius.TopLeft * m.M11;
                var s = style();

                // Grab a little more than the surface (the blur and the curved edge look past it), kept on screen.
                var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                int cx = Math.Max(vs.Left, x - Margin), cy = Math.Max(vs.Top, y - Margin);
                int cw = Math.Min(vs.Right, x + w + Margin) - cx, ch = Math.Min(vs.Bottom, y + h + Margin) - cy;
                if (cw < 2 || ch < 2) return;

                // What we draw depends on the place, size and style too, not only on the pixels behind.
                long shape = ((((long)x * 31 + y) * 31 + w) * 31 + h) * 31 + (long)(radius * 10)
                             + (long)(s.Blur * 7919 + s.TintAmount * 100003 + s.Refraction * 1009 + s.Highlight * 101 + s.Tint);
                busy = true;
                IntPtr me = hwnd;
                var ui = win.Dispatcher;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    BitmapSource img = null;
                    bool changed = false, failed = false;
                    try
                    {
                        Pix pix;
                        try
                        {
                            if (!SetWindowDisplayAffinity(me, WDA_EXCLUDEFROMCAPTURE)) failed = true;
                            DwmFlush();   // wait until the screen is composed without us
                            pix = failed ? null : ScreenCapture.CaptureRegion(cx, cy, cw, ch);
                        }
                        finally { SetWindowDisplayAffinity(me, WDA_NONE); }

                        if (pix != null)
                        {
                            long sig = Signature(pix) ^ shape;
                            if (sig != lastSig)
                            {
                                lastSig = sig;
                                changed = true;
                                img = GlassFx.Render(pix, x, y, w, h, radius, s);
                            }
                        }
                    }
                    catch (Exception ex) { Program.Log(ex); }

                    ui.BeginInvoke(new Action(() =>
                    {
                        busy = false;
                        if (failed)
                        {
                            // Older Windows: fall back to the desktop photo the surfaces already use.
                            broken = true;
                            timer.Stop();
                            Program.Log("Live glass isn't supported on this version of Windows");
                            return;
                        }
                        quiet = changed ? 0 : quiet + 1;
                        timer.Interval = TimeSpan.FromMilliseconds(quiet > QuietTicks ? SlowMs : FastMs);
                        if (img == null) return;
                        var brush = new ImageBrush(img) { Stretch = Stretch.Fill };
                        brush.Freeze();
                        target.Background = brush;
                    }));
                });
            }
            catch (Exception ex) { busy = false; Program.Log(ex); }
        }

        /// <summary>A cheap fingerprint of the grabbed pixels (a few thousand samples).</summary>
        private static long Signature(Pix p)
        {
            long sig = 1469598103934665603L;
            int step = Math.Max(1, p.D.Length / 4096);
            for (int i = 0; i < p.D.Length; i += step)
            {
                // Ignore the lowest bits of each channel, so video noise / dithering doesn't count as a change.
                sig = (sig ^ (p.D[i] & 0xF8F8F8)) * 1099511628211L;
            }
            return sig;
        }
    }
}
