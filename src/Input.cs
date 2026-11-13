using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>Master volume through the Windows Core Audio API.</summary>
    internal static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
            [PreserveSig] int GetChannelCount(out uint c);
            [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        private const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;

        private static void Release(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }

        /// <summary>The default output's volume control. The caller must Release() it.</summary>
        private static IAudioEndpointVolume Endpoint()
        {
            object en = null;
            IMMDevice dev = null;
            try
            {
                en = new MMDeviceEnumeratorCom();
                if (((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(eRender, eMultimedia, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return null;
                var ep = o as IAudioEndpointVolume;
                if (ep == null) Release(o);
                return ep;
            }
            catch (Exception ex) { Program.Log(ex); return null; }
            finally { Release(dev); Release(en); }
        }

        public static float GetVolume()
        {
            var ep = Endpoint();
            float v = 0;
            if (ep != null) { ep.GetMasterVolumeLevelScalar(out v); Release(ep); }
            return v;
        }

        public static void SetVolume(float v)
        {
            var ep = Endpoint();
            Guid g = Guid.Empty;
            if (ep != null) { ep.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, v)), ref g); Release(ep); }
        }

        public static bool GetMute()
        {
            var ep = Endpoint();
            bool m = false;
            if (ep != null) { ep.GetMute(out m); Release(ep); }
            return m;
        }

        public static void SetMute(bool m)
        {
            var ep = Endpoint();
            Guid g = Guid.Empty;
            if (ep != null) { ep.SetMute(m, ref g); Release(ep); }
        }
    }

    /// <summary>
    /// Keyboard hook: makes a lone tap of the Windows key open Launchpad instead of Start
    /// (Win+anything shortcuts are untouched), and drives the Alt+Tab window switcher.
    /// </summary>
    internal static class WinKey
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Native.POINT pt; public uint lPrivate; }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr hMod, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int nCode, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private const uint WM_QUIT = 0x12, PM_NOREMOVE = 0;
        private const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10;
        private const uint VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_ESCAPE = 0x1B,
            VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

        private static LowLevelKeyboardProc proc;   // kept alive for the GC
        private static IntPtr hook;
        private static Thread thread;
        private static volatile uint threadId;
        private static volatile bool stopping;
        private static Dispatcher ui;

        // Only touched on the hook thread.
        private static bool winDown, comboUsed;
        private static bool switching, switchCancelled;

        public static void Install()
        {
            if (thread != null) return;
            ui = Application.Current.Dispatcher;
            stopping = false;
            proc = Hook;
            thread = new Thread(HookThread) { IsBackground = true, Name = "MacLook keyboard hook" };
            thread.Start();
        }

        public static void Uninstall()
        {
            var t = thread;
            if (t == null) return;
            stopping = true;
            uint tid = threadId;
            if (tid != 0) PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (t != Thread.CurrentThread) t.Join(500);
            thread = null;
        }

        /// <summary>
        /// The hook gets its own thread and message loop, so a busy UI thread (glass rendering, layout, GC)
        /// can never stall keyboard input system-wide or make Windows silently drop the hook for timing out.
        /// </summary>
        private static void HookThread()
        {
            MSG m;
            PeekMessage(out m, IntPtr.Zero, 0, 0, PM_NOREMOVE);   // create the message queue before anyone posts to it
            threadId = GetCurrentThreadId();
            IntPtr h;
            try
            {
                using (var p = Process.GetCurrentProcess())
                    h = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(p.MainModule.ModuleName), 0);
            }
            catch (Exception ex) { Program.Log(ex); return; }
            if (h == IntPtr.Zero)
            {
                Program.Log("Could not install the Windows key hook (error " + Marshal.GetLastWin32Error() + ").");
                return;
            }
            hook = h;
            while (!stopping && GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(h);
            hook = IntPtr.Zero;
        }

        private static IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Nothing may escape a hook callback: an exception here would take down the whole process.
            try
            {
                if (nCode >= 0)
                {
                    var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    if ((k.flags & LLKHF_INJECTED) == 0)
                    {
                        int msg = wParam.ToInt32();
                        bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN, up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                        if (SwitcherKey(k, down, up) || WindowsKey(k, down, up)) return new IntPtr(1);
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        /// <summary>True when the key was already held before this event (the hook runs before the async key state updates).</summary>
        private static bool WasDown(uint vk) { return (GetAsyncKeyState((int)vk) & 0x8000) != 0; }

        private static void Ui(Action a)
        {
            var d = ui;
            if (d != null && !d.HasShutdownStarted) d.BeginInvoke(a);
        }

        /// <summary>
        /// Replays the release of this key after an unassigned "mask" key, so Windows sees a key combo
        /// (no Start menu, no app menu bar) and the caller can swallow the real release. Returns false when
        /// Windows refuses the replay (UIPI: an elevated window is active); the real release must then go
        /// through, or the key would stay stuck down for the whole system.
        /// </summary>
        private static bool MaskRelease(KBDLLHOOKSTRUCT k)
        {
            return Native.SendMaskedKeyUp(k.vkCode, k.scanCode, (k.flags & LLKHF_EXTENDED) != 0);
        }

        /// <summary>Lone Windows key opens Launchpad. Returns true to swallow the key.</summary>
        private static bool WindowsKey(KBDLLHOOKSTRUCT k, bool down, bool up)
        {
            bool isWin = k.vkCode == VK_LWIN || k.vkCode == VK_RWIN;
            if (isWin && down)
            {
                // A fresh press (not auto-repeat) always starts over, even if the last release was never seen
                // (Win+L: the key comes up on the lock screen, where this hook doesn't run).
                if (!winDown || !WasDown(k.vkCode)) { winDown = true; comboUsed = false; }
                Taskbar.Enforce();
                return false;
            }
            if (!isWin && down && winDown) { comboUsed = true; return false; }
            if (isWin && up && winDown)
            {
                winDown = false;
                if (comboUsed || !Settings.WinKeyLaunchpad || !MaskRelease(k)) return false;
                Ui(() => { if (Program.Launchpad != null) Program.Launchpad.Toggle(); });
                return true;
            }
            return false;
        }

        /// <summary>
        /// Alt+Tab opens Mac Look's window switcher instead of Windows' own (when enabled).
        /// Returns true to swallow the key. The work runs on the UI queue so the hook returns instantly.
        /// </summary>
        private static bool SwitcherKey(KBDLLHOOKSTRUCT k, bool down, bool up)
        {
            uint vk = k.vkCode;
            bool isAlt = vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;
            if (isAlt && down)
            {
                if (switching && !WasDown(VK_MENU))
                {
                    // A fresh Alt press while still "switching": the last Alt release was never seen
                    // (e.g. it happened on the secure desktop). Drop the stale session.
                    switching = false;
                    Ui(() => Program.Switcher.Cancel());
                }
                return false;
            }
            if (isAlt && up)
            {
                if (!switching) return false;
                switching = false;
                if (!switchCancelled) Ui(() => Program.Switcher.Commit());
                // Same "mask key" trick as the Windows key: stops the Alt release from opening the app's menu.
                return MaskRelease(k);
            }
            // Once a session has started, finish it even if the setting or fullscreen state changes mid-way.
            if (!switching && (!Settings.SwitcherAltTab || Program.IsFullscreen)) return false;

            // Ask Windows whether Alt is held rather than tracking it: a release we never saw (Ctrl+Alt+Del,
            // lock screen) must not leave plain Tab hijacked.
            if (vk == VK_TAB && WasDown(VK_MENU))
            {
                if (down)
                {
                    bool shift = WasDown(VK_SHIFT);
                    if (!switching) { switching = true; switchCancelled = false; Ui(() => Program.Switcher.Open(shift)); }
                    else if (!switchCancelled) { if (shift) Ui(() => Program.Switcher.Prev()); else Ui(() => Program.Switcher.Next()); }
                }
                return true;
            }
            if (switching && !switchCancelled && down)
            {
                switch (vk)
                {
                    case VK_LEFT: case VK_UP: Ui(() => Program.Switcher.Prev()); return true;
                    case VK_RIGHT: case VK_DOWN: Ui(() => Program.Switcher.Next()); return true;
                    case VK_ESCAPE: switchCancelled = true; Ui(() => Program.Switcher.Cancel()); return true;
                    case VK_RETURN: switchCancelled = true; Ui(() => Program.Switcher.Commit()); return true;
                }
            }
            return false;
        }
    }
}
