using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>
    /// Closes a popover-style window when you click anywhere outside it, like macOS menus and panels.
    /// Polls the mouse buttons only while the window is visible (no global hook, nothing to clean up).
    /// </summary>
    internal static class ClickAway
    {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Native.RECT r);

        private const int VK_LBUTTON = 1, VK_RBUTTON = 2, VK_MBUTTON = 4, PollMs = 25;
        private const uint GA_ROOT = 2;
        private static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

        public static void Watch(Window w, Action close)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(PollMs) };
            bool wasDown = true;   // a button already held when we open (the click that opened us) doesn't count
            timer.Tick += (s, e) =>
            {
                try
                {
                    bool down = Pressed(VK_LBUTTON) || Pressed(VK_RBUTTON) || Pressed(VK_MBUTTON);
                    bool fresh = down && !wasDown;
                    wasDown = down;
                    if (!fresh || !w.IsVisible) return;
                    Native.POINT p;
                    if (!Native.GetCursorPos(out p)) return;
                    var me = new WindowInteropHelper(w).Handle;
                    Native.RECT r;
                    if (GetWindowRect(me, out r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return;
                    // Our own menus and popups (e.g. a right-click menu opened from the panel) aren't "outside".
                    // The menu bar and Dock are, so clicking them closes the panel like on a Mac.
                    var root = GetAncestor(WindowFromPoint(p), GA_ROOT);
                    uint pid;
                    Native.GetWindowThreadProcessId(root, out pid);
                    if (pid == ownPid && !IsBar(root)) return;
                    close();
                }
                catch (Exception ex) { Program.Log(ex); }
            };
            w.IsVisibleChanged += (s, e) =>
            {
                if (w.IsVisible) { wasDown = true; timer.Start(); }
                else timer.Stop();
            };
        }

        private static bool Pressed(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        private static bool IsBar(IntPtr h)
        {
            return (Program.MenuBar != null && h == new WindowInteropHelper(Program.MenuBar).Handle)
                || (Program.Dock != null && h == new WindowInteropHelper(Program.Dock).Handle);
        }
    }
}
