using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook
{
    internal class DockItem
    {
        public string Key, Name, IconSource, LaunchTarget, Args = "";
        public DockEntry Entry;
        public bool IsSeparator, IsFolder, IsTrash;
        public List<AppWindow> Windows = new List<AppWindow>();

        public FrameworkElement View;
        public Image Img;
        public Ellipse Dot;
        public TranslateTransform Shift;
        public double Size, Vel;

        public bool Running { get { return Windows.Count > 0; } }
        public bool IsBuiltin { get { return LaunchTarget != null && LaunchTarget.StartsWith("maclook:", StringComparison.OrdinalIgnoreCase); } }
    }

    internal class DockWindow : Window
    {
        private const double Pad = 5, Gap = 6, SepWidth = 15, BottomMargin = 6;
        private static readonly Brush HitBrush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        private const string TrashGuid = "::{645FF040-5081-101B-9F08-00AA002F954E}";

        private double Base { get { return Settings.IconSize; } }
        private double MaxScale { get { return Settings.Magnify ? Settings.MagnifyScale : 1.0; } }

        private IntPtr hwnd;
        private double scale = 1;
        private Grid dockGrid;
        private Border bg, shadow, label;
        private TextBlock labelText;
        private StackPanel panel;
        private Canvas overlay;

        private List<DockItem> items = new List<DockItem>();
        private Dictionary<string, DockItem> byKey = new Dictionary<string, DockItem>();
        private readonly List<string> transientOrder = new List<string>();
        private string downloads;
        private bool trashFull;

        private bool hovering, animating, menuOpen;
        private double mouseX;
        private DockItem hovered;

        // auto-hide
        private readonly TranslateTransform slide = new TranslateTransform();
        private DispatcherTimer edgeTimer;
        private bool shown = true;
        private DateTime leftAt = DateTime.MinValue;
        private Pix localSnap;

        private FileSystemWatcher watcher;
        private DispatcherTimer reloadTimer;

        public DockWindow()
        {
            Title = "Mac Look Dock";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily(Theme.UiFont);
            FontSize = 13;
            downloads = Native.GetKnownFolder("374DE290-123F-4565-9164-39C4925E467B");
            Build();
            Config.LoadDock();
            SourceInitialized += OnSourceInitialized;
            Loaded += (s, e) => Refresh();
            WatchConfig();
        }

        private void Build()
        {
            var root = new Grid();
            dockGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, BottomMargin),
                RenderTransform = slide
            };
            bg = new Border { VerticalAlignment = VerticalAlignment.Bottom, UseLayoutRounding = true };
            bg.SetResourceReference(Border.BackgroundProperty, "DockBg");
            bg.SizeChanged += (s, e) => ApplyGlass();
            IsVisibleChanged += (s, e) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(ApplyGlass), DispatcherPriority.Loaded); };
            shadow = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.35 }
            };
            shadow.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding("Height") { Source = bg });
            panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(Pad, 0, Pad, 0)
            };
            dockGrid.Children.Add(shadow);
            dockGrid.Children.Add(bg);
            dockGrid.Children.Add(panel);
            dockGrid.MouseMove += (s, e) => { mouseX = e.GetPosition(this).X; hovering = true; StartAnim(); };
            dockGrid.MouseLeave += (s, e) => { hovering = false; hovered = null; label.Visibility = Visibility.Collapsed; StartAnim(); };

            overlay = new Canvas { IsHitTestVisible = false };
            labelText = new TextBlock { FontSize = 13 };
            labelText.SetResourceReference(TextBlock.ForegroundProperty, "LabelFg");
            label = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 3, 10, 4),
                BorderThickness = new Thickness(0.5),
                Child = labelText,
                Visibility = Visibility.Collapsed
            };
            label.SetResourceReference(Border.BackgroundProperty, "LabelBg");
            label.SetResourceReference(Border.BorderBrushProperty, "LabelBorder");
            overlay.Children.Add(label);

            root.Children.Add(dockGrid);
            root.Children.Add(overlay);
            Content = root;
            ApplyShape();
        }

        private void ApplyShape()
        {
            var cr = new CornerRadius(Settings.DockRadius);
            bg.CornerRadius = cr;
            shadow.CornerRadius = cr;
            bg.Height = Base + 2 * Pad;
        }

        // ------------------------------------------------------------------ window plumbing
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            scale = src.CompositionTarget.TransformToDevice.M11;
            src.AddHook(WndProc);
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            Reposition();

            edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            edgeTimer.Tick += (s, a) => EdgeTick();
            edgeTimer.Start();
            if (Settings.DockAutoHide) { shown = false; slide.Y = HiddenOffset; }
        }

        private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == AppBar.CallbackMessage && wParam.ToInt32() == Native.ABN_POSCHANGED)
                Dispatcher.BeginInvoke(new Action(Reposition));
            return IntPtr.Zero;
        }

        public void ApplyGlass()
        {
            if (!IsVisible || !shown) return;
            var src = Settings.DockAutoHide ? (localSnap ?? Program.Desktop) : Program.Desktop;
            if (src == null) return;
            GlassFx.Apply(bg, src, GlassStyles.Dock);
        }

        public void Reposition()
        {
            if (hwnd == IntPtr.Zero) return;
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double winH = Base * Math.Max(MaxScale, 1.6) + 2 * Pad + BottomMargin + 70;
            Left = b.Left / scale;
            Width = b.Width / scale;
            Height = winH;
            Top = b.Bottom / scale - winH;
            ApplyShape();
            bool reserve = !Settings.DockAutoHide && Settings.ReserveDockSpace;
            AppBar.Set(hwnd, Native.ABE_BOTTOM, reserve ? (int)Math.Round((Base + 2 * Pad + BottomMargin + 2) * scale) : 0);
        }

        /// <summary>Re-create everything after a size / style change in Settings.</summary>
        public void Rebuild()
        {
            byKey.Clear();
            items = new List<DockItem>();
            Reposition();
            Refresh();
            if (Settings.DockAutoHide && shown && !dockGrid.IsMouseOver) HideDock();
            if (!Settings.DockAutoHide && !shown) ShowDock();
            ApplyGlass();
        }

        private List<AppWindow> lastWins = new List<AppWindow>();

        /// <summary>True if any open (not minimized) window overlaps the strip where the Dock lives.</summary>
        private bool Covered()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double band = (Base + 2 * Pad + BottomMargin + 8) * scale;
            double left, right;
            try
            {
                var tl = dockGrid.PointToScreen(new Point(0, 0));
                left = tl.X; right = tl.X + dockGrid.ActualWidth * scale;
            }
            catch { return true; }
            IntPtr fg = Native.GetForegroundWindow();
            foreach (var w in lastWins)
            {
                if (!Native.IsWindowVisible(w.Hwnd) || Native.IsIconic(w.Hwnd)) continue;
                var r = Native.FrameRect(w.Hwnd);
                if (r.Bottom > b.Bottom - band && r.Top < b.Bottom && r.Right > left && r.Left < right) return true;
            }
            return false;
        }

        /// <summary>Where (in screen pixels) the Dock icon for a window is, for the genie animation.</summary>
        public Rect IconRectFor(IntPtr hwnd)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double size = Base * scale;
            var fallback = new Rect(b.Left + b.Width / 2.0 - size / 2, b.Bottom - size - (Pad + BottomMargin) * scale, size, size);
            var it = items.FirstOrDefault(i => i.Windows.Any(w => w.Hwnd == hwnd));
            if (it == null || it.Img == null || !shown) return fallback;
            try
            {
                var p = it.Img.PointToScreen(new Point(0, 0));
                return new Rect(p.X, p.Y, it.Img.ActualWidth * scale, it.Img.ActualHeight * scale);
            }
            catch { return fallback; }
        }

        // ------------------------------------------------------------------ auto-hide
        private double HiddenOffset { get { return Base * MaxScale + 2 * Pad + BottomMargin + 40; } }

        private void EdgeTick()
        {
            if (!Settings.DockAutoHide || !IsVisible) return;
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double left, right;
            try
            {
                var tl = dockGrid.PointToScreen(new Point(0, 0));
                left = tl.X - 80 * scale;
                right = tl.X + dockGrid.ActualWidth * scale + 80 * scale;
            }
            catch { return; }

            if (Settings.DockShowWhenClear && !Covered())
            {
                // Nothing is in the way (e.g. an empty desktop): keep the Dock up like a normal Dock.
                leftAt = DateTime.MinValue;
                if (!shown) ShowDock();
                return;
            }
            if (!shown)
            {
                if (p.Y >= b.Bottom - 2 && p.X >= left && p.X <= right && p.X >= b.Left && p.X < b.Right) ShowDock();
                return;
            }
            double topPx = b.Bottom - (bg.ActualHeight + BottomMargin + 24) * scale;
            bool inside = dockGrid.IsMouseOver || menuOpen || (p.Y >= topPx && p.X >= left && p.X <= right);
            if (inside) { leftAt = DateTime.MinValue; return; }
            if (leftAt == DateTime.MinValue) leftAt = DateTime.Now;
            else if ((DateTime.Now - leftAt).TotalMilliseconds > 400) HideDock();
        }

        public void ShowDock()
        {
            if (shown) return;
            shown = true;
            leftAt = DateTime.MinValue;
            if (Settings.DockAutoHide)
            {
                // Photograph what is behind the dock right now (it's off-screen), so the glass matches.
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int h = Math.Min(b.Height, (int)((Base * 2.6 + 80) * scale));
                localSnap = ScreenCapture.CaptureRegion(b.Left, b.Bottom - h, b.Width, h);
            }
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            double from = slide.Y;
            slide.Y = 0;
            ApplyGlass();
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
            });
        }

        public void HideDock()
        {
            if (!shown) return;
            shown = false;
            hovering = false;
            hovered = null;
            label.Visibility = Visibility.Collapsed;
            StartAnim();
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(HiddenOffset, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
        }

        // ------------------------------------------------------------------ config file
        private void WatchConfig()
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); Reload(); };
                watcher = new FileSystemWatcher(Config.Dir, "dock.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler h = (s, e) => Dispatcher.BeginInvoke(new Action(() => { reloadTimer.Stop(); reloadTimer.Start(); }));
                watcher.Changed += h;
                watcher.Created += h;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public void Reload()
        {
            try { Config.LoadDock(); } catch (Exception ex) { Program.Log(ex); }
            items = new List<DockItem>();      // forces the panel to rebuild
            Refresh();
        }

        // ------------------------------------------------------------------ model
        private List<DockItem> Compute(List<AppWindow> wins)
        {
            var result = new List<DockItem>();
            var used = new HashSet<AppWindow>();

            foreach (var e in Config.Entries)
            {
                var it = new DockItem
                {
                    Key = "pin:" + e.Target.ToLowerInvariant(), Name = e.Name, Entry = e,
                    IconSource = e.Target, LaunchTarget = e.Target, Args = e.Args
                };
                if (result.Any(r => r.Key == it.Key)) continue;
                foreach (var w in wins)
                    if (!used.Contains(w) && e.Matches(w)) { it.Windows.Add(w); used.Add(w); }
                result.Add(it);
            }

            var groups = new Dictionary<string, DockItem>();
            if (Settings.ShowRunning)
            {
                foreach (var w in wins)
                {
                    if (used.Contains(w)) continue;
                    string key = "run:" + w.GroupKey;
                    DockItem it;
                    if (!groups.TryGetValue(key, out it))
                    {
                        it = new DockItem
                        {
                            Key = key, Name = WindowTracker.DisplayName(w),
                            IconSource = w.ShellTarget, LaunchTarget = w.ShellTarget
                        };
                        groups[key] = it;
                    }
                    it.Windows.Add(w);
                }
            }
            transientOrder.RemoveAll(k => !groups.ContainsKey(k));
            foreach (var k in groups.Keys) if (!transientOrder.Contains(k)) transientOrder.Add(k);
            if (transientOrder.Count > 0)
            {
                result.Add(new DockItem { Key = "sep:running", IsSeparator = true });
                foreach (var k in transientOrder) result.Add(groups[k]);
            }

            result.Add(new DockItem { Key = "sep:folders", IsSeparator = true });
            if (downloads != null && Directory.Exists(downloads))
                result.Add(new DockItem { Key = "folder:downloads", Name = "Downloads", IconSource = downloads, LaunchTarget = downloads, IsFolder = true });
            result.Add(new DockItem { Key = "trash", Name = "Recycle Bin", IconSource = TrashGuid, LaunchTarget = "shell:RecycleBinFolder", IsFolder = true, IsTrash = true });
            return result;
        }

        public void Refresh()
        {
            List<AppWindow> wins;
            try { wins = WindowTracker.Enumerate(); }
            catch (Exception ex) { Program.Log(ex); return; }
            lastWins = wins;
            try { WindowGlass.Apply(wins); } catch (Exception ex) { Program.Log(ex); }

            var fresh = Compute(wins);
            bool sameLayout = fresh.Count == items.Count && fresh.Select(i => i.Key).SequenceEqual(items.Select(i => i.Key));

            var merged = new List<DockItem>();
            foreach (var n in fresh)
            {
                DockItem existing;
                if (byKey.TryGetValue(n.Key, out existing))
                {
                    existing.Windows = n.Windows;
                    existing.Name = n.Name;
                    existing.Entry = n.Entry;
                    existing.Args = n.Args;
                    merged.Add(existing);
                }
                else
                {
                    MakeView(n);
                    merged.Add(n);
                }
            }
            byKey = merged.ToDictionary(i => i.Key);
            items = merged;

            if (!sameLayout)
            {
                panel.Children.Clear();
                foreach (var it in items) panel.Children.Add(it.View);
            }
            foreach (var it in items)
                if (it.Dot != null) it.Dot.Visibility = it.Running && Settings.ShowIndicators ? Visibility.Visible : Visibility.Hidden;

            UpdateTrash();
        }

        private void UpdateTrash()
        {
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
            bool full = Native.SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
            if (full == trashFull) return;
            trashFull = full;
            DockItem t;
            if (byKey.TryGetValue("trash", out t) && t.Img != null)
                { var raw = IconLoader.Get(TrashGuid, 256, true); t.Img.Source = raw == null ? Placeholder("Recycle Bin") : (Settings.IconStyle == "glass" ? IconStyler.Plate(raw) : raw); }
        }

        // ------------------------------------------------------------------ views
        private void MakeView(DockItem it)
        {
            if (it.IsSeparator)
            {
                var line = new Border
                {
                    Width = 1, Height = Base * 0.72, Opacity = 0.35,
                    Margin = new Thickness((SepWidth - 1) / 2, 0, (SepWidth - 1) / 2, Pad + Base * 0.14),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                line.SetResourceReference(Border.BackgroundProperty, "Dot");
                it.View = line;
                return;
            }

            var g = new Grid { Background = HitBrush, VerticalAlignment = VerticalAlignment.Bottom };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Pad) });

            var img = new Image
            {
                Width = Base, Height = Base,
                Margin = new Thickness(Gap / 2, Pad, Gap / 2, 0),
                Source = IconStyler.Get(it.IsBuiltin ? it.LaunchTarget : it.IconSource) ?? Placeholder(it.Name),
                RenderTransformOrigin = new Point(0.5, 1)
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            it.Shift = new TranslateTransform();
            img.RenderTransform = it.Shift;

            var dot = new Ellipse
            {
                Width = 4, Height = 4, Visibility = Visibility.Hidden,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            dot.SetResourceReference(Shape.FillProperty, "Dot");
            Grid.SetRow(dot, 1);
            g.Children.Add(img);
            g.Children.Add(dot);

            g.MouseEnter += (s, e) => ShowLabel(it);
            g.MouseLeftButtonDown += (s, e) => { img.Opacity = 0.65; e.Handled = true; };
            g.MouseLeftButtonUp += (s, e) => { img.Opacity = 1; e.Handled = true; Click(it); };
            g.MouseLeave += (s, e) => img.Opacity = 1;
            g.MouseRightButtonUp += (s, e) => { e.Handled = true; ShowMenu(it); };

            it.Img = img;
            it.Dot = dot;
            it.View = g;
            it.Size = Base;
            if (it.IsTrash) trashFull = !trashFull; // force icon state check on next UpdateTrash
        }

        private static BitmapSource Placeholder(string name)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var br = new LinearGradientBrush(Color.FromRgb(0x9A, 0x9A, 0xA0), Color.FromRgb(0x5E, 0x5E, 0x64), 90);
                dc.DrawRoundedRectangle(br, null, new Rect(10, 10, 108, 108), 26, 26);
                string letter = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
#pragma warning disable 618
                var ft = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 60, Brushes.White);
#pragma warning restore 618
                dc.DrawText(ft, new Point(64 - ft.Width / 2, 64 - ft.Height / 2));
            }
            var rtb = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private void ShowLabel(DockItem it)
        {
            hovered = it;
            labelText.Text = it.Name;
            label.Visibility = Visibility.Visible;
            PositionLabel();
        }

        private void PositionLabel()
        {
            if (hovered == null || hovered.Img == null || label.Visibility != Visibility.Visible) return;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var p = hovered.Img.TranslatePoint(new Point(hovered.Size / 2, 0), overlay);
            double top = p.Y;
            if (Settings.MagnifyInside)
            {
                var bp = bg.TranslatePoint(new Point(0, 0), overlay);
                top = Math.Min(top, bp.Y);
            }
            Canvas.SetLeft(label, p.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, top - label.DesiredSize.Height - 10);
        }

        // ------------------------------------------------------------------ magnification
        private void StartAnim()
        {
            if (animating) return;
            animating = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            double total = 0;
            foreach (var it in items) total += it.IsSeparator ? SepWidth : Base + Gap;
            double u = mouseX - (ActualWidth / 2 - total / 2);
            double radius = Base * 3;
            double x = 0, biggest = Base;
            bool moving = false;

            foreach (var it in items)
            {
                double w = it.IsSeparator ? SepWidth : Base + Gap;
                if (!it.IsSeparator && it.Img != null)
                {
                    double target = Base;
                    if (hovering && Settings.Magnify)
                    {
                        double d = Math.Abs(u - (x + w / 2));
                        if (d < radius) target = Base + Base * (MaxScale - 1) * (Math.Cos(Math.PI * d / radius) + 1) / 2;
                    }
                    // Spring toward the target size (mass-spring-damper, like a physics-based dock).
                    double diff = target - it.Size;
                    it.Vel = it.Vel * 0.6 + diff * 0.22;
                    it.Size += it.Vel;
                    if (Math.Abs(diff) < 0.15 && Math.Abs(it.Vel) < 0.08) { it.Size = target; it.Vel = 0; }
                    else moving = true;
                    if (it.Size < Base * 0.9) it.Size = Base * 0.9;
                    if (it.Img.Width != it.Size) { it.Img.Width = it.Size; it.Img.Height = it.Size; }
                    if (it.Size > biggest) biggest = it.Size;
                }
                x += w;
            }

            // Keep every icon inside the glass: the dock grows taller with the biggest icon.
            double bgH = (Settings.MagnifyInside ? biggest : Base) + 2 * Pad;
            if (Math.Abs(bg.Height - bgH) > 0.5) bg.Height = Math.Round(bgH);

            dockGrid.UpdateLayout();
            PositionLabel();
            if (!moving)
            {
                CompositionTarget.Rendering -= OnFrame;
                animating = false;
            }
        }

        // ------------------------------------------------------------------ actions
        private void Click(DockItem it)
        {
            if (it.Running)
            {
                var w = it.Windows.FirstOrDefault(x => !Native.IsIconic(x.Hwnd)) ?? it.Windows[0];
                Native.ActivateWindow(w.Hwnd);
                return;
            }
            if (string.IsNullOrEmpty(it.LaunchTarget)) return;
            if (!it.IsFolder && !it.IsBuiltin && Settings.BounceOnLaunch) Bounce(it);
            Launcher.Open(it.LaunchTarget, it.Args);
        }

        private void Bounce(DockItem it)
        {
            var anim = new DoubleAnimation(0, -Base * 0.42, TimeSpan.FromMilliseconds(260))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            it.Shift.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private static MenuItem MI(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            if (act != null) mi.Click += (s, e) => act();
            else mi.IsEnabled = false;
            return mi;
        }

        private void ShowMenu(DockItem it)
        {
            if (it.IsSeparator) return;
            var m = new ContextMenu();
            if (it.IsTrash)
            {
                m.Items.Add(MI("Open", () => Launcher.Open(it.LaunchTarget)));
                m.Items.Add(new Separator());
                m.Items.Add(MI("Empty Recycle Bin", trashFull ? new Action(() => Native.SHEmptyRecycleBin(hwnd, null, 0)) : null));
            }
            else if (it.IsFolder)
            {
                m.Items.Add(MI("Open", () => Launcher.Open(it.LaunchTarget)));
            }
            else
            {
                IntPtr fg = Native.GetForegroundWindow();
                foreach (var w in it.Windows)
                {
                    IntPtr h = w.Hwnd;
                    string t = w.Title.Length > 60 ? w.Title.Substring(0, 59) + "…" : w.Title;
                    var wi = MI(t, () => Native.ActivateWindow(h));
                    wi.IsChecked = h == fg;
                    m.Items.Add(wi);
                }
                if (it.Windows.Count > 0) m.Items.Add(new Separator());

                var keep = MI("Keep in Dock", string.IsNullOrEmpty(it.LaunchTarget) ? null : new Action(() => ToggleKeep(it)));
                keep.IsChecked = it.Entry != null;
                m.Items.Add(keep);
                if (!string.IsNullOrEmpty(it.LaunchTarget) && File.Exists(it.LaunchTarget))
                {
                    string path = it.LaunchTarget;
                    m.Items.Add(MI("Show in File Explorer", () => Launcher.Open("explorer.exe", "/select,\"" + path + "\"")));
                }
                m.Items.Add(new Separator());
                if (it.Running)
                {
                    if (!string.IsNullOrEmpty(it.LaunchTarget))
                        m.Items.Add(MI("New Window", () => { if (Settings.BounceOnLaunch) Bounce(it); Launcher.Open(it.LaunchTarget, it.Args); }));
                    m.Items.Add(MI("Quit", () =>
                    {
                        foreach (var w in it.Windows) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }));
                }
                else
                {
                    m.Items.Add(MI("Open", () => Click(it)));
                }
            }
            m.Items.Add(new Separator());
            if (!it.IsSeparator && !it.IsBuiltin && !string.IsNullOrEmpty(it.IconSource))
            {
                string key = it.IconSource;
                m.Items.Add(MI("Change Icon…", () => IconStyler.PickFor(key)));
                if (IconStyler.HasCustom(key)) m.Items.Add(MI("Reset Icon", () => { IconStyler.ResetCustom(key); Program.IconsChanged(); }));
            }
            m.Items.Add(MI("Dock Settings…", () => Program.SettingsWin.Open("Dock")));
            m.PlacementTarget = it.Img;
            m.Placement = PlacementMode.Top;
            m.HorizontalOffset = -12;
            menuOpen = true;
            m.Closed += (s, e) => menuOpen = false;
            GlassFx.PrepareMenu(m);
            m.IsOpen = true;
        }

        private void ToggleKeep(DockItem it)
        {
            if (it.Entry != null)
            {
                Config.Entries.Remove(it.Entry);
            }
            else
            {
                var e = new DockEntry { Name = it.Name, Target = it.LaunchTarget };
                var w = it.Windows.FirstOrDefault();
                if (w != null && !string.IsNullOrEmpty(w.ProcName)) e.Match.Add(w.ProcName.ToLowerInvariant());
                Config.Entries.Add(e);
            }
            try { Config.SaveDock(); } catch (Exception ex) { Program.Log(ex); }
            Reload();
        }
    }
}
