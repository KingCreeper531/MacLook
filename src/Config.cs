using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MacLook
{
    internal class DockEntry
    {
        public string Name;
        public string Target;
        public string Args = "";
        public List<string> Match = new List<string>();

        public bool IsAppsFolder { get { return Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase); } }
        public string Aumid { get { return IsAppsFolder ? Target.Substring(17) : null; } }

        public bool Matches(AppWindow w)
        {
            if (w.Aumid != null && Aumid != null && string.Equals(Aumid, w.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var m in Match)
            {
                if (string.Equals(m, w.ProcName, StringComparison.OrdinalIgnoreCase)) return true;
                if (w.Aumid != null && string.Equals(m, w.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (!IsAppsFolder && w.ExePath != null && string.Equals(Target, w.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string ToLine()
        {
            // '|' separates fields, so a name like "Photos | Vacation" would shift every column; Args is the
            // last field and is read back with any '|' it contains, so only the name needs cleaning.
            string name = (Name ?? "").Replace('|', '¦').Replace('\r', ' ').Replace('\n', ' ');
            return name + " | " + Target + " | " + string.Join(",", Match) + (string.IsNullOrEmpty(Args) ? "" : " | " + Args);
        }
    }

    internal static class Config
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacLook");
        public static readonly string DockFile = Path.Combine(Dir, "dock.txt");
        public static readonly string SettingsFile = Path.Combine(Dir, "settings.ini");

        public static List<DockEntry> Entries = new List<DockEntry>();

        public static void LoadDock()
        {
            if (!File.Exists(DockFile))
            {
                Entries = Defaults();
                AddBuiltinsOnce();
                SaveDock();
                return;
            }
            var list = new List<DockEntry>();
            foreach (var raw in File.ReadAllLines(DockFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                // At most 4 fields: anything after the third '|' belongs to the arguments.
                var parts = line.Split(new[] { '|' }, 4).Select(p => p.Trim()).ToArray();
                if (parts.Length < 2 || parts[1].Length == 0) continue;
                var e = new DockEntry { Name = parts[0], Target = Environment.ExpandEnvironmentVariables(parts[1]) };
                if (parts.Length > 2) e.Match = parts[2].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (parts.Length > 3) e.Args = parts[3];
                list.Add(e);
            }
            Entries = list;
            if (AddBuiltinsOnce()) SaveDock();
        }

        /// <summary>Puts Launchpad and Mission Control in the dock once (users may remove them later).
        /// Returns true if Entries changed and should be saved.</summary>
        private static bool AddBuiltinsOnce()
        {
            if (Settings.BuiltinsAdded) return false;
            Settings.BuiltinsAdded = true;
            Settings.Save();
            if (Entries.Any(e => e.Target.StartsWith("maclook:", StringComparison.OrdinalIgnoreCase))) return false;
            int at = Math.Min(1, Entries.Count);
            Entries.Insert(at, new DockEntry { Name = "Mission Control", Target = "maclook:missioncontrol" });
            Entries.Insert(at, new DockEntry { Name = "Launchpad", Target = "maclook:launchpad" });
            return true;
        }

        public static void SaveDock()
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine("# Mac Look dock. One app per line:   Name | Target | process names to match (optional) | arguments (optional)");
            sb.AppendLine("# Target is a path to an .exe, .lnk shortcut, folder or file, or shell:AppsFolder\\<AppID> for Store apps.");
            sb.AppendLine("# Save this file and the dock reloads automatically.");
            sb.AppendLine();
            foreach (var e in Entries) sb.AppendLine(e.ToLine());
            File.WriteAllText(DockFile, sb.ToString());
        }

        private static List<DockEntry> Defaults()
        {
            var list = new List<DockEntry>();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AddExe(list, "File Explorer", Path.Combine(win, "explorer.exe"), "explorer");

            var browsers = new[]
            {
                new[] { "Google Chrome", pf + @"\Google\Chrome\Application\chrome.exe", "chrome" },
                new[] { "Google Chrome", local + @"\Google\Chrome\Application\chrome.exe", "chrome" },
                new[] { "Firefox", pf + @"\Mozilla Firefox\firefox.exe", "firefox" },
                new[] { "Brave", pf + @"\BraveSoftware\Brave-Browser\Application\brave.exe", "brave" },
                new[] { "Opera GX", local + @"\Programs\Opera GX\launcher.exe", "opera" },
                new[] { "Microsoft Edge", pf86 + @"\Microsoft\Edge\Application\msedge.exe", "msedge" },
                new[] { "Microsoft Edge", pf + @"\Microsoft\Edge\Application\msedge.exe", "msedge" },
            };
            int count = 0;
            foreach (var b in browsers)
            {
                if (count >= 2) break;
                if (list.Any(e => e.Name == b[0])) continue;
                if (AddExe(list, b[0], b[1], b[2])) count++;
            }

            AddStoreApp(list, "Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", "photos");
            AddStoreApp(list, "Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "notepad");
            AddStoreApp(list, "Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "calculatorapp");
            AddStoreApp(list, "Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "windowsterminal");
            AddShortcut(list, "Discord", "discord");
            AddShortcut(list, "Spotify", "spotify");
            AddShortcut(list, "Steam", "steam,steamwebhelper");
            AddShortcut(list, "Visual Studio Code", "code");
            AddStoreApp(list, "Microsoft Store", "Microsoft.WindowsStore_8wekyb3d8bbwe!App", "");
            AddStoreApp(list, "Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", "systemsettings");
            return list;
        }

        private static DockEntry Make(string name, string target, string match)
        {
            return new DockEntry
            {
                Name = name, Target = target,
                Match = match.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            };
        }

        private static bool AddExe(List<DockEntry> list, string name, string path, string match)
        {
            if (!File.Exists(path)) return false;
            list.Add(Make(name, path, match));
            return true;
        }

        private static void AddStoreApp(List<DockEntry> list, string name, string aumid, string match)
        {
            string target = "shell:AppsFolder\\" + aumid;
            if (IconLoader.Exists(target)) list.Add(Make(name, target, match));
        }

        private static void AddShortcut(List<DockEntry> list, string name, string match)
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            };
            foreach (var root in roots)
            {
                string found = FindFile(root, name + ".lnk", 0);
                if (found != null) { list.Add(Make(name, found, match)); return; }
            }
        }

        private static string FindFile(string dir, string file, int depth)
        {
            try
            {
                string p = Path.Combine(dir, file);
                if (File.Exists(p)) return p;
                if (depth > 2) return null;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string r = FindFile(sub, file, depth + 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }
    }

    internal static class Settings
    {
        // General
        public static bool HideTaskbar = true;
        public static bool WinKeyLaunchpad = true;
        public static bool AutoUpdate = true;
        // Dock
        public static int IconSize = 50;
        public static bool Magnify = true;
        public static double MagnifyScale = 1.6;
        public static bool MagnifyInside = true;      // grow the glass with the icons instead of popping out
        public static bool DockAutoHide = true;
        public static bool ReserveDockSpace = false;
        public static bool ShowRunning = true;
        public static bool ShowIndicators = true;
        public static bool BounceOnLaunch = true;
        public static int DockRadius = 24;
        // Menu bar
        public static bool ShowAppMenus = true;
        public static bool ShowWifi = true;
        public static bool ShowSearch = true;
        public static bool ShowSound = true;
        public static bool ClockDay = true;
        public static bool ClockDate = true;
        public static bool Clock24h = false;
        public static bool ClockSeconds = false;
        // Appearance
        public static string ThemeMode = "auto";      // auto | light | dark
        public static string Accent = "blue";
        public static double GlassFrost = 1.0;
        public static double GlassTint = 1.0;
        public static double GlassRefraction = 1.0;
        public static double GlassShine = 1.0;
        public static bool DockShowWhenClear = true;  // auto-hide only when a window actually covers the dock
        // Icons & windows
        public static string IconStyle = "glass";     // original | glass
        public static string WindowFrames = "acrylic"; // off | mica | acrylic
        public static bool AnimateWindows = true;
        // Launchpad
        public static string LaunchpadSort = "az";    // az | used | custom
        public static int LaunchpadIconSize = 84;
        public static bool LaunchpadShowAll = false;     // include help files, manuals, websites and admin tools
        // Hot corners & shortcuts
        public static string HotTopLeft = "none";
        public static string HotTopRight = "none";
        public static string HotBottomLeft = "none";
        public static string HotBottomRight = "none";
        public static bool ScreenshotShortcuts = true;
        public static bool ForceQuitShortcut = true;
        public static bool SwitcherAltTab = true;
        public static string WindowButtons = "replace";   // replace (cover Windows' buttons) | left | off
        public static bool MacSettingsForWindows = true;   // the Dock's Settings icon opens Mac Look's System Settings
        // Internal
        public static string OriginalWallpaper = "";
        public static bool BuiltinsAdded = false;
        public static int OriginalMinAnimate = -1;      // Windows' minimize-animation setting before Mac Look changed it (-1 = unchanged)

        private static System.Reflection.FieldInfo[] Fields
        {
            get { return typeof(Settings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); }
        }

        public static void Load()
        {
            if (!File.Exists(Config.SettingsFile)) return;
            string[] lines;
            try { lines = File.ReadAllLines(Config.SettingsFile); }
            catch (Exception ex) { Program.Log(ex); return; }   // locked or unreadable: keep the defaults
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var fields = Fields;
            foreach (var line in lines)
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                var f = fields.FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (f == null) continue;
                // A malformed value leaves the field at its default instead of turning it into false / 0.
                if (f.FieldType == typeof(bool))
                {
                    bool b;
                    if (bool.TryParse(v, out b)) f.SetValue(null, b);
                }
                else if (f.FieldType == typeof(int))
                {
                    int n;
                    if (int.TryParse(v, System.Globalization.NumberStyles.Integer, ci, out n)) f.SetValue(null, n);
                }
                else if (f.FieldType == typeof(double))
                {
                    // Accept "1,6" from hand edits in comma-decimal locales; never read ',' as a thousands separator.
                    double d;
                    if (double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, ci, out d)
                        && !double.IsNaN(d) && !double.IsInfinity(d))
                        f.SetValue(null, d);
                }
                else if (f.FieldType == typeof(string)) f.SetValue(null, v);
            }
            // Keep hand-edited values inside the ranges the Settings sliders allow (a negative radius would throw).
            IconSize = Math.Max(32, Math.Min(96, IconSize));
            MagnifyScale = Math.Max(1.0, Math.Min(2.5, MagnifyScale));
            DockRadius = Math.Max(0, Math.Min(48, DockRadius));
            LaunchpadIconSize = Math.Max(40, Math.Min(160, LaunchpadIconSize));
            GlassFrost = Math.Max(0, Math.Min(2, GlassFrost));
            GlassTint = Math.Max(0, Math.Min(1.6, GlassTint));
            GlassRefraction = Math.Max(0, Math.Min(2.5, GlassRefraction));
            GlassShine = Math.Max(0, Math.Min(2, GlassShine));
        }

        public static void Save()
        {
            Directory.CreateDirectory(Config.Dir);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllLines(Config.SettingsFile, Fields.Select(f =>
            {
                object v = f.GetValue(null);
                string s = v is bool ? ((bool)v ? "true" : "false") : v is double ? ((double)v).ToString(ci) : Convert.ToString(v, ci);
                return char.ToLowerInvariant(f.Name[0]) + f.Name.Substring(1) + "=" + s;
            }).ToArray());
        }
    }
}
