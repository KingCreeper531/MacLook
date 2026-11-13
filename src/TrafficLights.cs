using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace MacLook
{
    /// <summary>
    /// Mac-style close / minimize / zoom buttons that ride on the active window's title bar.
    /// It's a separate little overlay (no code goes into other apps); clicks send the window its own
    /// close / minimize / maximize command, so apps still ask to save your work.
    /// </summary>
    internal class TrafficLightsWindow : Window
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_DESTROY = 0x8001;
        private const int SC_CLOSE = 0xF060, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;

        private IntPtr hwnd, target;
        private double scale = 1;
        private Native.WinEventDelegate proc;
        private IntPtr fgHook, moveHook;
        private readonly TextBlock[] symbols = new TextBlock[3];
        private readonly StackPanel row;
        private readonly Border patch;

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;

        public TrafficLightsWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Light(0, "#FF5F57", "#E0443E", "×", () => Command(SC_CLOSE)));
            row.Children.Add(Light(1, "#FEBC2E", "#DEA123", "−", () => Command(SC_MINIMIZE)));
            row.Children.Add(Light(2, "#28C840", "#1AAB29", "+", () => Command(Native.IsZoomed(target) ? SC_RESTORE : SC_MAXIMIZE)));
            row.MouseEnter += (s, e) => { foreach (var t in symbols) t.Opacity = 1; };
            row.MouseLeave += (s, e) => { foreach (var t in symbols) t.Opacity = 0; };
            this.row = row;
            patch = new Border { Child = row };
            Content = patch;

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                scale = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            };
        }

        private UIElement Light(int i, string fill, string rim, string symbol, Action click)
        {
            var g = new Grid { Width = 12, Height = 12, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            g.Children.Add(new Ellipse
            {
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(rim)), StrokeThickness = 0.6
            });
            var t = new TextBlock
            {
                Text = symbol, FontSize = i == 2 ? 10.5 : 11, FontWeight = FontWeights.Bold, Opacity = 0, Margin = new Thickness(0, -2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(150, 60, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            symbols[i] = t;
            g.Children.Add(t);
            g.MouseLeftButtonDown += (s, e) => e.Handled = true;
            g.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return g;
        }

        private void Command(int sc)
        {
            if (target == IntPtr.Zero) return;
            Native.PostMessage(target, 0x0112, new IntPtr(sc), IntPtr.Zero);   // WM_SYSCOMMAND
            // Minimize: the window leaves at once. Close: stay put until the window is really gone
            // (it may ask to save first), so Windows' own buttons never peek out.
            if (sc == SC_MINIMIZE) Hide();
        }

        private System.Windows.Threading.DispatcherTimer watch;

        public void Start()
        {
            watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            watch.Tick += (s, e) => Watch();
            watch.Start();
            proc = OnEvent;
            fgHook = Native.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, proc, 0, 0, 2);
            Follow(Native.GetForegroundWindow());
        }

        public void Stop()
        {
            if (watch != null) watch.Stop();
            if (fgHook != IntPtr.Zero) Native.UnhookWinEvent(fgHook);
            if (moveHook != IntPtr.Zero) Native.UnhookWinEvent(moveHook);
            fgHook = moveHook = IntPtr.Zero;
            Hide();
        }

        /// <summary>Re-read the setting (Left / Right / Off).</summary>
        public void Refresh() { Follow(Native.GetForegroundWindow()); }

        private void OnEvent(IntPtr hook, uint ev, IntPtr h, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0) return;
            if (ev == EVENT_SYSTEM_FOREGROUND) Follow(h);
            else if (ev == EVENT_SYSTEM_MINIMIZESTART && h == target) Hide();
            else if (ev == EVENT_OBJECT_LOCATIONCHANGE && h == target) Position();
            else if (ev == EVENT_OBJECT_DESTROY && h == target) { target = IntPtr.Zero; Hide(); }
            else if (ev == 0x8003 && h == target) { target = IntPtr.Zero; Hide(); }   // EVENT_OBJECT_HIDE: closed
        }

        private void Follow(IntPtr h)
        {
            if (h == hwnd || (h != IntPtr.Zero && WindowTracker.IsOwn(h) && target != IntPtr.Zero)) return;
            if (moveHook != IntPtr.Zero) { Native.UnhookWinEvent(moveHook); moveHook = IntPtr.Zero; }
            target = IntPtr.Zero;
            if (Settings.WindowButtons == "off" || h == IntPtr.Zero || Program.IsFullscreen || !Eligible(h)) { Hide(); return; }
            target = h;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            // Follow this window as it moves (only its own process's events, to stay light).
            moveHook = Native.SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, proc, pid, 0, 2);
            Position();
        }

        private static bool Eligible(IntPtr h)
        {
            if (WindowTracker.IsOwn(h) || WindowTracker.IsShellWindow(h) || Native.IsIconic(h) || !Native.IsWindowVisible(h)) return false;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            long style = Native.GetWindowLongPtr(h, -16).ToInt64();
            const long WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000;
            // Only real app windows that have a close button of their own.
            if ((style & WS_SYSMENU) == 0 && (style & WS_CAPTION) != WS_CAPTION) return false;
            return Native.GetWindowTextLength(h) > 0;
        }

        private void Position()
        {
            if (target == IntPtr.Zero) return;
            if (Native.IsIconic(target) || !Native.IsWindowVisible(target)) { Hide(); return; }
            var r = Native.FrameRect(target);
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 80) { Hide(); return; }

            if (Settings.WindowButtons == "left")
            {
                patch.Background = Brushes.Transparent;
                row.HorizontalAlignment = HorizontalAlignment.Left;
                row.Margin = new Thickness(2);
                Width = 70; Height = 18;
                Left = (r.Left + 10 * scale) / scale;
                Top = (r.Top + (Native.IsZoomed(target) ? 8 : 9) * scale) / scale;
                if (!IsVisible) Show();
                return;
            }

            // "Replace": find exactly where Windows draws minimize / maximize / close for this window.
            Native.RECT wr, cb;
            Native.GetWindowRect(target, out wr);
            double bx, by, bw, bh;
            if (Native.DwmGetWindowAttribute(target, DWMWA_CAPTION_BUTTON_BOUNDS, out cb, 16) == 0 && cb.Right - cb.Left > 20 && cb.Bottom - cb.Top > 10)
            {
                bx = wr.Left + cb.Left; by = wr.Top + cb.Top; bw = cb.Right - cb.Left; bh = cb.Bottom - cb.Top;
                if (bx + bw > r.Right) bw = r.Right - bx;          // keep inside the visible frame
                if (by < r.Top) { bh -= r.Top - by; by = r.Top; }
            }
            else
            {
                bw = 138 * scale; bh = 32 * scale;                 // Windows 11 default: three 46 px buttons
                bx = r.Right - bw; by = r.Top;
            }

            // Paint the patch in the title bar's own colour so Windows' buttons disappear under it.
            patch.Background = new SolidColorBrush(SampleTitleBar((int)(bx - 6 * scale), (int)(by + bh / 2)));
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, 0, 14, 0);
            Width = Math.Max(76, bw / scale);
            Height = Math.Max(20, bh / scale);
            Left = bx / scale;
            Top = by / scale;
            if (!IsVisible) Show();
        }

        /// <summary>
        /// The title bar's colour: nine samples to the left of the buttons, and the most common one wins
        /// (so an icon or a hover highlight next to the buttons can't throw it off).
        /// </summary>
        private Color SampleTitleBar(int x, int y)
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                var counts = new System.Collections.Generic.Dictionary<uint, int>();
                int[] dx = { 4, 16, 34 };
                int[] dy = { -8, 0, 8 };
                foreach (int ox in dx)
                    foreach (int oy in dy)
                    {
                        uint c = GetPixel(dc, x - (int)(ox * scale), y + (int)(oy * scale)) & 0xFFFFFF;
                        if (c == 0xFFFFFF && GetPixel(dc, x, y) == 0xFFFFFFFF) continue;
                        int n;
                        counts.TryGetValue(c, out n);
                        counts[c] = n + 1;
                    }
                if (counts.Count == 0) return Color.FromRgb(32, 32, 32);
                uint best = 0; int bestN = -1;
                foreach (var kv in counts) if (kv.Value > bestN) { best = kv.Key; bestN = kv.Value; }
                return Color.FromRgb((byte)(best & 0xFF), (byte)((best >> 8) & 0xFF), (byte)((best >> 16) & 0xFF));
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        /// <summary>
        /// Safety net: some windows (new windows, the screenshot tool, menus) don't send the events we
        /// listen for, so every quarter second make sure we're on the right window and still on top.
        /// </summary>
        private void Watch()
        {
            if (Settings.WindowButtons == "off") { if (IsVisible) Hide(); return; }
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != hwnd && fg != target && !WindowTracker.IsOwn(fg)) { Follow(fg); return; }
            if (target != IntPtr.Zero) Position();
            if (IsVisible) Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // stay topmost, no move/size/activate
        }
    }
}