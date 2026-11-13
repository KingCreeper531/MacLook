using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MacLook
{
    /// <summary>
    /// macOS-style window tiling: drag a window until the pointer touches the left / right edge of a screen
    /// (or the top, to fill it) and a frosted glass frame springs open where the window will go; let go and
    /// the window snaps there. Also gives a window flung across the screen a small spring when released.
    /// Windows' own Snap is switched off for this session while tiling is on (never saved to the profile).
    /// </summary>
    internal static class Tiling
    {
        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x0A, EVENT_SYSTEM_MOVESIZEEND = 0x0B, WINEVENT_SKIPOWNPROCESS = 2;
        private const uint SPI_GETWINARRANGING = 0x82, SPI_SETWINARRANGING = 0x83;
        private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200, SWP_ASYNCWINDOWPOS = 0x4000;
        private const int GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000, WS_THICKFRAME = 0x40000;

        private const int EdgePx = 4;              // the pointer has to touch the edge ...
        private const int EdgeKeepPx = 14;         // ... but may wobble this far away before the frame closes again
        private const double GapDip = 6;           // tiled windows keep a small margin, like macOS
        private const int SizeChangesForResize = 2;   // one size change is a maximized / snapped window being restored

        // Release spring
        private const double SpringMinSpeed = 700;   // px/s: slower releases just stop
        private const double SpringPxPerSpeed = 0.01; // overshoot per px/s of release speed ...
        private const double SpringMaxPx = 16;        // ... capped here
        private const double SpringMinPx = 2;
        private const double VelocityWindowMs = 70;   // velocity is measured over the last stretch of the drag
        private const double GlideMs = 70, SettleMs = 300;
        private const double SpringOmega = 22, SpringDamping = 0.7;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SpiGetBool(uint action, uint param, out int pv, uint winIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SpiSet(uint action, uint param, IntPtr pv, uint winIni);

        private enum Zone { None, Left, Right, Fill }

        private struct Sample { public double T; public int X, Y; }

        private class Drag
        {
            public IntPtr H;
            public int W, Ht, SizeChanges;
            public bool WasZoomed, Tileable;
            public Zone Zone;
            public Rect ZoneRect;   // physical pixels, the window's visible frame goes here
            public readonly List<Sample> Samples = new List<Sample>();
            public bool Resizing { get { return SizeChanges >= SizeChangesForResize; } }
        }

        private class Spring
        {
            public IntPtr H;
            public int X0, Y0, W, Ht;
            public double Dx, Dy, Start;
        }

        private static Native.WinEventDelegate proc;
        private static IntPtr hook;
        private static bool running, frames;
        private static Drag drag;
        private static Spring spring;
        private static System.Windows.Forms.Screen[] screens;
        private static PreviewWindow preview;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static int originalArranging = -1;

        private static double Now { get { return clock.Elapsed.TotalMilliseconds; } }

        // ------------------------------------------------------------------ start / stop
        /// <summary>
        /// Start, or re-read Settings.TilingPreview / Settings.WindowSpring (safe to call again after a settings change).
        /// </summary>
        public static void Start()
        {
            if (Settings.TilingPreview) ApplySnapOff(); else RestoreSnap();
            bool want = Settings.TilingPreview || Settings.WindowSpring;
            if (want && hook == IntPtr.Zero)
            {
                proc = OnEvent;
                hook = Native.SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, proc, 0, 0, WINEVENT_SKIPOWNPROCESS);
            }
            else if (!want) Unhook();
            running = want;
        }

        public static void Stop()
        {
            running = false;
            Unhook();
            if (preview != null) { try { preview.Close(); } catch (Exception ex) { Program.Log(ex); } preview = null; }
            RestoreSnap();
        }

        /// <summary>For MacLook.exe --restore after a crash: turn Windows' own Snap back on if Mac Look turned it off.</summary>
        public static void RestoreAfterCrash() { RestoreSnap(); }

        private static void Unhook()
        {
            if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
            hook = IntPtr.Zero;
            drag = null;
            spring = null;
            StopFrames();
            if (preview != null) preview.Vanish();
        }

        /// <summary>
        /// Windows' Snap would fight ours (its own outline, its own layouts), so it's off for this session only:
        /// not saved to the profile, and switched back when Mac Look quits. The original is remembered on disk,
        /// so a crash can't make "off" look like the user's choice.
        /// </summary>
        private static void ApplySnapOff()
        {
            if (originalArranging < 0 && Settings.OriginalWinArranging >= 0) originalArranging = Settings.OriginalWinArranging;
            int on;
            if (originalArranging < 0 && SpiGetBool(SPI_GETWINARRANGING, 0, out on, 0))
            {
                originalArranging = on != 0 ? 1 : 0;
                Settings.OriginalWinArranging = originalArranging;
                try { Settings.Save(); } catch (Exception ex) { Program.Log(ex); }
            }
            if (originalArranging < 0) return;
            SetArranging(false);
        }

        private static void RestoreSnap()
        {
            if (originalArranging < 0) originalArranging = Settings.OriginalWinArranging;
            if (originalArranging < 0) return;
            SetArranging(originalArranging != 0);
            originalArranging = -1;
            Settings.OriginalWinArranging = -1;
            try { Settings.Save(); } catch (Exception ex) { Program.Log(ex); }
        }

        private static void SetArranging(bool on)
        {
            // The docs disagree on whether the flag goes in uiParam or pvParam: pass it in both. fWinIni = 0: session only.
            SpiSet(SPI_SETWINARRANGING, on ? 1u : 0u, new IntPtr(on ? 1 : 0), 0);
        }

        // ------------------------------------------------------------------ events
        private static void OnEvent(IntPtr hk, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // Called from inside the message loop: nothing may escape from here.
            try
            {
                if (!running || idObject != 0 || hwnd == IntPtr.Zero) return;
                if (ev == EVENT_SYSTEM_MOVESIZESTART) BeginDrag(hwnd);
                else if (ev == EVENT_SYSTEM_MOVESIZEEND) EndDrag(hwnd);
            }
            catch (Exception ex) { Program.Log(ex); drag = null; spring = null; }
        }

        private static bool IsCandidate(IntPtr h)
        {
            if (WindowTracker.IsOwn(h) || WindowTracker.IsShellWindow(h)) return false;
            if ((Native.GetWindowLongPtr(h, GWL_STYLE).ToInt64() & WS_CHILD) != 0) return false;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            return true;
        }

        private static void BeginDrag(IntPtr h)
        {
            spring = null;   // a new drag always wins over a settling window
            drag = null;
            if (preview != null) preview.Vanish();
            if (!IsCandidate(h)) return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            long style = Native.GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            drag = new Drag
            {
                H = h, W = r.Right - r.Left, Ht = r.Bottom - r.Top, WasZoomed = Native.IsZoomed(h),
                // Only windows the user can resize can be tiled; a fixed-size dialog would break.
                Tileable = (style & WS_THICKFRAME) != 0
            };
            screens = System.Windows.Forms.Screen.AllScreens;
            StartFrames();
        }

        private static void EndDrag(IntPtr h)
        {
            var d = drag;
            if (d == null || d.H != h) return;
            drag = null;
            if (d.Zone != Zone.None && Settings.TilingPreview && Native.IsWindow(h))
            {
                TileTo(h, d.ZoneRect);
                if (preview != null) preview.Settle();
                return;
            }
            if (preview != null) preview.Vanish();
            if (Settings.WindowSpring && !d.Resizing && Native.IsWindow(h) && !Native.IsZoomed(h) && !Native.IsIconic(h)) StartSpring(d);
        }

        // ------------------------------------------------------------------ per-frame loop (drag + spring)
        private static void StartFrames()
        {
            if (frames) return;
            frames = true;
            CompositionTarget.Rendering += OnFrame;
            Smooth.Begin();
        }

        private static void StopFrames()
        {
            if (!frames) return;
            frames = false;
            CompositionTarget.Rendering -= OnFrame;
            Smooth.End();
        }

        private static void OnFrame(object sender, EventArgs e)
        {
            try
            {
                if (drag != null) TickDrag();
                if (spring != null) TickSpring();
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                drag = null; spring = null;
                if (preview != null) preview.Vanish();
            }
            if (drag == null && spring == null) StopFrames();
        }

        private static void TickDrag()
        {
            var d = drag;
            Native.RECT r;
            if (!Native.IsWindow(d.H) || !Native.GetWindowRect(d.H, out r))
            {
                // The window went away mid-drag: MOVESIZEEND may never come.
                drag = null;
                if (preview != null) preview.Vanish();
                return;
            }
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            bool zoomed = Native.IsZoomed(d.H);
            if ((w != d.W || ht != d.Ht) && !zoomed && !d.WasZoomed) d.SizeChanges++;
            d.W = w; d.Ht = ht; d.WasZoomed = zoomed;

            double now = Now;
            d.Samples.Add(new Sample { T = now, X = r.Left, Y = r.Top });
            while (d.Samples.Count > 2 && d.Samples[0].T < now - 250) d.Samples.RemoveAt(0);

            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            Rect zr = Rect.Empty;
            Zone z = Zone.None;
            if (Settings.TilingPreview && d.Tileable && !d.Resizing) z = ZoneAt(p, d.Zone, out zr);
            if (z == d.Zone && (z == Zone.None || zr == d.ZoneRect)) return;
            d.Zone = z;
            d.ZoneRect = zr;
            if (z == Zone.None) { if (preview != null) preview.Shrink(p); return; }
            if (preview != null && preview.Dark != Theme.Dark)
            {
                // The theme changed since the frame was built: build it again in the new colours.
                try { preview.Close(); } catch (Exception ex) { Program.Log(ex); }
                preview = null;
            }
            if (preview == null) preview = new PreviewWindow();
            preview.Open(zr, p, d.H);
        }

        /// <summary>Which tile the pointer asks for, and its rectangle (physical pixels).</summary>
        private static Zone ZoneAt(Native.POINT p, Zone current, out Rect rect)
        {
            rect = Rect.Empty;
            if (screens == null) return Zone.None;
            System.Windows.Forms.Screen scr = null;
            foreach (var s in screens) if (s.Bounds.Contains(p.X, p.Y)) { scr = s; break; }
            if (scr == null) return Zone.None;
            var b = scr.Bounds;
            Zone z = Zone.None;
            // An edge shared with another screen is just a way across, not a place to tile.
            if (p.X - b.Left <= Reach(current, Zone.Left) && !ScreenAt(b.Left - 2, p.Y)) z = Zone.Left;
            else if (b.Right - 1 - p.X <= Reach(current, Zone.Right) && !ScreenAt(b.Right + 1, p.Y)) z = Zone.Right;
            else if (p.Y - b.Top <= Reach(current, Zone.Fill) && !ScreenAt(p.X, b.Top - 2)) z = Zone.Fill;
            if (z == Zone.None) return z;

            var wa = scr.WorkingArea;
            double g = Math.Round(GapDip * DpiScale), half = Math.Floor(wa.Width / 2.0);
            if (z == Zone.Left) rect = new Rect(wa.Left + g, wa.Top + g, half - g - g / 2, wa.Height - 2 * g);
            else if (z == Zone.Right) rect = new Rect(wa.Left + half + g / 2, wa.Top + g, wa.Width - half - g - g / 2, wa.Height - 2 * g);
            else rect = new Rect(wa.Left + g, wa.Top + g, wa.Width - 2 * g, wa.Height - 2 * g);
            if (rect.Width < 100 || rect.Height < 100) { rect = Rect.Empty; return Zone.None; }
            return z;
        }

        private static int Reach(Zone current, Zone z) { return current == z ? EdgeKeepPx : EdgePx; }

        private static bool ScreenAt(int x, int y)
        {
            foreach (var s in screens) if (s.Bounds.Contains(x, y)) return true;
            return false;
        }

        private static double dpiScale;
        private static double DpiScale
        {
            get
            {
                if (dpiScale <= 0)
                {
                    try { using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96.0; }
                    catch { dpiScale = 1; }
                }
                return dpiScale;
            }
        }

        /// <summary>Puts the window's visible frame exactly on the tile (the invisible resize borders hang outside it).</summary>
        private static void TileTo(IntPtr h, Rect zone)
        {
            if (Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            Native.RECT wr;
            if (!Native.GetWindowRect(h, out wr)) return;
            var fr = Native.FrameRect(h);
            int l = fr.Left - wr.Left, t = fr.Top - wr.Top, rr = wr.Right - fr.Right, bb = wr.Bottom - fr.Bottom;
            int x = (int)Math.Round(zone.X), y = (int)Math.Round(zone.Y);
            int w = (int)Math.Round(zone.Width), ht = (int)Math.Round(zone.Height);
            // Async: a hung app must not freeze Mac Look.
            Native.SetWindowPos(h, IntPtr.Zero, x - l, y - t, w + l + rr, ht + t + bb, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);
        }

        // ------------------------------------------------------------------ release spring
        private static void StartSpring(Drag d)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(d.H, out r)) return;
            double now = Now;
            Sample first = new Sample { T = -1 };
            foreach (var s in d.Samples) if (s.T >= now - VelocityWindowMs) { first = s; break; }
            if (first.T < 0) return;
            double dt = (now - first.T) / 1000.0;
            if (dt < 0.02) return;
            double vx = (r.Left - first.X) / dt, vy = (r.Top - first.Y) / dt;
            double speed = Math.Sqrt(vx * vx + vy * vy);
            if (speed < SpringMinSpeed) return;
            double amp = Math.Min(SpringMaxPx, speed * SpringPxPerSpeed);
            if (amp < SpringMinPx) return;
            double dx = vx / speed * amp, dy = vy / speed * amp;

            // Never for a window hanging off a screen (or one the glide would push off it).
            var fr = Native.FrameRect(d.H);
            var rest = new System.Drawing.Rectangle(fr.Left, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
            var peak = rest;
            peak.Offset((int)Math.Round(dx), (int)Math.Round(dy));
            if (!OnOneScreen(rest) || !OnOneScreen(peak)) return;

            spring = new Spring { H = d.H, X0 = r.Left, Y0 = r.Top, W = r.Right - r.Left, Ht = r.Bottom - r.Top, Dx = dx, Dy = dy, Start = now };
            StartFrames();
        }

        private static bool OnOneScreen(System.Drawing.Rectangle r)
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens) if (s.WorkingArea.Contains(r)) return true;
            return false;
        }

        /// <summary>How far along the overshoot the window is (0 = rest, 1 = full overshoot) t ms after release.</summary>
        private static double SpringCurve(double t)
        {
            if (t < GlideMs)
            {
                double q = 1 - t / GlideMs;
                return 1 - q * q;   // keeps gliding, slowing down
            }
            // Then a damped spring pulls it back to where it was let go.
            double s = (t - GlideMs) / 1000.0;
            double wd = SpringOmega * Math.Sqrt(1 - SpringDamping * SpringDamping);
            return Math.Exp(-SpringDamping * SpringOmega * s) * (Math.Cos(wd * s) + SpringDamping * SpringOmega / wd * Math.Sin(wd * s));
        }

        private static void TickSpring()
        {
            var s = spring;
            Native.RECT r;
            if (!Native.IsWindow(s.H) || Native.IsIconic(s.H) || Native.IsZoomed(s.H) || !Native.GetWindowRect(s.H, out r)) { spring = null; return; }
            // Something else resized or moved it (an app, a snapping tool): let go at once.
            if (r.Right - r.Left != s.W || r.Bottom - r.Top != s.Ht ||
                Math.Abs(r.Left - s.X0) > SpringMaxPx + 4 || Math.Abs(r.Top - s.Y0) > SpringMaxPx + 4) { spring = null; return; }
            double t = Now - s.Start;
            int x = s.X0, y = s.Y0;
            bool done = t >= GlideMs + SettleMs;
            if (!done)
            {
                double k = SpringCurve(t);
                x += (int)Math.Round(s.Dx * k);
                y += (int)Math.Round(s.Dy * k);
            }
            if (x != r.Left || y != r.Top)
                Native.SetWindowPos(s.H, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);
            if (done) spring = null;
        }

        // ------------------------------------------------------------------ the glass preview frame
        /// <summary>
        /// The frosted frame showing where the window will land. Click-through, never activated, not in Alt+Tab,
        /// and kept just below the dragged window so the window stays on top of it.
        /// </summary>
        private class PreviewWindow : Window
        {
            [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
            private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
            private const long WS_EX_TRANSPARENT = 0x20;
            private const double MarginDip = 56, Radius = 12, Tiny = 0.06;
            private const double OpenMs = 240, CloseMs = 170, SettleFadeMs = 230, FrostFadeMs = 110;

            private readonly Grid panel = new Grid();
            private readonly Border frost;
            private readonly ScaleTransform zoom = new ScaleTransform(Tiny, Tiny);
            private IntPtr hwnd;
            private double scale = 1;
            private Rect zone = Rect.Empty;
            private int generation;
            public readonly bool Dark = Theme.Dark;

            public PreviewWindow()
            {
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; ShowActivated = false; Topmost = false; ResizeMode = ResizeMode.NoResize;
                Focusable = false; IsHitTestVisible = false;

                bool dark = Dark;
                var cr = new CornerRadius(Radius);
                var tint = new Border { CornerRadius = cr, Background = new SolidColorBrush(dark ? Color.FromArgb(150, 40, 40, 44) : Color.FromArgb(120, 250, 250, 252)) };
                frost = new Border { CornerRadius = cr, Opacity = 0 };
                var wash = new Border
                {
                    CornerRadius = cr,
                    Background = new LinearGradientBrush(
                        dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(90, 255, 255, 255),
                        dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(36, 255, 255, 255), 90)
                };
                var outer = new Border { CornerRadius = cr, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(dark ? (byte)110 : (byte)40, 0, 0, 0)) };
                var inner = new Border
                {
                    CornerRadius = new CornerRadius(Radius - 1), Margin = new Thickness(1), BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(170, 255, 255, 255))
                };
                panel.Children.Add(frost);
                panel.Children.Add(tint);
                panel.Children.Add(wash);
                panel.Children.Add(outer);
                panel.Children.Add(inner);
                panel.Margin = new Thickness(MarginDip);
                panel.RenderTransform = zoom;
                panel.Opacity = 0;
                panel.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = dark ? 0.45 : 0.28, Color = Colors.Black };
                Content = panel;

                SourceInitialized += (s, e) =>
                {
                    hwnd = new WindowInteropHelper(this).Handle;
                    scale = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;
                    long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                    // Click-through, never focused, not in Alt+Tab.
                    Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                    // Our own frame stays out of the screen pictures the frost is made from (Windows 10 2004+).
                    try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); } catch { }
                };
                new WindowInteropHelper(this).EnsureHandle();
            }

            /// <summary>Spring open from the pointer to fill the tile (physical pixels), just below the dragged window.</summary>
            public void Open(Rect tile, Native.POINT p, IntPtr below)
            {
                generation++;
                bool fresh = !IsVisible || panel.Opacity < 0.05;
                if (tile != zone)
                {
                    zone = tile;
                    Left = tile.X / scale - MarginDip; Top = tile.Y / scale - MarginDip;
                    Width = tile.Width / scale + 2 * MarginDip; Height = tile.Height / scale + 2 * MarginDip;
                    frost.BeginAnimation(OpacityProperty, null);
                    frost.Opacity = 0;
                    frost.Background = null;
                    LoadFrost(tile);
                    fresh = true;
                }
                double w = tile.Width / scale, h = tile.Height / scale;
                zoom.CenterX = Math.Max(0, Math.Min(w, (p.X - tile.X) / scale));
                zoom.CenterY = Math.Max(0, Math.Min(h, (p.Y - tile.Y) / scale));
                if (fresh)
                {
                    zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null); zoom.ScaleX = Tiny;
                    zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null); zoom.ScaleY = Tiny;
                    panel.BeginAnimation(OpacityProperty, null); panel.Opacity = 0;
                }
                if (!IsVisible) Show();
                // Right under the dragged window: it stays on top of its own preview.
                if (below != IntPtr.Zero)
                    Native.SetWindowPos(hwnd, below, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

                var ease = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(OpenMs)) { EasingFunction = ease });
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(OpenMs)) { EasingFunction = ease });
                panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(OpenMs * 0.55)));
            }

            /// <summary>The pointer left the edge: shrink back toward it and fade.</summary>
            public void Shrink(Native.POINT p)
            {
                if (!IsVisible) return;
                int gen = ++generation;
                if (!zone.IsEmpty)
                {
                    zoom.CenterX = Math.Max(0, Math.Min(zone.Width / scale, (p.X - zone.X) / scale));
                    zoom.CenterY = Math.Max(0, Math.Min(zone.Height / scale, (p.Y - zone.Y) / scale));
                }
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(Tiny, TimeSpan.FromMilliseconds(CloseMs)) { EasingFunction = ease });
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(Tiny, TimeSpan.FromMilliseconds(CloseMs)) { EasingFunction = ease });
                FadeOut(CloseMs, gen);
            }

            /// <summary>The window landed on the tile: the frame just melts away behind it.</summary>
            public void Settle()
            {
                if (!IsVisible) return;
                int gen = ++generation;
                // Finish growing (a quick release can come mid-spring) while fading.
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(SettleFadeMs * 0.6)));
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(SettleFadeMs * 0.6)));
                FadeOut(SettleFadeMs, gen);
            }

            /// <summary>Gone right now (a new drag started, or tiling was switched off).</summary>
            public void Vanish()
            {
                generation++;
                zone = Rect.Empty;
                panel.BeginAnimation(OpacityProperty, null);
                panel.Opacity = 0;
                if (IsVisible) Hide();
            }

            private void FadeOut(double ms, int gen)
            {
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                a.Completed += (s, e) =>
                {
                    if (gen != generation) return;   // reopened meanwhile
                    zone = Rect.Empty;
                    if (IsVisible) Hide();
                };
                panel.BeginAnimation(OpacityProperty, a);
            }

            /// <summary>
            /// Frost: a heavily blurred picture of what's under the tile. Taken on a worker thread (a big screen
            /// grab takes a few ms) and faded in when ready; until then the plain tint shows.
            /// </summary>
            private void LoadFrost(Rect tile)
            {
                int x = (int)tile.X, y = (int)tile.Y, w = (int)tile.Width, h = (int)tile.Height;
                bool dark = Dark;
                var ui = Dispatcher;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    BitmapSource img = null;
                    try
                    {
                        var snap = ScreenCapture.CaptureRegion(x, y, w, h);
                        if (snap != null) img = GlassFx.Backdrop(snap, 8, 6, dark ? 0.25 : 0.0);
                    }
                    catch (Exception ex) { Program.Log(ex); }
                    if (img == null) return;
                    ui.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (zone != tile) return;   // a different tile by now
                            var brush = new ImageBrush(img) { Stretch = Stretch.Fill };
                            brush.Freeze();
                            frost.Background = brush;
                            frost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(FrostFadeMs)));
                        }
                        catch (Exception ex) { Program.Log(ex); }
                    }));
                });
            }
        }
    }
}
