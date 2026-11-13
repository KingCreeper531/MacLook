using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacLook
{
    /// <summary>Every open window as a live thumbnail, flying out from where it really is.</summary>
    internal class MissionControlWindow : Window
    {
        private class Thumb
        {
            public AppWindow W;
            public IntPtr Handle;
            public Rect From, To;          // physical pixels
            public Border Hit;
            public Border Caption;
        }

        private readonly Canvas canvas = new Canvas { Background = Brushes.Transparent };
        private readonly List<Thumb> thumbs = new List<Thumb>();
        private IntPtr hwnd;
        private double scale = 1;
        private DateTime animStart;
        private bool animating, reversing;
        private Action afterDismiss;
        private DateTime lastHidden;

        public MissionControlWindow()
        {
            Title = "Mission Control";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            FontFamily = new FontFamily(Theme.UiFont);
            Content = canvas;
            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                scale = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;
            };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
            canvas.MouseLeftButtonUp += (s, e) => { if (e.OriginalSource == canvas) Dismiss(); };
            Deactivated += (s, e) => Dismiss();
        }

        public void Toggle()
        {
            if (IsVisible) { Dismiss(); return; }
            if ((DateTime.Now - lastHidden).TotalMilliseconds < 300) return;
            Open();
        }

        private void Open()
        {
            var wins = WindowTracker.Enumerate().Where(w => !Native.IsIconic(w.Hwnd)).ToList();
            var snap = ScreenCapture.Capture();
            var backdrop = GlassFx.Backdrop(snap, 6, 7, 0.40);
            Background = backdrop != null ? (Brush)new ImageBrush(backdrop) { Stretch = Stretch.Fill } : new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Show();
            Activate();
            Native.SetForegroundWindow(hwnd);
            Layout(wins);
        }

        private void Layout(List<AppWindow> wins)
        {
            canvas.Children.Clear();
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            if (wins.Count == 0)
            {
                var t = new TextBlock { Text = "No open windows", Foreground = Brushes.White, FontSize = 22, Opacity = 0.75 };
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(t, (b.Width / scale - t.DesiredSize.Width) / 2);
                Canvas.SetTop(t, b.Height / scale / 2 - 20);
                canvas.Children.Add(t);
                return;
            }

            double x0 = 70 * scale, y0 = 80 * scale, aw = b.Width - 140 * scale, ah = b.Height - 190 * scale;
            int n = wins.Count;
            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * (aw / ah) / 1.5)));
            int rows = (int)Math.Ceiling(n / (double)cols);
            double cw = aw / cols, ch = ah / rows;

            for (int i = 0; i < n; i++)
            {
                var w = wins[i];
                IntPtr th;
                if (Native.DwmRegisterThumbnail(hwnd, w.Hwnd, out th) != 0) continue;
                Native.PSIZE size;
                Native.DwmQueryThumbnailSourceSize(th, out size);
                double sw = Math.Max(1, size.x), sh = Math.Max(1, size.y);
                double fit = Math.Min(1, Math.Min((cw - 50 * scale) / sw, (ch - 70 * scale) / sh));
                double tw = sw * fit, tht = sh * fit;
                int c = i % cols, r = i / cols;
                int inRow = Math.Min(cols, n - r * cols);
                double rowOffset = (cols - inRow) * cw / 2;      // centre a short last row
                double cx = x0 + rowOffset + c * cw + cw / 2, cy = y0 + r * ch + (ch - 30 * scale) / 2;
                var to = new Rect(cx - tw / 2, cy - tht / 2, tw, tht);

                Native.RECT wr;
                Native.GetWindowRect(w.Hwnd, out wr);
                var from = new Rect(wr.Left - b.Left, wr.Top - b.Top, Math.Max(1, wr.Right - wr.Left), Math.Max(1, wr.Bottom - wr.Top));

                var thumb = new Thumb { W = w, Handle = th, From = from, To = to };
                BuildOverlay(thumb);
                thumbs.Add(thumb);
                Place(thumb, from);
            }
            animStart = DateTime.Now;
            if (!animating) { animating = true; CompositionTarget.Rendering += OnFrame; Smooth.Begin(); }
        }

        private void BuildOverlay(Thumb t)
        {
            var hit = new Border
            {
                CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Opacity = 0
            };
            var caption = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4), Opacity = 0,
                Background = new SolidColorBrush(Color.FromArgb(150, 30, 30, 34)),
                Child = new TextBlock { Text = WindowTracker.DisplayName(t.W), Foreground = Brushes.White, FontSize = 13 }
            };
            var to = t.To;
            Canvas.SetLeft(hit, to.X / scale - 6); Canvas.SetTop(hit, to.Y / scale - 6);
            hit.Width = to.Width / scale + 12; hit.Height = to.Height / scale + 12;
            caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(caption, (to.X + to.Width / 2) / scale - caption.DesiredSize.Width / 2);
            Canvas.SetTop(caption, (to.Y + to.Height) / scale + 12);

            hit.MouseEnter += (s, e) => { hit.SetResourceReference(Border.BorderBrushProperty, "Accent"); caption.Opacity = 1; };
            hit.MouseLeave += (s, e) => { hit.BorderBrush = Brushes.Transparent; caption.Opacity = 0; };
            IntPtr target = t.W.Hwnd;
            hit.MouseLeftButtonUp += (s, e) => { e.Handled = true; Dismiss(() => Native.ActivateWindow(target)); };
            t.Hit = hit;
            t.Caption = caption;
            canvas.Children.Add(hit);
            canvas.Children.Add(caption);
        }

        private static void Place(Thumb t, Rect r)
        {
            var p = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                opacity = 255, fVisible = true, fSourceClientAreaOnly = false,
                rcDestination = new Native.RECT { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom }
            };
            Native.DwmUpdateThumbnailProperties(t.Handle, ref p);
        }

        private void OnFrame(object sender, EventArgs e)
        {
            double t = Math.Min(1, (DateTime.Now - animStart).TotalMilliseconds / (reversing ? 240.0 : 300.0));
            double k = reversing ? 1 - t * t * t : 1 - Math.Pow(1 - t, 3);   // ease-in going back, ease-out coming out
            foreach (var th in thumbs)
            {
                var r = new Rect(
                    th.From.X + (th.To.X - th.From.X) * k, th.From.Y + (th.To.Y - th.From.Y) * k,
                    th.From.Width + (th.To.Width - th.From.Width) * k, th.From.Height + (th.To.Height - th.From.Height) * k);
                Place(th, r);
                th.Hit.Opacity = k;
            }
            if (t >= 1)
            {
                CompositionTarget.Rendering -= OnFrame; Smooth.End();
                animating = false;
                if (reversing) Finish();
            }
        }

        /// <summary>Fly every thumbnail back to where its window really is, then close.</summary>
        private void Dismiss(Action after = null)
        {
            if (!IsVisible || reversing) return;
            lastHidden = DateTime.Now;
            afterDismiss = after;
            if (thumbs.Count == 0) { Finish(); return; }
            foreach (var t in thumbs) { t.Caption.Opacity = 0; t.Hit.BorderBrush = Brushes.Transparent; }
            reversing = true;
            animStart = DateTime.Now;
            if (!animating) { animating = true; CompositionTarget.Rendering += OnFrame; Smooth.Begin(); }
        }

        private void Finish()
        {
            reversing = false;
            lastHidden = DateTime.Now;
            if (animating) { CompositionTarget.Rendering -= OnFrame; Smooth.End(); animating = false; }
            foreach (var t in thumbs) Native.DwmUnregisterThumbnail(t.Handle);
            thumbs.Clear();
            canvas.Children.Clear();
            Hide();
            Background = Brushes.Black;
            var a = afterDismiss;
            afterDismiss = null;
            if (a != null) a();
        }
    }
}
