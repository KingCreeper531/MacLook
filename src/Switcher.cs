using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MacLook
{
    /// <summary>
    /// Alt+Tab, Mac Look style: every open window as a live card on a rounded liquid-glass panel.
    /// Tab / Shift+Tab / arrows / mouse move a gliding highlight; the chosen card grows a little;
    /// letting go of Alt (or clicking) zooms into that window.
    /// </summary>
    internal class SwitcherWindow : Window
    {
        private class Card
        {
            public AppWindow W;
            public IntPtr Thumb;
            public Rect Box;          // DIPs: the whole cell
            public Rect Tile;         // DIPs: the rounded tile inside the cell
            public Rect ThumbRect;    // physical px inside the window, at rest
            public Border TileView;
            public DateTime Start;
            public double Sel, SelTarget;
        }

        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        private const double CellW = 252, CellH = 204, Pad = 20, Footer = 64, Radius = 26;
        private readonly Canvas canvas = new Canvas();
        private readonly Border ring;
        private readonly TranslateTransform ringPos = new TranslateTransform();
        private readonly Image footerIcon = new Image { Width = 32, Height = 32 };
        private readonly TextBlock footerApp = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.6 };
        private readonly TextBlock footerTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel footer;
        private readonly List<Card> cards = new List<Card>();
        private IntPtr hwnd;
        private double scale = 1;
        private int index;
        private bool animating;

        public bool IsOpen { get; private set; }

        public SwitcherWindow()
        {
            Title = "Window Switcher";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            FontFamily = new FontFamily(Theme.UiFont);
            SetResourceReference(ForegroundProperty, "MenuFg");
            Background = Brushes.Black;

            ring = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2.5), RenderTransform = ringPos, IsHitTestVisible = false };
            ring.SetResourceReference(Border.BorderBrushProperty, "Accent");

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(footerApp);
            text.Children.Add(footerTitle);
            footer = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(footerIcon, BitmapScalingMode.HighQuality);
            footer.Children.Add(footerIcon);
            footer.Children.Add(text);
            Content = canvas;

            // Mouse: hover picks a window, click switches to it.
            canvas.MouseMove += (s, e) =>
            {
                if (!IsOpen) return;
                var p = e.GetPosition(canvas);
                for (int i = 0; i < cards.Count; i++)
                    if (cards[i].Box.Contains(p) && i != index) { index = i; MoveRing(true); break; }
            };
            canvas.MouseLeftButtonUp += (s, e) =>
            {
                if (!IsOpen) return;
                var p = e.GetPosition(canvas);
                if (cards.Any(c => c.Box.Contains(p))) Commit();
            };

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                scale = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                // Rounded corners drawn by Windows itself (a window region would stop the live previews).
                int corner = 2;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
                int border = Theme.Dark ? 0x00505050 : 0x00D0D0D0;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, 4);
            };
        }

        // ------------------------------------------------------------------ open / move / close
        public void Open(bool backwards)
        {
            var wins = WindowTracker.Enumerate();
            if (wins.Count == 0) return;
            if (wins.Count > 18) wins = wins.Take(18).ToList();

            int n = wins.Count;
            var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double sw = screen.Width / scale, sh = screen.Height / scale;
            int maxCols = Math.Max(1, (int)((sw - 160) / CellW));
            int cols = Math.Min(n, Math.Min(6, maxCols));
            int rows = (int)Math.Ceiling(n / (double)cols);
            double w = cols * CellW + 2 * Pad, h = rows * CellH + 2 * Pad + Footer;
            Width = w; Height = h;
            Left = (sw - w) / 2; Top = (sh - h) / 2 - 20;

            // Liquid glass made from what's behind the panel.
            var snap = ScreenCapture.CaptureRegion((int)(Left * scale), (int)(Top * scale), (int)(w * scale), (int)(h * scale));
            if (snap != null)
            {
                var glass = GlassFx.Render(snap, snap.X, snap.Y, snap.W, snap.H, 0, GlassStyles.Panel);
                Background = new ImageBrush(glass) { Stretch = Stretch.Fill };
            }

            foreach (var c in cards) if (c.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(c.Thumb);
            cards.Clear();
            canvas.Children.Clear();
            Show();

            var now = DateTime.Now;
            for (int i = 0; i < n; i++)
            {
                var win = wins[i];
                int r = i / cols, col = i % cols;
                int inRow = Math.Min(cols, n - r * cols);
                double rowShift = (cols - inRow) * CellW / 2;
                var box = new Rect(Pad + rowShift + col * CellW, Pad + r * CellH, CellW, CellH);
                var tile = new Rect(box.X + 6, box.Y + 6, box.Width - 12, box.Height - 12);
                var card = new Card { W = win, Box = box, Tile = tile, Start = now.AddMilliseconds(i * 30) };

                // Soft glass tile behind each window.
                card.TileView = new Border { Width = tile.Width, Height = tile.Height, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(Theme.Dark ? (byte)26 : (byte)70, 255, 255, 255)) };
                Canvas.SetLeft(card.TileView, tile.X); Canvas.SetTop(card.TileView, tile.Y);
                canvas.Children.Add(card.TileView);

                // App icon + name under the picture.
                var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var li = new Image { Source = IconStyler.Get(win.ShellTarget, 64), Width = 18, Height = 18, Margin = new Thickness(0, 0, 6, 0) };
                RenderOptions.SetBitmapScalingMode(li, BitmapScalingMode.HighQuality);
                label.Children.Add(li);
                label.Children.Add(new TextBlock { Text = WindowTracker.DisplayName(win), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = tile.Width - 50, VerticalAlignment = VerticalAlignment.Center });
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, tile.X + (tile.Width - label.DesiredSize.Width) / 2);
                Canvas.SetTop(label, tile.Bottom - 30);
                canvas.Children.Add(label);

                var area = new Rect(tile.X + 12, tile.Y + 12, tile.Width - 24, tile.Height - 50);
                IntPtr th = IntPtr.Zero;
                if (!Native.IsIconic(win.Hwnd) && Native.DwmRegisterThumbnail(hwnd, win.Hwnd, out th) == 0)
                {
                    Native.PSIZE size;
                    Native.DwmQueryThumbnailSourceSize(th, out size);
                    double fit = Math.Min(area.Width / Math.Max(1, size.x / scale), area.Height / Math.Max(1, size.y / scale));
                    double tw = size.x / scale * fit, tht = size.y / scale * fit;
                    var tr = new Rect(area.X + (area.Width - tw) / 2, area.Y + (area.Height - tht) / 2, tw, tht);
                    card.ThumbRect = new Rect(tr.X * scale, tr.Y * scale, tr.Width * scale, tr.Height * scale);
                    card.Thumb = th;
                    Place(card);
                }
                else
                {
                    // Minimized windows show their app icon, a little faded.
                    var icon = new Image { Source = IconStyler.Get(win.ShellTarget, 128), Width = 76, Height = 76, Opacity = 0.75 };
                    RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(icon, area.X + (area.Width - 76) / 2); Canvas.SetTop(icon, area.Y + (area.Height - 76) / 2);
                    canvas.Children.Add(icon);
                }
                cards.Add(card);
            }
            canvas.Children.Add(ring);
            canvas.Children.Add(footer);

            IsOpen = true;
            index = n > 1 ? (backwards ? n - 1 : 1) : 0;
            MoveRing(false);
            StartAnim();
        }

        private void StartAnim()
        {
            if (animating) return;
            animating = true;
            CompositionTarget.Rendering += Frame;
        }

        /// <summary>Cards pop in one after another, and the chosen card springs a little larger.</summary>
        private void Frame(object sender, EventArgs e)
        {
            bool busy = false;
            foreach (var c in cards)
            {
                double d = c.SelTarget - c.Sel;
                if (Math.Abs(d) > 0.01) { c.Sel += d * 0.3; busy = true; } else c.Sel = c.SelTarget;
                if ((DateTime.Now - c.Start).TotalMilliseconds < 300) busy = true;
                if (c.Thumb != IntPtr.Zero) Place(c);
            }
            if (!busy) { CompositionTarget.Rendering -= Frame; animating = false; }
        }

        private static double Spring(double t)
        {
            const double s = 1.6;   // back-ease out: overshoots a touch, then settles
            t -= 1;
            return t * t * ((s + 1) * t + s) + 1;
        }

        private void Place(Card c)
        {
            double t = Math.Max(0, Math.Min(1, (DateTime.Now - c.Start).TotalMilliseconds / 280.0));
            double pop = t <= 0 ? 0 : Spring(t);
            double sc = (0.82 + 0.18 * pop) * (1 + 0.05 * c.Sel);
            var r = c.ThumbRect;
            double w = r.Width * sc, h = r.Height * sc;
            var p = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                opacity = (byte)(255 * Math.Max(0, Math.Min(1, t * 1.6))), fVisible = true, fSourceClientAreaOnly = false,
                rcDestination = new Native.RECT
                {
                    Left = (int)(r.X + (r.Width - w) / 2), Top = (int)(r.Y + (r.Height - h) / 2),
                    Right = (int)(r.X + (r.Width + w) / 2), Bottom = (int)(r.Y + (r.Height + h) / 2)
                }
            };
            Native.DwmUpdateThumbnailProperties(c.Thumb, ref p);
        }

        public void Next() { if (!IsOpen || cards.Count == 0) return; index = (index + 1) % cards.Count; MoveRing(true); }
        public void Prev() { if (!IsOpen || cards.Count == 0) return; index = (index - 1 + cards.Count) % cards.Count; MoveRing(true); }

        private void MoveRing(bool animate)
        {
            if (cards.Count == 0) return;
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].SelTarget = i == index ? 1 : 0;
                cards[i].TileView.Background = new SolidColorBrush(i == index
                    ? Color.FromArgb(Theme.Dark ? (byte)52 : (byte)120, 255, 255, 255)
                    : Color.FromArgb(Theme.Dark ? (byte)26 : (byte)70, 255, 255, 255));
            }
            StartAnim();

            var c = cards[index];
            ring.Width = c.Tile.Width + 4; ring.Height = c.Tile.Height + 4;
            double x = c.Tile.X - 2, y = c.Tile.Y - 2;
            var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
            if (animate)
            {
                ringPos.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                ringPos.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
            else
            {
                ringPos.BeginAnimation(TranslateTransform.XProperty, null);
                ringPos.BeginAnimation(TranslateTransform.YProperty, null);
                ringPos.X = x; ringPos.Y = y;
            }

            footerIcon.Source = IconStyler.Get(c.W.ShellTarget, 64);
            footerApp.Text = WindowTracker.DisplayName(c.W).ToUpperInvariant();
            footerTitle.Text = c.W.Title;
            footerTitle.MaxWidth = Width - 120;
            footer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(footer, (Width - Math.Min(footer.DesiredSize.Width, Width - 40)) / 2);
            Canvas.SetTop(footer, Height - Footer + 8);
            if (animate) footer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(180)));
        }

        /// <summary>Close and switch to the highlighted window, zooming from its card to full size.</summary>
        public void Commit()
        {
            if (!IsOpen) return;
            var c = cards.Count > 0 ? cards[index] : null;
            Rect from = Rect.Empty;
            if (c != null && c.Thumb != IntPtr.Zero)
                from = new Rect(Left * scale + c.ThumbRect.X, Top * scale + c.ThumbRect.Y, c.ThumbRect.Width, c.ThumbRect.Height);
            CloseSwitcher();
            if (c == null) return;
            Native.ActivateWindow(c.W.Hwnd);
            if (!from.IsEmpty && Settings.AnimateWindows) WindowAnimator.Zoom(c.W.Hwnd, from);
        }

        public void Cancel() { CloseSwitcher(); }

        private void CloseSwitcher()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (animating) { CompositionTarget.Rendering -= Frame; animating = false; }
            foreach (var c in cards) if (c.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(c.Thumb);
            cards.Clear();
            Hide();
        }
    }
}
