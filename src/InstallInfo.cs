using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MacLook
{
    /// <summary>
    /// Where Mac Look installs (per user, no admin), and how it registers/unregisters itself.
    /// Shared by MacLookSetup.exe and MacLook.exe --uninstall.
    /// </summary>
    internal static class InstallInfo
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\MacLook");
        public static readonly string Exe = Path.Combine(Dir, "MacLook.exe");
        public static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Mac Look.lnk");
        public static readonly string StartLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mac Look.lnk");
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacLook";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsInstalled { get { return File.Exists(Exe); } }

        public static void CreateShortcut(string link, string target)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "Mac Look";
            sc.Save();
        }

        public static void Register(string version, long sizeKb)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Mac Look");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "Mac Look");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("URLInfoAbout", "https://github.com/KingCreeper531/MacLook");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
            }
        }

        public static void SetRunAtLogin(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("MacLook", "\"" + Exe + "\"");
                else if (k.GetValue("MacLook") != null) k.DeleteValue("MacLook");
            }
        }

        /// <summary>Stops every running Mac Look except this process.</summary>
        public static void StopRunning()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("MacLook"))
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }

        /// <summary>Removes shortcuts, login entry and the Installed-apps entry, then deletes the folder once we've exited.</summary>
        public static void Unregister(bool removeSettings, string settingsDir)
        {
            try { if (File.Exists(DesktopLink)) File.Delete(DesktopLink); } catch { }
            try { if (File.Exists(StartLink)) File.Delete(StartLink); } catch { }
            try { SetRunAtLogin(false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

            string cmd = "ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"";
            if (removeSettings && !string.IsNullOrEmpty(settingsDir)) cmd += " & rmdir /s /q \"" + settingsDir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
