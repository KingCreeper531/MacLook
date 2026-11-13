using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MacLook
{
    internal class AppWindow
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string ExePath;
        public string ProcName;
        public string Aumid;
        public string Title;
        public bool FrameHost;      // UWP app hosted in ApplicationFrameHost

        public bool UseAumid
        {
            get
            {
                if (Aumid == null) return false;
                if (FrameHost || ExePath == null) return true;
                return ExePath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public string GroupKey
        {
            get { return UseAumid ? "aumid:" + Aumid.ToLowerInvariant() : "exe:" + (ExePath ?? ProcName).ToLowerInvariant(); }
        }

        /// <summary>Something the shell can both draw an icon for and launch.</summary>
        public string ShellTarget
        {
            get { return UseAumid ? "shell:AppsFolder\\" + Aumid : ExePath; }
        }
    }

    internal static class WindowTracker
    {
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint c);
            [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
            [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
            [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
            [FieldOffset(16)] public IntPtr p2;   // pads struct to 24 bytes on x64
        }

        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

        private const ushort VT_LPWSTR = 31;
        private static readonly Guid PKEY_AppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const uint PID_AppUserModel_ID = 5;

        private static readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;
        private static readonly Dictionary<IntPtr, string> aumidCache = new Dictionary<IntPtr, string>();
        private static readonly Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> shellClasses = new HashSet<string> {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland"
        };

        public static bool IsOwn(IntPtr h)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            return pid == selfPid;
        }

        public static bool IsShellWindow(IntPtr h)
        {
            return shellClasses.Contains(Native.GetClass(h));
        }

        /// <summary>Top-level windows the Windows taskbar would show, in z-order (most recent first).</summary>
        public static List<AppWindow> Enumerate()
        {
            var result = new List<AppWindow>();
            var alive = new HashSet<IntPtr>();
            Native.EnumWindows((h, l) =>
            {
                // Never let an exception unwind through the native EnumWindows frame.
                try
                {
                    var w = InspectStrict(h);
                    if (w != null) { result.Add(w); alive.Add(h); }
                }
                catch (Exception ex) { Program.Log(ex); }
                return true;
            }, IntPtr.Zero);

            var dead = new List<IntPtr>();
            foreach (var k in aumidCache.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (var k in dead) aumidCache.Remove(k);
            return result;
        }

        private static AppWindow InspectStrict(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return null;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            bool appWin = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (!appWin)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return null;
                if ((ex & Native.WS_EX_NOACTIVATE) != 0) return null;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return null;
            }
            if (Native.GetWindowTextLength(h) == 0) return null;
            if (Native.IsCloaked(h)) return null;
            if (shellClasses.Contains(Native.GetClass(h))) return null;
            var w = Describe(h);
            if (w == null) return null;
            if (w.FrameHost && w.Aumid == null) return null;
            return w;
        }

        /// <summary>Describe any window (used for the foreground app). Returns null for our own windows.</summary>
        public static AppWindow Describe(IntPtr h)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == selfPid || pid == 0) return null;
            var w = new AppWindow { Hwnd = h, Pid = pid, Title = Native.GetTitle(h) };
            w.ExePath = Native.GetProcessPath(pid);
            w.ProcName = w.ExePath != null ? Path.GetFileNameWithoutExtension(w.ExePath) : Native.GetProcessName(pid);
            w.FrameHost = Native.GetClass(h) == "ApplicationFrameWindow";
            w.Aumid = GetAumid(h);

            if (w.FrameHost)
            {
                IntPtr core = Native.FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero)
                {
                    uint cpid;
                    Native.GetWindowThreadProcessId(core, out cpid);
                    string p = Native.GetProcessPath(cpid);
                    if (p != null) { w.ProcName = Path.GetFileNameWithoutExtension(p); w.ExePath = p; w.Pid = cpid; }
                }
            }
            return w;
        }

        public static string DisplayName(AppWindow w)
        {
            if (w == null) return "";
            if (w.FrameHost && !string.IsNullOrEmpty(w.Title)) return w.Title;
            if (string.Equals(w.ProcName, "explorer", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
            if (w.ExePath == null) return Pretty(w.ProcName);

            string name;
            if (nameCache.TryGetValue(w.ExePath, out name)) return name;
            name = null;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(w.ExePath);
                name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(name)) name = Pretty(w.ProcName);
            name = name.Trim();
            if (name.Length > 40) name = name.Substring(0, 40);
            nameCache[w.ExePath] = name;
            return name;
        }

        private static string Pretty(string s)
        {
            if (string.IsNullOrEmpty(s)) return "App";
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static string GetAumid(IntPtr h)
        {
            string cached;
            if (aumidCache.TryGetValue(h, out cached)) return cached;
            string result = null;
            IPropertyStore ps = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(h, ref iid, out ps) == 0 && ps != null)
                {
                    var key = new PROPERTYKEY { fmtid = PKEY_AppUserModel, pid = PID_AppUserModel_ID };
                    PROPVARIANT pv;
                    if (ps.GetValue(ref key, out pv) == 0)
                    {
                        if (pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero) result = Marshal.PtrToStringUni(pv.p);
                        PropVariantClear(ref pv);
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            finally { if (ps != null) Marshal.ReleaseComObject(ps); }
            if (string.IsNullOrWhiteSpace(result)) result = null;
            aumidCache[h] = result;
            return result;
        }
    }
}
