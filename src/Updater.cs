using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

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

        private const string UserAgent = "MacLook-Updater";
        private const long MinExeBytes = 50000;   // anything smaller is an error page, not Mac Look

        private static bool busy;
        private static DispatcherTimer timer;

        /// <summary>Adds TLS 1.2 (GitHub requires it) without switching off newer protocols the system allows.</summary>
        private static void EnableTls12()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        }

        /// <summary>Runs the result back on the UI thread, unless the app is already shutting down.</summary>
        private static void OnUi(Dispatcher ui, Action a)
        {
            if (ui != null && !ui.HasShutdownStarted) ui.BeginInvoke(a);
        }

        /// <summary>Check shortly after startup and then every 6 hours.</summary>
        public static void Start()
        {
            Launcher.Later(20000, () => Check(false));
            timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += (s, e) => Check(false);
            timer.Start();
        }

        /// <param name="manual">True when the user pressed "Check for Updates": always report the result.</param>
        public static void Check(bool manual)
        {
            if (busy) return;
            if (!manual && !Settings.AutoUpdate) return;
            busy = true;
            var ui = Application.Current.Dispatcher;   // captured here: Application.Current is not for worker threads
            var t = new Thread(() =>
            {
                string tag = null, url = null, error = null;
                try { Latest(out tag, out url); }
                catch (Exception ex) { error = ex.Message; }
                OnUi(ui, () => OnChecked(manual, tag, url, error));
            });
            t.IsBackground = true;
            t.Start();
        }

        private static void Latest(out string tag, out string url)
        {
            EnableTls12();
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
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
            int major, minor, build = 0;
            if (!int.TryParse(m.Groups[1].Value, out major) || !int.TryParse(m.Groups[2].Value, out minor)) return null;
            if (m.Groups[3].Success && !int.TryParse(m.Groups[3].Value, out build)) return null;
            return new Version(major, minor, build, 0);
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
            // The file name comes from the parsed version, never the raw tag (tags may contain / or other characters).
            Download(url, latest.ToString(3));
        }

        private static void Download(string url, string version)
        {
            busy = true;
            var ui = Application.Current.Dispatcher;
            var t = new Thread(() =>
            {
                string file = null, error = null;
                try
                {
                    string dir = Path.Combine(Config.Dir, "update");
                    Directory.CreateDirectory(dir);
                    file = Path.Combine(dir, "MacLook-" + version + ".exe");
                    // Download to a temporary name and only rename a complete file into place,
                    // so a dropped connection can never leave a truncated exe that gets installed.
                    string part = file + ".part";
                    if (File.Exists(part)) File.Delete(part);
                    EnableTls12();
                    long expected = -1;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                        wc.DownloadFile(url, part);
                        long len;
                        string cl = wc.ResponseHeaders == null ? null : wc.ResponseHeaders[HttpResponseHeader.ContentLength];
                        if (cl != null && long.TryParse(cl, out len)) expected = len;
                    }
                    long got = new FileInfo(part).Length;
                    if (got < MinExeBytes || (expected >= 0 && got != expected))
                    {
                        try { File.Delete(part); } catch { }
                        throw new Exception("The downloaded file looks incomplete.");
                    }
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(part, file);
                }
                catch (Exception ex) { error = ex.Message; }
                OnUi(ui, () =>
                {
                    busy = false;
                    if (error != null) { Program.Log("Update failed: " + error); return; }
                    Install(file);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// A tiny script waits for Mac Look to exit, replaces the exe (retrying while the old file is still
        /// locked), and starts Mac Look again. The paths travel as environment variables, not inside the script:
        /// cmd reads .cmd files in the OEM code page, which would mangle non-ASCII user names, and
        /// % or ^ in a path would be interpreted.
        /// </summary>
        private static void Install(string newExe)
        {
            try
            {
                string exe;
                int pid;
                using (var self = Process.GetCurrentProcess()) { exe = self.MainModule.FileName; pid = self.Id; }
                string script = Path.Combine(Config.Dir, "update", "apply-update.cmd");
                File.WriteAllText(script,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    "tasklist /FI \"PID eq " + pid + "\" | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                    "set tries=0\r\n" +
                    ":copy\r\n" +
                    "copy /Y \"%MACLOOK_NEW%\" \"%MACLOOK_EXE%\" >nul && (del \"%MACLOOK_NEW%\" >nul 2>&1 & goto run)\r\n" +
                    "set /a tries+=1\r\n" +
                    "if %tries% lss 10 (timeout /t 1 /nobreak >nul & goto copy)\r\n" +
                    ":run\r\n" +
                    "start \"\" \"%MACLOOK_EXE%\"\r\n");
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                psi.EnvironmentVariables["MACLOOK_NEW"] = newExe;
                psi.EnvironmentVariables["MACLOOK_EXE"] = exe;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                // Don't quit if the swap can't even be scheduled: the user would be left with no Mac Look running.
                Program.Log("Update failed: " + ex);
                return;
            }
            Program.Log("Updating to " + newExe);
            Program.Quit();
        }
    }
}
