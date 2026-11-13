using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>
    /// macOS Stage Manager: the active window stays centre stage, every other app window is tucked away into a
    /// strip of tilted snapshots on the left edge. Click a snapshot to bring that window back (the one on stage
    /// takes its place); activating any other window (Alt+Tab, Dock, taskbar) does the same. Tucked windows are
    /// simply minimized, so turning Stage Manager off (or Mac Look crashing) never loses a window.
    /// </summary>
    internal static class StageManager
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17;
        private const uint WINEVENT_SKIPOWNPROCESS = 2;
        private const int MaxShown = 5;               // snapshots in the strip; older tucked windows wait in the Dock
        private const int FgSettleMs = 150;           // let Alt+Tab & co. settle before reacting to a new active window
        private const int BusyMs = 650;               // ignore activations we cause ourselves for this long
        private const int EdgePollMs = 60, HideDelayMs = 450;
        private const int CascadeMs = 45;             // windows tucked together leave one after another
        private const int ComeBackGraceMs = 1500;     // a tucked window still showing after this was restored by someone else
        private const double WindowRadius = 8;        // Windows 11 window corners (DIPs)

        internal class Tucked
        {
            public IntPtr H;
            public AppWindow W;
            public BitmapSource Shot, Icon;
            public Rect Frame;           // physical pixels, where it was (and comes back to)
            public double Aspect = 1.5;
            public bool Pending;         // its snapshot is still flying into the strip
            public DateTime Since;
        }

        private static readonly List<Tucked> tucked = new List<Tucked>();   // most recent first
        private static IntPtr stage;
        private static readonly List<IntPtr> companions = new List<IntPtr>();   // other windows of the stage app, sharing the stage
        private static bool running, checkQueued;
        private static Native.WinEventDelegate proc;
        private static IntPtr fgHook, minHook;
        private static StripWindow strip;
        private static DispatcherTimer edgeTimer;
        private static DateTime busyUntil = DateTime.MinValue, leftAt = DateTime.MinValue;

        public static bool IsRunning { get { return running; } }

        /// <summary>Control Center / Settings switch: flips Settings.StageManager, saves, starts or stops.</summary>
        public static void Toggle()
        {
            Settings.StageManager = !Settings.StageManager;
            try { Settings.Save(); } catch (Exception ex) { Program.Log(ex); }
            if (Settings.StageManager) Start(); else StopAnimated();
        }

        /// <summary>Starts if Settings.StageManager is on (safe to call when it's off or already running).</summary>
        public static void Start()
        {
            if (running || !Settings.StageManager) return;
            try
            {
                running = true;
                proc = OnEvent;
                fgHook = Native.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, WINEVENT_SKIPOWNPROCESS);
                minHook = Native.SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, proc, 0, 0, WINEVENT_SKIPOWNPROCESS);
                strip = new StripWindow();
                strip.Show();
                edgeTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(EdgePollMs) };
                edgeTimer.Tick += (s, e) => EdgeTick();
                edgeTimer.Start();
                busyUntil = DateTime.Now.AddMilliseconds(BusyMs + CascadeMs * MaxShown);

                // The active window stays; everything else goes into the strip, most recent first.
                List<AppWindow> wins;
                try { wins = WindowTracker.Enumerate(); } catch (Exception ex) { Program.Log(ex); wins = new List<AppWindow>(); }
                IntPtr fg = Native.GetForegroundWindow();
                IntPtr root = fg == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(fg, Native.GA_ROOTOWNER);
                stage = IntPtr.Zero;
                foreach (var w in wins) if ((w.Hwnd == fg || w.Hwnd == root) && Eligible(w.Hwnd)) { stage = w.Hwnd; break; }
                if (stage == IntPtr.Zero) foreach (var w in wins) if (Eligible(w.Hwnd)) { stage = w.Hwnd; break; }
                int n = 0;
                foreach (var w in wins)
                {
                    if (w.Hwnd == stage || !Eligible(w.Hwnd)) continue;
                    // Animated ones go after the strip below is laid out, so each lands on its final slot.
                    Tuck(w, tucked.Count, n < MaxShown, n < MaxShown ? 10 + n * CascadeMs : 0);
                    n++;
                }
                strip.Layout();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Turn off right away and give every tucked window back (used at quit, so nothing is animated).</summary>
        public static void Stop()
        {
            if (!running) return;
            var list = Shutdown();
            stage = IntPtr.Zero;
            companions.Clear();
            // Oldest first, so the most recent ends up on top. Async: a hung app can't hold up quitting.
            for (int i = list.Count - 1; i >= 0; i--)
                if (Native.IsWindow(list[i].H) && Native.IsIconic(list[i].H)) Native.ShowWindowAsync(list[i].H, Native.SW_RESTORE);
        }

        /// <summary>Turned off from Control Center: the snapshots fly back out into their windows.</summary>
        private static void StopAnimated()
        {
            if (!running) return;
            var rects = new Dictionary<Tucked, Rect>();
            var angles = new Dictionary<Tucked, double>();
            if (strip != null)
                foreach (var t in tucked)
                {
                    Rect r; double a;
                    if (strip.TakeCard(t, out r, out a)) { rects[t] = r; angles[t] = a; }
                }
            var list = Shutdown();
            IntPtr keep = stage;
            stage = IntPtr.Zero;
            companions.Clear();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (!Native.IsWindow(t.H) || !Native.IsIconic(t.H)) continue;
                IntPtr h = t.H;
                Rect from;
                if (Settings.AnimateWindows && t.Shot != null && rects.TryGetValue(t, out from))
                    new Flight(t.Shot, from, t.Frame, angles[t], 0, StripWindow.CardRadius, WindowRadius, null,
                        () => Native.ShowWindowAsync(h, Native.SW_RESTORE), true).Run();
                else Native.ShowWindowAsync(h, Native.SW_RESTORE);
            }
            // Restoring activates each window in turn: give the stage window its focus back afterwards.
            if (keep != IntPtr.Zero)
                Launcher.Later(Flight.Duration + 150, () => { if (!running && Native.IsWindow(keep) && !Native.IsIconic(keep)) Native.ActivateWindow(keep); });
        }

        private static List<Tucked> Shutdown()
        {
            running = false;
            if (fgHook != IntPtr.Zero) Native.UnhookWinEvent(fgHook);
            if (minHook != IntPtr.Zero) Native.UnhookWinEvent(minHook);
            fgHook = minHook = IntPtr.Zero;
            if (edgeTimer != null) edgeTimer.Stop();
            edgeTimer = null;
            var list = tucked.ToList();
            tucked.Clear();
            if (strip != null) { try { strip.Close(); } catch (Exception ex) { Program.Log(ex); } strip = null; }
            return list;
        }

        private static Tucked Find(IntPtr h)
        {
            foreach (var t in tucked) if (t.H == h) return t;
            return null;
        }

        /// <summary>A window Stage Manager may move around (games and fullscreen video are left alone).</summary>
        private static bool Eligible(IntPtr h)
        {
            if (h == IntPtr.Zero || !Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
            if (WindowTracker.IsOwn(h) || Native.IsCloaked(h)) return false;
            if (!Native.IsZoomed(h))
            {
                var r = Native.FrameRect(h);
                var b = System.Windows.Forms.Screen.FromHandle(h).Bounds;
                if (r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ events
        private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // Called from inside the message loop: nothing may escape from here.
            try
            {
                if (!running || idObject != 0 || hwnd == IntPtr.Zero) return;
                if (ev == EVENT_SYSTEM_FOREGROUND) QueueCheck(FgSettleMs);
                else if (ev == EVENT_SYSTEM_MINIMIZESTART)
                {
                    // The user minimized the stage window into the Dock: the stage is empty now.
                    if (hwnd == stage && Find(hwnd) == null) stage = IntPtr.Zero;
                    companions.Remove(hwnd);
                }
                else if (ev == EVENT_SYSTEM_MINIMIZEEND)
                {
                    // A tucked window was restored by someone else (Dock, taskbar, the app itself).
                    var t = Find(hwnd);
                    if (t != null && (DateTime.Now - t.Since).TotalMilliseconds > 300)
                    {
                        tucked.Remove(t);
                        if (strip != null) strip.Layout();
                        QueueCheck(FgSettleMs);
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static void QueueCheck(int ms)
        {
            if (checkQueued) return;
            checkQueued = true;
            Launcher.Later(Math.Max(1, ms), CheckForeground);
        }

        /// <summary>Another window became active: it takes the stage and the old stage window is tucked away.</summary>
        private static void CheckForeground()
        {
            checkQueued = false;
            try
            {
                if (!running || Program.IsFullscreen) return;
                var wait = busyUntil - DateTime.Now;
                if (wait.TotalMilliseconds > 0) { QueueCheck((int)wait.TotalMilliseconds + 20); return; }
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == stage || WindowTracker.IsOwn(fg)) return;
                IntPtr root = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
                if (root == stage && stage != IntPtr.Zero) return;   // a dialog of the stage window
                List<AppWindow> wins = WindowTracker.Enumerate();
                AppWindow w = null;
                foreach (var x in wins) if (x.Hwnd == fg || x.Hwnd == root) { w = x; break; }
                if (w == null || !Eligible(w.Hwnd)) return;   // the desktop, a tool window, a game...
                var back = Find(w.Hwnd);
                if (back != null) tucked.Remove(back);   // it came back by itself (Alt+Tab, Dock, taskbar)

                IntPtr old = stage;
                stage = w.Hwnd;
                companions.Remove(w.Hwnd);
                AppWindow ow = null;
                if (old != IntPtr.Zero && old != stage && Eligible(old))
                {
                    foreach (var x in wins) if (x.Hwnd == old) { ow = x; break; }
                    if (ow == null) ow = WindowTracker.Describe(old);
                }
                if (ow != null && ow.GroupKey == w.GroupKey)
                {
                    // Another window of the same app: like macOS, it joins the stage instead of pushing its sibling away.
                    if (!companions.Contains(old)) companions.Add(old);
                    return;
                }
                // A different app: the whole stage set goes to the strip, the window that was active ending up on top.
                foreach (var c in companions.ToList())
                {
                    if (!Eligible(c)) continue;
                    var cw = WindowTracker.Describe(c);
                    if (cw != null) Tuck(cw, 0, true, 0);
                }
                companions.Clear();
                if (ow != null) Tuck(ow, 0, true, 0);
                if (strip != null) strip.Layout();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ------------------------------------------------------------------ tuck / bring back
        private static void Tuck(AppWindow w, int index, bool animate, int delayMs)
        {
            if (Find(w.Hwnd) != null) return;
            var fr = Native.FrameRect(w.Hwnd);
            var t = new Tucked { H = w.Hwnd, W = w, Frame = new Rect(fr.Left, fr.Top, Math.Max(1, fr.Right - fr.Left), Math.Max(1, fr.Bottom - fr.Top)), Since = DateTime.Now };
            t.Aspect = AspectOf(t.Frame);
            t.Pending = animate && Settings.AnimateWindows;
            try { t.Icon = IconStyler.Get(w.ShellTarget, 64); } catch (Exception ex) { Program.Log(ex); }
            tucked.Insert(Math.Min(index, tucked.Count), t);
            if (delayMs > 0) Launcher.Later(delayMs, () => DoTuck(t, animate));
            else DoTuck(t, animate);
        }

        private static double AspectOf(Rect r)
        {
            return r.Height > 0 ? Math.Max(0.6, Math.Min(2.6, r.Width / r.Height)) : 1.5;
        }

        private static void DoTuck(Tucked t, bool animate)
        {
            try
            {
                if (!running || !tucked.Contains(t)) return;
                IntPtr h = t.H;
                if (!Native.IsWindow(h)) { tucked.Remove(t); strip.Layout(); return; }
                if (Native.IsIconic(h)) { t.Pending = false; strip.Layout(); return; }
                bool shown = tucked.IndexOf(t) < MaxShown;
                if (shown)
                {
                    // The snapshot is taken right before the window goes, so the strip shows it as it was.
                    Rect frame;
                    t.Shot = WindowShot.Capture(h, out frame);
                    if (t.Shot != null) { t.Frame = frame; t.Aspect = AspectOf(frame); }
                }
                t.Since = DateTime.Now;
                t.Pending = animate && shown && t.Shot != null && Settings.AnimateWindows;
                strip.Layout();
                if (!t.Pending) { strip.Reveal(t); Minimize(h); return; }

                strip.SlideIn();
                var to = strip.SlotRectPx(t);
                new Flight(t.Shot, t.Frame, to, 0, StripWindow.Angle, WindowRadius, StripWindow.CardRadius,
                    () => Minimize(h),                                             // once the snapshot covers it
                    () => { t.Pending = false; if (strip != null) strip.Reveal(t); }, false).Run();
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                t.Pending = false;
                try { Minimize(t.H); } catch { }
            }
        }

        private static void Minimize(IntPtr h)
        {
            if (!Native.IsWindow(h) || Native.IsIconic(h)) return;
            WindowAnimator.SkipNextMinimize(h);   // our own animation replaces the genie
            Native.ShowWindowAsync(h, Native.SW_MINIMIZE);
        }

        /// <summary>A snapshot in the strip was clicked: its window takes the stage, the current one is tucked.</summary>
        private static void Bring(Tucked t)
        {
            try
            {
                if (!running || !tucked.Contains(t)) return;
                busyUntil = DateTime.Now.AddMilliseconds(BusyMs);
                Rect from; double angle;
                bool hadCard = strip.TakeCard(t, out from, out angle);
                tucked.Remove(t);
                IntPtr old = stage;
                stage = t.H;
                foreach (var c in companions.ToList())
                {
                    if (c == t.H || !Eligible(c)) continue;
                    var cw = WindowTracker.Describe(c);
                    if (cw != null) Tuck(cw, 0, true, 0);
                }
                companions.Clear();
                if (old != IntPtr.Zero && old != t.H && Eligible(old))
                {
                    var ow = WindowTracker.Describe(old);
                    if (ow != null) Tuck(ow, 0, true, 0);
                }
                strip.Layout();
                if (!Native.IsWindow(t.H)) { stage = IntPtr.Zero; return; }
                IntPtr h = t.H;
                if (hadCard && t.Shot != null && Settings.AnimateWindows)
                    new Flight(t.Shot, from, t.Frame, angle, 0, StripWindow.CardRadius, WindowRadius, null, () => Native.ActivateWindow(h), true).Run();
                else Native.ActivateWindow(h);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ------------------------------------------------------------------ strip auto-hide (like the Dock's)
        private static void EdgeTick()
        {
            try
            {
                if (!running || strip == null) return;
                Prune();
                if (Program.IsFullscreen) { if (strip.IsVisible) strip.Hide(); return; }
                if (!strip.IsVisible) strip.Show();
                strip.Place();
                if (tucked.Count == 0) return;

                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return;
                var area = strip.CardsAreaPx;
                bool covered = false;
                if (stage != IntPtr.Zero && Native.IsWindowVisible(stage) && !Native.IsIconic(stage))
                {
                    var fr = Native.FrameRect(stage);
                    covered = fr.Left < area.Right && fr.Right > area.Left && fr.Top < area.Bottom && fr.Bottom > area.Top;
                }
                if (!covered) { leftAt = DateTime.MinValue; strip.SlideIn(); return; }
                if (!strip.Shown)
                {
                    var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    if (p.X <= b.Left + 1 && p.Y >= area.Top && p.Y <= area.Bottom) { leftAt = DateTime.MinValue; strip.SlideIn(); }
                    return;
                }
                bool over = p.X >= area.Left - 4 && p.X < area.Right + 12 && p.Y >= area.Top && p.Y <= area.Bottom;
                if (over || Flight.Active > 0) { leftAt = DateTime.MinValue; return; }
                if (leftAt == DateTime.MinValue) leftAt = DateTime.Now;
                else if ((DateTime.Now - leftAt).TotalMilliseconds > HideDelayMs) strip.SlideOut();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Forget windows that closed, or that came back without telling us.</summary>
        private static void Prune()
        {
            if (stage != IntPtr.Zero && (!Native.IsWindow(stage) || !Native.IsWindowVisible(stage))) stage = IntPtr.Zero;
            companions.RemoveAll(c => !Native.IsWindow(c) || !Native.IsWindowVisible(c) || Native.IsIconic(c));
            var now = DateTime.Now;
            int removed = tucked.RemoveAll(t => !Native.IsWindow(t.H) || !Native.IsWindowVisible(t.H) ||
                (!t.Pending && !Native.IsIconic(t.H) && (now - t.Since).TotalMilliseconds > ComeBackGraceMs));
            if (removed > 0) strip.Layout();
        }

        // ------------------------------------------------------------------ 3D snapshot
        /// <summary>
        /// A window snapshot on a plane tilted around its vertical axis, seen through a perspective camera.
        /// The viewport (Host) is Pad larger than the snapshot on every side, so the near edge has room to grow.
        /// Built the same way for the strip and for flights, so a flight lands exactly on its card.
        /// </summary>
        private class Scene
        {
            public const double Pad = 0.14, Fov = 40;
            private const double Edge = 2;   // transparent rim: the plane's edge is drawn smooth by the texture, not jagged by the 3D edge
            public readonly Grid Host = new Grid();
            public readonly Border Face;
            public readonly AxisAngleRotation3D Rot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            public readonly double FaceW;

            public Scene(BitmapSource shot, BitmapSource icon, double aspect, double faceW)
            {
                FaceW = faceW;
                double faceH = faceW / aspect;
                Face = new Border { Width = faceW, Height = faceH, Margin = new Thickness(Edge) };
                if (shot != null)
                {
                    var brush = new ImageBrush(shot) { Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                    Face.Background = brush;
                }
                else
                {
                    // No picture (the app couldn't be captured): its icon on a plain card.
                    Face.Background = new LinearGradientBrush(Color.FromRgb(0x5A, 0x5D, 0x66), Color.FromRgb(0x34, 0x36, 0x3C), 90);
                    if (icon != null) Face.Child = new Image { Source = icon, Width = faceH * 0.45, Height = faceH * 0.45, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                var visual = new Grid { Width = faceW + 2 * Edge, Height = faceH + 2 * Edge, Background = Brushes.Transparent };
                visual.Children.Add(Face);
                visual.Measure(new Size(visual.Width, visual.Height));
                visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));

                double sx = 1 / (1 + 2 * Pad), sy = sx / aspect;
                var mesh = new MeshGeometry3D
                {
                    Positions = new Point3DCollection { new Point3D(-sx, -sy, 0), new Point3D(sx, -sy, 0), new Point3D(sx, sy, 0), new Point3D(-sx, sy, 0) },
                    TextureCoordinates = new PointCollection { new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0) },
                    TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
                };
                var material = new DiffuseMaterial(Brushes.White);
                Viewport2DVisual3D.SetIsVisualHostMaterial(material, true);
                var plane = new Viewport2DVisual3D { Geometry = mesh, Material = material, Visual = visual, Transform = new RotateTransform3D(Rot) };

                // Camera distance so that the unrotated plane exactly fills the viewport minus its padding.
                double d = 1 / Math.Tan(Fov / 2 * Math.PI / 180);
                var vp = new Viewport3D
                {
                    IsHitTestVisible = false,
                    Camera = new PerspectiveCamera(new Point3D(0, 0, d), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), Fov) { NearPlaneDistance = 0.05, FarPlaneDistance = 50 }
                };
                vp.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
                vp.Children.Add(plane);
                Host.Children.Add(vp);
                Host.IsHitTestVisible = false;
                Host.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.4, Color = Colors.Black };
            }

            /// <summary>Corner radius as it should look on screen, for a snapshot shown widthOnScreen wide (same units).</summary>
            public void SetRadius(double radius, double widthOnScreen)
            {
                if (widthOnScreen <= 0) return;
                Face.CornerRadius = new CornerRadius(Math.Max(0, radius * FaceW / widthOnScreen));
            }
        }

        // ------------------------------------------------------------------ the strip
        private class StripWindow : Window
        {
            public const double StripW = 210, CardMaxW = 150, CardMaxH = 108, CardLeft = 26, Gap = 26;
            public const double CardRadius = 10, Angle = 24, HoverAngle = 10, BadgeSize = 38;
            private const double TextureScale = 2;   // cards are drawn at twice their size, so they stay crisp

            private readonly Canvas canvas = new Canvas();
            private readonly TranslateTransform slide = new TranslateTransform();
            private readonly Dictionary<Tucked, Card> cards = new Dictionary<Tucked, Card>();
            private double scale = 1;
            private System.Drawing.Rectangle wa;
            public bool Shown = true;

            public StripWindow()
            {
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
                canvas.RenderTransform = slide;
                Content = canvas;
                SourceInitialized += (s, e) =>
                {
                    var h = new WindowInteropHelper(this).Handle;
                    scale = HwndSource.FromHwnd(h).CompositionTarget.TransformToDevice.M11;
                    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                    // Never takes focus from the stage window, not in Alt+Tab.
                    Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                };
                new WindowInteropHelper(this).EnsureHandle();
                Place();
            }

            /// <summary>Left edge of the primary screen's work area (follows display changes).</summary>
            public void Place()
            {
                var w = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                if (w == wa) return;
                wa = w;
                Left = wa.Left / scale; Top = wa.Top / scale;
                Width = StripW; Height = wa.Height / scale;
                Layout();
            }

            /// <summary>Where the cards live, in physical pixels.</summary>
            public Rect CardsAreaPx
            {
                get { return new Rect(wa.Left, wa.Top, (CardLeft + CardMaxW + 12) * scale, wa.Height); }
            }

            private static Size CardSize(Tucked t)
            {
                double w = CardMaxW, h = w / t.Aspect;
                if (h > CardMaxH) { h = CardMaxH; w = h * t.Aspect; }
                return new Size(w, h);
            }

            /// <summary>Card rectangles (DIPs in the strip) for the windows shown, centred vertically.</summary>
            private Dictionary<Tucked, Rect> Slots()
            {
                var shown = tucked.Take(MaxShown).ToList();
                var sizes = shown.Select(CardSize).ToList();
                double total = sizes.Sum(s => s.Height) + Gap * Math.Max(0, shown.Count - 1);
                double y = Math.Max(8, (wa.Height / scale - total) / 2);
                var result = new Dictionary<Tucked, Rect>();
                for (int i = 0; i < shown.Count; i++)
                {
                    var s = sizes[i];
                    result[shown[i]] = new Rect(CardLeft + (CardMaxW - s.Width) / 2, y, s.Width, s.Height);
                    y += s.Height + Gap;
                }
                return result;
            }

            /// <summary>Bring the cards in line with the tucked list: new ones appear, gone ones fade, the rest glide.</summary>
            public void Layout()
            {
                var slots = Slots();
                foreach (var kv in cards.ToList())
                {
                    if (slots.ContainsKey(kv.Key)) continue;
                    cards.Remove(kv.Key);
                    var gone = kv.Value.Root;
                    var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
                    fade.Completed += (s, e) => canvas.Children.Remove(gone);
                    gone.IsHitTestVisible = false;
                    gone.BeginAnimation(OpacityProperty, fade);
                }
                foreach (var kv in slots)
                {
                    Card c;
                    if (!cards.TryGetValue(kv.Key, out c))
                    {
                        c = new Card(kv.Key, kv.Value.Size);
                        cards[kv.Key] = c;
                        canvas.Children.Add(c.Root);
                        c.MoveTo(kv.Value, false);
                        if (kv.Key.Pending) c.Root.Opacity = 0;
                        else
                        {
                            c.Root.Opacity = 0;
                            c.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                        }
                    }
                    else if (c.Shot != kv.Key.Shot || Math.Abs(c.Size.Width - kv.Value.Width) > 0.5 || Math.Abs(c.Size.Height - kv.Value.Height) > 0.5)
                    {
                        // Its picture arrived or changed shape (taken again on a later tuck): build it fresh.
                        canvas.Children.Remove(c.Root);
                        c = new Card(kv.Key, kv.Value.Size);
                        cards[kv.Key] = c;
                        canvas.Children.Add(c.Root);
                        c.MoveTo(kv.Value, false);
                        if (kv.Key.Pending) c.Root.Opacity = 0;
                    }
                    else c.MoveTo(kv.Value, true);
                }
            }

            /// <summary>Where a window's card is (or will be), in physical pixels, with the strip slid in.</summary>
            public Rect SlotRectPx(Tucked t)
            {
                Rect r;
                if (!Slots().TryGetValue(t, out r)) return Rect.Empty;
                return new Rect(wa.Left + r.X * scale, wa.Top + r.Y * scale, r.Width * scale, r.Height * scale);
            }

            /// <summary>The flight landed: show the card in its place.</summary>
            public void Reveal(Tucked t)
            {
                Card c;
                if (!cards.TryGetValue(t, out c)) return;
                c.Root.BeginAnimation(OpacityProperty, null);
                c.Root.Opacity = 1;
                c.Badge.Opacity = 0;
                c.Badge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            }

            /// <summary>Removes a card at once, telling where it is on screen right now (physical pixels) and its tilt.</summary>
            public bool TakeCard(Tucked t, out Rect px, out double angle)
            {
                px = Rect.Empty; angle = Angle;
                Card c;
                if (!cards.TryGetValue(t, out c)) return false;
                cards.Remove(t);
                var r = c.CurrentRect();
                angle = c.Scene.Rot.Angle;
                canvas.Children.Remove(c.Root);
                px = new Rect(wa.Left + (r.X + slide.X) * scale, wa.Top + r.Y * scale, r.Width * scale, r.Height * scale);
                return c.Root.Opacity > 0.05 && IsVisible;
            }

            public void SlideIn()
            {
                if (Shown) return;
                Shown = true;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
                });
            }

            public void SlideOut()
            {
                if (!Shown) return;
                Shown = false;
                foreach (var c in cards.Values) c.Hover(false);
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-(StripW + 20), TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                });
            }

            /// <summary>One tilted snapshot with its app icon; lifts toward you on hover, comes to the stage on click.</summary>
            private class Card
            {
                public readonly Grid Root = new Grid();
                public readonly Scene Scene;
                public readonly Image Badge;
                public readonly Size Size;
                public readonly BitmapSource Shot;
                private readonly Tucked t;
                private readonly ScaleTransform grow = new ScaleTransform(1, 1);
                private readonly TranslateTransform lift = new TranslateTransform();
                private bool pressed, hovered;

                public Card(Tucked t, Size size)
                {
                    this.t = t;
                    Size = size;
                    Shot = t.Shot;
                    double pw = size.Width * Scene.Pad, ph = size.Height * Scene.Pad;
                    Root.Width = size.Width + 2 * pw;
                    Root.Height = size.Height + 2 * ph;
                    Scene = new Scene(t.Shot, t.Icon, size.Width / size.Height, size.Width * TextureScale);
                    Scene.Rot.Angle = Angle;
                    Scene.SetRadius(CardRadius, size.Width);
                    Root.Children.Add(Scene.Host);

                    Badge = new Image
                    {
                        Source = t.Icon, Width = BadgeSize, Height = BadgeSize, IsHitTestVisible = false,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(pw - 12, 0, 0, ph - 12),
                        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.35 }
                    };
                    RenderOptions.SetBitmapScalingMode(Badge, BitmapScalingMode.HighQuality);
                    Root.Children.Add(Badge);

                    // Almost-transparent rather than transparent: fully clear pixels of a layered window let clicks through.
                    var hit = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Margin = new Thickness(pw - 6, ph - 6, pw - 6, ph - 6),
                        CornerRadius = new CornerRadius(CardRadius), Cursor = Cursors.Hand,
                        ToolTip = WindowTracker.DisplayName(t.W) + (string.IsNullOrEmpty(t.W.Title) ? "" : " — " + t.W.Title)
                    };
                    hit.MouseEnter += (s, e) => Hover(true);
                    hit.MouseLeave += (s, e) => { pressed = false; Hover(false); };
                    hit.MouseLeftButtonDown += (s, e) => { pressed = true; e.Handled = true; };
                    hit.MouseLeftButtonUp += (s, e) =>
                    {
                        if (!pressed) return;
                        pressed = false;
                        e.Handled = true;
                        var me = this.t;
                        // After this input event: Bring removes this very card.
                        Root.Dispatcher.BeginInvoke(new Action(() => Bring(me)));
                    };
                    Root.Children.Add(hit);

                    grow.CenterX = Root.Width / 2; grow.CenterY = Root.Height / 2;
                    var g = new TransformGroup();
                    g.Children.Add(grow);
                    g.Children.Add(lift);
                    Root.RenderTransform = g;
                }

                public void MoveTo(Rect slot, bool animate)
                {
                    double x = slot.X - Size.Width * Scene.Pad, y = slot.Y - Size.Height * Scene.Pad;
                    if (!animate)
                    {
                        Root.BeginAnimation(Canvas.LeftProperty, null); Root.BeginAnimation(Canvas.TopProperty, null);
                        Canvas.SetLeft(Root, x); Canvas.SetTop(Root, y);
                        return;
                    }
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    Root.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
                    Root.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
                }

                /// <summary>The snapshot's rectangle right now (DIPs in the strip), hover lift included.</summary>
                public Rect CurrentRect()
                {
                    double left = Canvas.GetLeft(Root), top = Canvas.GetTop(Root);
                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    double k = grow.ScaleX;
                    double cx = left + Root.Width / 2 + lift.X, cy = top + Root.Height / 2 + lift.Y;
                    double w = Size.Width * k, h = Size.Height * k;
                    return new Rect(cx - w / 2, cy - h / 2, w, h);
                }

                public void Hover(bool on)
                {
                    if (on == hovered) return;
                    hovered = on;
                    var d = TimeSpan.FromMilliseconds(on ? 220 : 260);
                    IEasingFunction ease = on ? (IEasingFunction)new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
                    grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(on ? 1.07 : 1, d) { EasingFunction = ease });
                    grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(on ? 1.07 : 1, d) { EasingFunction = ease });
                    lift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 10 : 0, d) { EasingFunction = ease });
                    Scene.Rot.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(on ? HoverAngle : Angle, d) { EasingFunction = ease });
                }
            }
        }

        // ------------------------------------------------------------------ flights between a window and its card
        /// <summary>
        /// A snapshot flying between a window's rectangle and its card (physical pixels), turning in 3D on the way.
        /// A click-through, never-focused, topmost overlay; its frame loop is always unhooked however it ends.
        /// </summary>
        private class Flight : Window
        {
            public const int Duration = 340;
            private const long WS_EX_TRANSPARENT = 0x20;
            public static int Active;

            private readonly Rect from, to, area;
            private readonly double a0, a1, r0, r1;
            private readonly Action shown, landed;
            private readonly bool fadeAfter;
            private readonly Canvas canvas = new Canvas();
            private readonly Scene scene;
            private double scale = 1;
            private DateTime start;
            private bool frames, closing, counted, shownDone, landedDone;
            private int ticks, afterLand = -1;

            /// <param name="shown">runs once the overlay is really on screen (e.g. hide the window under it)</param>
            /// <param name="landed">runs when the snapshot arrives</param>
            /// <param name="fadeAfter">fade out over a live window that appeared underneath, rather than vanish</param>
            public Flight(BitmapSource shot, Rect from, Rect to, double a0, double a1, double r0, double r1, Action shown, Action landed, bool fadeAfter)
            {
                this.from = from; this.to = to; this.a0 = a0; this.a1 = a1; this.r0 = r0; this.r1 = r1;
                this.shown = shown; this.landed = landed; this.fadeAfter = fadeAfter;
                var u = Rect.Union(from, to);
                double grow = Math.Max(Math.Max(from.Width, from.Height), Math.Max(to.Width, to.Height)) * Scene.Pad + 48;
                u.Inflate(grow, grow);
                area = u;
                double aspect = from.Height > 0 ? from.Width / from.Height : 1.5;
                scene = new Scene(shot, null, aspect, Math.Min(shot.PixelWidth, WindowShot.MaxWidth));
                canvas.Children.Add(scene.Host);
                Content = canvas;

                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
                IsHitTestVisible = false;
                SourceInitialized += (s, e) =>
                {
                    var h = new WindowInteropHelper(this).Handle;
                    scale = HwndSource.FromHwnd(h).CompositionTarget.TransformToDevice.M11;
                    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                    Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                    Left = area.X / scale; Top = area.Y / scale; Width = area.Width / scale; Height = area.Height / scale;
                };
            }

            public void Run()
            {
                Active++;
                counted = true;
                try
                {
                    Show();
                    Frame(0);
                    start = DateTime.Now;
                    frames = true;
                    CompositionTarget.Rendering += OnRendering;
                    Smooth.Begin();
                }
                catch (Exception ex)
                {
                    Program.Log(ex);
                    Finish();
                }
            }

            private static double Lerp(double a, double b, double k) { return a + (b - a) * k; }

            private void Frame(double ms)
            {
                double p = Math.Min(1, ms / Duration);
                const double s = 1.1;   // a soft spring: overshoots a touch, then settles
                double q = p - 1, k = q * q * ((s + 1) * q + s) + 1;
                double x = Lerp(from.X, to.X, k), y = Lerp(from.Y, to.Y, k);
                double w = Math.Max(4, Lerp(from.Width, to.Width, k)), h = Math.Max(4, Lerp(from.Height, to.Height, k));
                var host = scene.Host;
                Canvas.SetLeft(host, (x - w * Scene.Pad - area.X) / scale);
                Canvas.SetTop(host, (y - h * Scene.Pad - area.Y) / scale);
                host.Width = w * (1 + 2 * Scene.Pad) / scale;
                host.Height = h * (1 + 2 * Scene.Pad) / scale;
                scene.Rot.Angle = Lerp(a0, a1, k);
                // Radii are in DIPs on screen; the snapshot is w px wide on screen.
                scene.SetRadius(Lerp(r0, r1, Math.Min(1, k)) * scale, w);
            }

            private void OnRendering(object sender, EventArgs e)
            {
                try
                {
                    ticks++;
                    if (ticks == 2) RunShown();   // the first frame is on screen now
                    if (afterLand >= 0)
                    {
                        // Keep the finished picture a couple of frames while the real thing appears beneath it.
                        if (++afterLand >= 3) Finish();
                        return;
                    }
                    double ms = (DateTime.Now - start).TotalMilliseconds;
                    Frame(ms);
                    if (ms < Duration) return;
                    afterLand = 0;
                    RunLanded();
                }
                catch (Exception ex)
                {
                    Program.Log(ex);
                    Finish();
                }
            }

            private void StopFrames()
            {
                if (!frames) return;
                frames = false;
                CompositionTarget.Rendering -= OnRendering;
                Smooth.End();
            }

            // Each callback runs exactly once, even if the flight fails or is closed early: a window must never
            // stay un-minimized while its card waits, nor stay minimized after being brought back.
            private void RunShown()
            {
                if (shownDone) return;
                shownDone = true;
                if (shown != null) try { shown(); } catch (Exception ex) { Program.Log(ex); }
            }

            private void RunLanded()
            {
                RunShown();
                if (landedDone) return;
                landedDone = true;
                if (landed != null) try { landed(); } catch (Exception ex) { Program.Log(ex); }
            }

            private void Finish()
            {
                RunLanded();
                StopFrames();
                if (closing) return;
                closing = true;
                if (!fadeAfter) { try { Close(); } catch { } return; }
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
                a.Completed += (s, e) => { try { Close(); } catch { } };
                BeginAnimation(OpacityProperty, a);
            }

            protected override void OnClosed(EventArgs e)
            {
                RunLanded();
                StopFrames();
                if (counted) { counted = false; Active--; }
                base.OnClosed(e);
            }
        }
    }

    /// <summary>Pictures of other apps' windows (PrintWindow), with the Mac buttons painted over Windows' own.</summary>
    internal static class WindowShot
    {
        private const uint PW_RENDERFULLCONTENT = 2;
        private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
        public const int MaxWidth = 1280;   // bigger pictures are scaled down: plenty for a flight, and kinder to memory

        /// <summary>The window's visible frame, or null (too small, fullscreen, or the app wouldn't draw).</summary>
        public static BitmapSource Capture(IntPtr h, out Rect frame)
        {
            frame = Rect.Empty;
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return null;
            Native.RECT wr, fr = Native.FrameRect(h);
            if (!Native.GetWindowRect(h, out wr)) return null;
            int w = wr.Right - wr.Left, hh = wr.Bottom - wr.Top;
            if (w < 40 || hh < 40 || w > 8000 || hh > 8000) return null;

            using (var bmp = new System.Drawing.Bitmap(w, hh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok;
                    try { ok = Native.PrintWindow(h, dc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(dc); }
                    if (!ok) return null;
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
                if (cw > MaxWidth)
                {
                    double k = MaxWidth / (double)cw;
                    src = new WriteableBitmap(new TransformedBitmap(src, new ScaleTransform(k, k)));
                }
                src.Freeze();
                frame = new Rect(wr.Left + cx, wr.Top + cy, cw, ch);
                return src;
            }
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
    }
}
