using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace MacLook
{
    /// <summary>
    /// Keeps Mac Look up to date from the GitHub repo's releases: checks the latest release,
    /// downloads MacLook.exe if it's newer, then swaps the file and restarts.
    /// </summary>
    internal static class Updater
    {
        public const string Repo = "KingCreeper531/MacLook";
        public static string RepoUrl { get { return "https://github.com/" + Repo; } }

        public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public static string CurrentText { get { var v = Current; return v.Major + "." + v.Minor + "." + v.Build; } }

        private static bool busy;
        private static System.Windows.Threading.DispatcherTimer timer;

        /// <summary>Check shortly after startup and then every 6 hours.</summary>
        public static void Start()
        {
            Launcher.Later(20000, () => Check(false));
            timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += (s, e) => Check(false);
            timer.Start();
        }

        /// <param name="manual">True when the user pressed "Check for Updates": always report the result.</param>
        public static void Check(bool manual)
        {
            if (busy) return;
            if (!manual && !Settings.AutoUpdate) return;
            busy = true;
            var t = new Thread(() =>
            {
                string tag = null, url = null, error = null;
                try { Latest(out tag, out url); }
                catch (Exception ex) { error = ex.Message; }
                Application.Current.Dispatcher.BeginInvoke(new Action(() => OnChecked(manual, tag, url, error)));
            });
            t.IsBackground = true;
            t.Start();
        }

        private static void Latest(out string tag, out string url)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "MacLook-Updater";
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                string json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
                var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                var a = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/MacLook\\.exe)\"");
                tag = m.Success ? m.Groups[1].Value : null;
                url = a.Success ? a.Groups[1].Value : null;
            }
        }

        private static Version Parse(string tag)
        {
            if (tag == null) return null;
            var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0, 0);
        }

        private static void OnChecked(bool manual, string tag, string url, string error)
        {
            busy = false;
            var latest = Parse(tag);
            var cur = new Version(Current.Major, Current.Minor, Current.Build, 0);
            if (error != null || latest == null || url == null)
            {
                if (manual) MessageBox.Show("Couldn't check for updates right now.\n\n" + (error ?? "No release found yet."), "Mac Look");
                return;
            }
            if (latest <= cur)
            {
                if (manual) MessageBox.Show("You're up to date (Mac Look " + CurrentText + ").", "Mac Look");
                return;
            }
            if (manual && MessageBox.Show("Mac Look " + tag.TrimStart('v') + " is available (you have " + CurrentText + ").\n\nUpdate and restart now?",
                    "Mac Look", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            Download(url, tag);
        }

        private static void Download(string url, string tag)
        {
            busy = true;
            var t = new Thread(() =>
            {
                string file = null, error = null;
                try
                {
                    string dir = Path.Combine(Config.Dir, "update");
                    Directory.CreateDirectory(dir);
                    file = Path.Combine(dir, "MacLook-" + tag + ".exe");
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "MacLook-Updater";
                        wc.DownloadFile(url, file);
                    }
                    if (new FileInfo(file).Length < 50000) throw new Exception("The downloaded file looks incomplete.");
                }
                catch (Exception ex) { error = ex.Message; }
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    busy = false;
                    if (error != null) { Program.Log("Update failed: " + error); return; }
                    Install(file);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>A tiny script waits for Mac Look to exit, replaces the exe, and starts the new one.</summary>
        private static void Install(string newExe)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int pid = Process.GetCurrentProcess().Id;
            string script = Path.Combine(Config.Dir, "update", "apply-update.cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"PID eq " + pid + "\" | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                "copy /Y \"" + newExe + "\" \"" + exe + "\" >nul\r\n" +
                "start \"\" \"" + exe + "\"\r\n" +
                "del \"" + newExe + "\" >nul 2>&1\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            Program.Log("Updating to " + newExe);
            Program.Quit();
        }
    }
}
