using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

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

        private static IAudioEndpointVolume Endpoint()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return null;
                return o as IAudioEndpointVolume;
            }
            catch { return null; }
        }

        public static float GetVolume()
        {
            var ep = Endpoint();
            float v = 0;
            if (ep != null) ep.GetMasterVolumeLevelScalar(out v);
            return v;
        }

        public static void SetVolume(float v)
        {
            var ep = Endpoint();
            Guid g = Guid.Empty;
            if (ep != null) ep.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, v)), ref g);
        }

        public static bool GetMute()
        {
            var ep = Endpoint();
            bool m = false;
            if (ep != null) ep.GetMute(out m);
            return m;
        }

        public static void SetMute(bool m)
        {
            var ep = Endpoint();
            Guid g = Guid.Empty;
            if (ep != null) ep.SetMute(m, ref g);
        }
    }

    /// <summary>
    /// Makes a lone tap of the Windows key open Launchpad instead of Start.
    /// Win+anything shortcuts are untouched.
    /// </summary>
    internal static class WinKey
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr hMod, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int nCode, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

        private static LowLevelKeyboardProc proc;   // kept alive for the GC
        private static IntPtr hook;
        private static bool winDown, comboUsed;

        public static void Install()
        {
            if (hook != IntPtr.Zero) return;
            proc = Hook;
            using (var p = Process.GetCurrentProcess())
                hook = SetWindowsHookEx(13, proc, GetModuleHandle(p.MainModule.ModuleName), 0);
            if (hook == IntPtr.Zero) Program.Log("Could not install the Windows key hook.");
        }

        public static void Uninstall()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        private static IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool injected = (k.flags & 0x10) != 0;
                int msg = wParam.ToInt32();
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                bool isWin = k.vkCode == 0x5B || k.vkCode == 0x5C;
                if (!injected)
                {
                    IntPtr sw = SwitcherKey(k.vkCode, down, up);
                    if (sw != IntPtr.Zero) return sw;

                    if (isWin && down) { if (!winDown) { winDown = true; comboUsed = false; } Taskbar.Enforce(); }
                    else if (!isWin && down && winDown) comboUsed = true;
                    else if (isWin && up && winDown)
                    {
                        winDown = false;
                        if (!comboUsed && Settings.WinKeyLaunchpad)
                        {
                            // Swallow this key-up and replay it after an unassigned "mask" key,
                            // so Windows sees a key combo and doesn't open Start.
                            byte vk = (byte)k.vkCode;
                            Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                            Native.keybd_event(0xE8, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                            Native.keybd_event(vk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                            Application.Current.Dispatcher.BeginInvoke(new Action(() => Program.Launchpad.Toggle()));
                            return new IntPtr(1);
                        }
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        private static bool altDown, switching, switchCancelled;

        private static void Ui(Action a) { Application.Current.Dispatcher.BeginInvoke(a); }

        /// <summary>
        /// Alt+Tab opens Mac Look's window switcher instead of Windows' own (when enabled).
        /// Returns non-zero to swallow the key. The work runs on the UI queue so the hook returns instantly.
        /// </summary>
        private static IntPtr SwitcherKey(uint vk, bool down, bool up)
        {
            bool isAlt = vk == 0x12 || vk == 0xA4 || vk == 0xA5;
            if (isAlt && down) { altDown = true; return IntPtr.Zero; }
            if (isAlt && up)
            {
                altDown = false;
                if (!switching) return IntPtr.Zero;
                switching = false;
                if (!switchCancelled) Ui(() => Program.Switcher.Commit());
                // Same "mask key" trick as the Windows key: stops the Alt release from opening the app's menu.
                Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                Native.keybd_event(0xE8, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Native.keybd_event((byte)vk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                return new IntPtr(1);
            }
            if (!Settings.SwitcherAltTab || Program.IsFullscreen) return IntPtr.Zero;

            if (vk == 0x09 && altDown)   // Tab
            {
                if (down)
                {
                    bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    if (!switching) { switching = true; switchCancelled = false; Ui(() => Program.Switcher.Open(shift)); }
                    else if (!switchCancelled) { if (shift) Ui(() => Program.Switcher.Prev()); else Ui(() => Program.Switcher.Next()); }
                }
                return new IntPtr(1);
            }
            if (switching && !switchCancelled && down)
            {
                switch (vk)
                {
                    case 0x25: case 0x26: Ui(() => Program.Switcher.Prev()); return new IntPtr(1);   // Left / Up
                    case 0x27: case 0x28: Ui(() => Program.Switcher.Next()); return new IntPtr(1);   // Right / Down
                    case 0x1B: switchCancelled = true; Ui(() => Program.Switcher.Cancel()); return new IntPtr(1);   // Esc
                    case 0x0D: switchCancelled = true; Ui(() => Program.Switcher.Commit()); return new IntPtr(1);   // Enter
                }
            }
            return IntPtr.Zero;
        }
    }
}
