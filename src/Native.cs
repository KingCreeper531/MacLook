using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MacLook
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
        public const uint ABE_TOP = 1, ABE_BOTTOM = 3;
        public const int ABN_POSCHANGED = 1;
        public const int ABS_AUTOHIDE = 1;

        [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string lpString);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);

        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_MAXIMIZE = 3, SW_SHOW = 5, SW_MINIMIZE = 6, SW_SHOWNA = 8, SW_RESTORE = 9;

        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        public const uint GA_ROOTOWNER = 3;
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        public const uint GW_OWNER = 4;
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr val);
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        public const uint WM_CLOSE = 0x10, WM_HOTKEY = 0x312;

        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        public const int DWMWA_CLOAKED = 14;

        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        public const uint KEYEVENTF_KEYUP = 2;

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        public const uint MOD_ALT = 1, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll")] public static extern bool LockWorkStation();

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

        // ---- window events (instant taskbar re-hide) ----
        public delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        public const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

        // ---- DWM attributes for other apps' windows ----
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT val, int size);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)] public struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] public static extern bool SystemParametersInfoAnim(uint action, uint param, ref ANIMATIONINFO pv, uint winIni);
        public const uint SPI_GETANIMATION = 0x48, SPI_SETANIMATION = 0x49;

        /// <summary>The visible frame of a window (without the invisible resize borders).</summary>
        public static RECT FrameRect(IntPtr h)
        {
            RECT r;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
            return r;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, UIntPtr w, string l, uint flags, uint timeout, out UIntPtr result);

        // ---- DWM live thumbnails (Mission Control) ----
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }
        [StructLayout(LayoutKind.Sequential)] public struct PSIZE { public int x, y; }
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
        public const uint DWM_TNP_RECTDESTINATION = 1, DWM_TNP_OPACITY = 4, DWM_TNP_VISIBLE = 8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
        public const uint MOD_CONTROL = 2;

        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
        public static extern bool SystemParametersInfoGet(uint action, uint param, StringBuilder pv, uint winIni);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
        public static extern bool SystemParametersInfoSet(uint action, uint param, string pv, uint winIni);
        public const uint SPI_SETDESKWALLPAPER = 0x14, SPI_GETDESKWALLPAPER = 0x73;

        [DllImport("shell32.dll")] public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        // ---- Acrylic / blur behind (undocumented but stable since Windows 10) ----
        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        /// <summary>state: 3 = blur, 4 = acrylic. argb is a normal 0xAARRGGBB tint.</summary>
        public static bool SetBlur(IntPtr hwnd, int state, uint argb)
        {
            uint a = (argb >> 24) & 0xFF, r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
            var accent = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = (a << 24) | (b << 16) | (g << 8) | r };
            int size = Marshal.SizeOf(accent);
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = p, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---- helpers ----
        public static string GetTitle(IntPtr h)
        {
            int len = GetWindowTextLength(h);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClass(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsCloaked(IntPtr h)
        {
            int v;
            return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out v, 4) == 0 && v != 0;
        }

        public static string GetProcessPath(uint pid)
        {
            IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hp == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(hp); }
        }

        public static string GetProcessName(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "app"; }
        }

        public static string GetKnownFolder(string guid)
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }

        public static void PressKeys(params byte[] keys)
        {
            foreach (byte k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
            for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void ActivateWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            if (!SetForegroundWindow(h))
            {
                // Foreground lock fallback: a synthetic key press lets us take foreground.
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(h);
            }
            BringWindowToTop(h);
        }

        public const byte VK_CTRL = 0x11, VK_SHIFT = 0x10, VK_ALT = 0x12, VK_WIN = 0x5B, VK_LEFT = 0x25, VK_RIGHT = 0x27,
            VK_F1 = 0x70, VK_F11 = 0x7A, VK_PLUS = 0xBB, VK_MINUS = 0xBD, VK_PERIOD = 0xBE, VK_SPACE = 0x20;
    }
}
