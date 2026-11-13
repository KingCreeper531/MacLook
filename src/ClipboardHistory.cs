using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MacLook
{
    /// <summary>
    /// The last few pieces of text copied anywhere, for Spotlight's Clipboard tab.
    /// Kept in memory only (never written to disk), and anything a password manager marks as private is skipped.
    /// Start, Stop and Clear must be called on the UI thread.
    /// </summary>
    internal static class ClipboardHistory
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr h);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const uint CF_UNICODETEXT = 13;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private const int MaxItems = 40;
        private const int MaxChars = 100000;
        private const int ReadDelayMs = 40;    // let the copying app finish putting all its formats on the clipboard
        private const int ReadRetries = 5;     // another app may hold the clipboard open for a moment

        private static readonly List<string> items = new List<string>();
        private static HwndSource source;
        private static uint fmtExclude, fmtCanInclude, fmtViewerIgnore;
        private static int generation;         // a burst of updates is read once, after the last one

        /// <summary>Raised on the UI thread when an entry is added or the history is cleared.</summary>
        public static event Action Changed;

        /// <summary>Copied text, newest first. Read on the UI thread.</summary>
        public static IList<string> Items { get { return items.AsReadOnly(); } }

        public static bool Running { get { return source != null; } }

        /// <summary>Start listening if Settings.ClipboardHistory is on; otherwise stop and forget everything. Safe to call again.</summary>
        public static void Start()
        {
            if (!Settings.ClipboardHistory) { Stop(); return; }
            if (source != null) return;
            try
            {
                fmtExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
                fmtCanInclude = RegisterClipboardFormat("CanIncludeInClipboardHistory");
                fmtViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
                // A message-only window: never shown, never in Alt+Tab, only receives messages.
                var p = new HwndSourceParameters("MacLookClipboard") { ParentWindow = HWND_MESSAGE, WindowStyle = 0, Width = 0, Height = 0 };
                source = new HwndSource(p);
                source.AddHook(WndProc);
                if (!AddClipboardFormatListener(source.Handle))
                {
                    Program.Log("Clipboard history: AddClipboardFormatListener failed (" + Marshal.GetLastWin32Error() + ")");
                    Stop();
                }
            }
            catch (Exception ex) { Program.Log(ex); Stop(); }
        }

        /// <summary>Stop listening and forget all entries.</summary>
        public static void Stop()
        {
            generation++;
            if (source != null)
            {
                var s = source;
                source = null;
                try
                {
                    RemoveClipboardFormatListener(s.Handle);
                    s.RemoveHook(WndProc);
                    s.Dispose();
                }
                catch (Exception ex) { Program.Log(ex); }
            }
            Clear();
        }

        public static void Clear()
        {
            if (items.Count == 0) return;
            items.Clear();
            RaiseChanged();
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_CLIPBOARDUPDATE) return IntPtr.Zero;
            handled = true;
            try
            {
                int gen = ++generation;
                Launcher.Later(ReadDelayMs, () => Read(gen, 0));
            }
            catch (Exception ex) { Program.Log(ex); }
            return IntPtr.Zero;
        }

        private static void Read(int gen, int attempt)
        {
            try
            {
                if (gen != generation || source == null) return;   // a newer copy is coming, or we were stopped
                if (!OpenClipboard(source.Handle))
                {
                    if (attempt < ReadRetries) Launcher.Later(ReadDelayMs, () => Read(gen, attempt + 1));
                    return;
                }
                string text = null;
                try
                {
                    if (IsPrivate() || !IsClipboardFormatAvailable(CF_UNICODETEXT)) return;
                    IntPtr h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return;
                    long bytes = (long)GlobalSize(h).ToUInt64();
                    if (bytes < 2 || bytes / 2 > MaxChars + 1) return;   // empty, or too big to be worth keeping
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return;
                    try { text = Marshal.PtrToStringUni(p, (int)(bytes / 2)); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
                if (text == null) return;
                int nul = text.IndexOf('\0');
                if (nul >= 0) text = text.Substring(0, nul);
                Add(text);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Password managers and some apps tag secrets so clipboard history tools leave them alone.</summary>
        private static bool IsPrivate()
        {
            if (fmtExclude != 0 && IsClipboardFormatAvailable(fmtExclude)) return true;
            if (fmtViewerIgnore != 0 && IsClipboardFormatAvailable(fmtViewerIgnore)) return true;
            if (fmtCanInclude != 0 && IsClipboardFormatAvailable(fmtCanInclude))
            {
                IntPtr h = GetClipboardData(fmtCanInclude);
                if (h == IntPtr.Zero) return true;   // can't tell: assume it's private
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return true;
                try { return (long)GlobalSize(h).ToUInt64() < 4 || Marshal.ReadInt32(p) == 0; }
                finally { GlobalUnlock(h); }
            }
            return false;
        }

        private static void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return;
            if (items.Count > 0 && items[0] == text) return;
            items.Remove(text);   // copying something again moves it to the top
            items.Insert(0, text);
            if (items.Count > MaxItems) items.RemoveRange(MaxItems, items.Count - MaxItems);
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            var h = Changed;
            if (h == null) return;
            try { h(); } catch (Exception ex) { Program.Log(ex); }
        }
    }
}
