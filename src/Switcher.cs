using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Alt+Tab, Mac Look style: every open window as a live card on a liquid-glass panel.
    /// Tab / Shift+Tab / arrows move a gliding highlight; letting go of Alt zooms into the chosen window.
    /// </summary>
    internal class SwitcherWindow : Window
    {
        private class Card
        {
            public AppWindow W;
            public IntPtr Thumb;
            public Rect Box;          // DIPs, inside the window
            public Rect ThumbRect;    // physical px, inside the window
            public DateTime Start;
        }

        private const double CellW = 236, CellH = 176, Pad = 22, LabelH = 22, Footer = 62;
        private readonly Canvas canvas = new Canvas();
        private readonly Border ring;
        private readonly TranslateTransform ringPos = new TranslateTransform();
        private readonly Image footerIcon = new Image { Width = 30, Height = 30 };
        private readonly TextBlock footerApp = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.65 };
        private readonly TextBlock footerTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel footer;
        private readonly List<Card> cards = new List<Card>();
        private IntPtr hwnd;
        private double scale = 1;
        private int index;
        private bool popping;

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

            ring = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(3), RenderTransform = ringPos, IsHitTestVisible = false };
            ring.SetResourceReference(Border.BorderBrushProperty, "Accent");
            ring.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            canvas.Children.Add(ring);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(footerApp);
            text.Children.Add(footerTitle);
            footer = new StackPanel { Orientation = Orientation.Horizontal };
            footer.Children.Add(footerIcon);
            footer.Children.Add(text);
            canvas.Children.Add(footer);
            Content = canvas;

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                scale = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                int corner = 2;   // let Windows round our corners
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
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

            // Liquid glass from what's behind the panel.
            var snap = ScreenCapture.CaptureRegion((int)(Left * scale), (int)(Top * scale), (int)(w * scale), (int)(h * scale));
            if (snap != null)
            {
                var glass = GlassFx.Render(snap, snap.X, snap.Y, snap.W, snap.H, 0, GlassStyles.Panel);
                Background = new ImageBrush(glass) { Stretch = Stretch.Fill };
            }
            Show();

            foreach (var c in cards) if (c.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(c.Thumb);
            cards.Clear();
            canvas.Children.Clear();
            canvas.Children.Add(ring);
            canvas.Children.Add(footer);
            var now = DateTime.Now;
            for (int i = 0; i < n; i++)
            {
                var win = wins[i];
                int r = i / cols, col = i % cols;
                int inRow = Math.Min(cols, n - r * cols);
                double rowShift = (cols - inRow) * CellW / 2;
                var box = new Rect(Pad + rowShift + col * CellW, Pad + r * CellH, CellW, CellH);
                var card = new Card { W = win, Box = box, Start = now.AddMilliseconds(i * 28) };

                // App name over each card.
                var name = new TextBlock { Text = WindowTracker.DisplayName(win), FontSize = 11.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis, Width = CellW - 28, TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(name, box.X + 14); Canvas.SetTop(name, box.Y + 8);
                canvas.Children.Add(name);

                var area = new Rect(box.X + 14, box.Y + LabelH + 8, CellW - 28, CellH - LabelH - 22);
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
                    Place(card, 0);
                }
                else
                {
                    // Minimized windows show their app icon instead of a live picture.
                    var icon = new Image { Source = IconStyler.Get(win.ShellTarget, 128), Width = 72, Height = 72, Opacity = 0.9 };
                    Canvas.SetLeft(icon, area.X + (area.Width - 72) / 2); Canvas.SetTop(icon, area.Y + (area.Height - 72) / 2);
                    canvas.Children.Add(icon);
                }
                cards.Add(card);
            }

            IsOpen = true;
            index = n > 1 ? (backwards ? n - 1 : 1) : 0;
            MoveRing(false);
            if (!popping) { popping = true; CompositionTarget.Rendering += Pop; }
        }

        /// <summary>Cards pop in one after another with a little spring.</summary>
        private void Pop(object sender, EventArgs e)
        {
            bool busy = false;
            foreach (var c in cards)
            {
                if (c.Thumb == IntPtr.Zero) continue;
                double t = (DateTime.Now - c.Start).TotalMilliseconds / 260.0;
                if (t < 1) busy = true;
                Place(c, Math.Max(0, Math.Min(1, t)));
            }
            if (!busy) { CompositionTarget.Rendering -= Pop; popping = false; }
        }

        private static double Spring(double t)
        {
            // Overshoots a touch, then settles (back-ease out).
            const double s = 1.6;
            t -= 1;
            return t * t * ((s + 1) * t + s) + 1;
        }

        private void Place(Card c, double t)
        {
            double k = t <= 0 ? 0 : Spring(t);
            double sc = 0.82 + 0.18 * k;
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
            var c = cards[index];
            ring.Width = c.Box.Width - 8; ring.Height = c.Box.Height - 8;
            double x = c.Box.X + 4, y = c.Box.Y + 4;
            var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
            if (animate)
            {
                ringPos.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                ringPos.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
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
            Canvas.SetTop(footer, Height - Footer + 10);
        }

        /// <summary>Close and switch to the highlighted window, zooming from its card to full size.</summary>
        public void Commit()
        {
            if (!IsOpen) return;
            var c = cards.Count > 0 ? cards[index] : null;
            Rect from = Rect.Empty;
            if (c != null && c.Thumb != IntPtr.Zero)
                from = new Rect(Left * scale + c.ThumbRect.X, Top * scale + c.ThumbRect.Y, c.ThumbRect.Width, c.ThumbRect.Height);
            Close(false);
            if (c == null) return;
            Native.ActivateWindow(c.W.Hwnd);
            if (!from.IsEmpty && Settings.AnimateWindows) WindowAnimator.Zoom(c.W.Hwnd, from);
        }

        public void Cancel() { Close(false); }

        private void Close(bool unused)
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (popping) { CompositionTarget.Rendering -= Pop; popping = false; }
            foreach (var c in cards) if (c.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(c.Thumb);
            cards.Clear();
            Hide();
        }
    }
}
