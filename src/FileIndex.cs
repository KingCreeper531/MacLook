using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace MacLook
{
    internal class FileEntry
    {
        public string Name, Path;
        public bool IsDir;
        public DateTime Modified;
    }

    /// <summary>
    /// File names in the user's own folders plus their Recent items, for Spotlight's Files tab.
    /// Built on a background thread and swapped in whole, so searching never waits for the disk.
    /// </summary>
    internal static class FileIndex
    {
        private const int MaxEntries = 50000;
        private const int MaxDepth = 8;            // folder levels below each root
        private const int MaxRecent = 150;
        private const double StaleAfterMinutes = 3;
        private const string DownloadsGuid = "374DE290-123F-4565-9164-39C4925E467B";

        // Build and tool folders hold thousands of files nobody searches for by name.
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", "__pycache__", "$RECYCLE.BIN", "System Volume Information" };

        // Swapped whole by the UI thread; never changed after they are published.
        private static List<FileEntry> files = new List<FileEntry>();
        private static List<FileEntry> recent = new List<FileEntry>();
        private static List<FileEntry> latest = new List<FileEntry>();
        private static DateTime builtAt = DateTime.MinValue;
        private static bool building;

        /// <summary>Raised on the UI thread whenever new results are published.</summary>
        public static event Action Updated;

        /// <summary>The full index has been built at least once.</summary>
        public static bool Ready { get { return builtAt != DateTime.MinValue; } }

        /// <summary>Recently opened files and folders, newest first.</summary>
        public static List<FileEntry> Recent { get { return recent; } }

        /// <summary>The most recently changed files in the index, newest first (for when Windows keeps no Recent items).</summary>
        public static List<FileEntry> Latest { get { return latest; } }

        /// <summary>Re-index in the background if the index is missing or a few minutes old. Call on the UI thread.</summary>
        public static void RefreshIfStale()
        {
            if (building || (DateTime.Now - builtAt).TotalMinutes < StaleAfterMinutes) return;
            building = true;
            var ui = Dispatcher.CurrentDispatcher;
            var t = new Thread(() =>
            {
                List<FileEntry> rec = null, all = null, lat = null;
                try { rec = ReadRecent(); } catch (Exception ex) { Program.Log(ex); }
                // Recent items are quick to read: show them while the full walk is still going.
                if (rec != null) ui.BeginInvoke(new Action(() => { recent = rec; Notify(); }));
                try
                {
                    all = Walk();
                    lat = all.Where(f => !f.IsDir).OrderByDescending(f => f.Modified).Take(MaxRecent).ToList();
                }
                catch (Exception ex) { Program.Log(ex); }
                ui.BeginInvoke(new Action(() =>
                {
                    if (all != null) files = all;
                    if (lat != null) latest = lat;
                    builtAt = DateTime.Now;   // even after a failure, so we don't retry on every open
                    building = false;
                    Notify();
                }));
            });
            t.SetApartmentState(ApartmentState.STA);   // WScript.Shell (for .lnk targets) wants STA
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        private static void Notify()
        {
            var h = Updated;
            if (h == null) return;
            try { h(); } catch (Exception ex) { Program.Log(ex); }
        }

        private static IEnumerable<string> Roots()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Native.GetKnownFolder(DownloadsGuid);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }

        /// <summary>Breadth first, so a single huge folder can't use up the whole entry budget before the others are seen.</summary>
        private static List<FileEntry> Walk()
        {
            var list = new List<FileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<KeyValuePair<DirectoryInfo, int>>();
            foreach (string root in Roots())
            {
                if (string.IsNullOrEmpty(root) || !seen.Add(root.TrimEnd('\\'))) continue;
                try { if (Directory.Exists(root)) queue.Enqueue(new KeyValuePair<DirectoryInfo, int>(new DirectoryInfo(root), 0)); }
                catch (Exception ex) { Program.Log(ex); }
            }
            while (queue.Count > 0 && list.Count < MaxEntries)
            {
                var item = queue.Dequeue();
                try
                {
                    foreach (var fsi in item.Key.EnumerateFileSystemInfos())
                    {
                        var attr = fsi.Attributes;
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        string name = fsi.Name;
                        bool dir = (attr & FileAttributes.Directory) != 0;
                        if (dir ? name.StartsWith(".") || SkipDirs.Contains(name) : name.StartsWith("~$")) continue;   // ~$: Office lock files
                        list.Add(new FileEntry { Name = name, Path = fsi.FullName, IsDir = dir, Modified = fsi.LastWriteTime });
                        if (list.Count >= MaxEntries) break;
                        // seen: one folder linked from two roots (or a junction loop) is walked once.
                        if (dir && item.Value + 1 < MaxDepth && seen.Add(fsi.FullName))
                            queue.Enqueue(new KeyValuePair<DirectoryInfo, int>((DirectoryInfo)fsi, item.Value + 1));
                    }
                }
                catch (Exception) { }   // unreadable or vanished folder: skip it, keep what we have
            }
            return list;
        }

        /// <summary>The targets of the shortcuts Windows keeps in shell:recent, newest first.</summary>
        private static List<FileEntry> ReadRecent()
        {
            var list = new List<FileEntry>();
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            var links = new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(MaxRecent * 2).ToList();
            Type st = Type.GetTypeFromProgID("WScript.Shell");
            if (st == null) return list;
            object shellObj = Activator.CreateInstance(st);
            dynamic shell = shellObj;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var lnk in links)
                {
                    if (list.Count >= MaxRecent) break;
                    string target;
                    try { target = (string)shell.CreateShortcut(lnk.FullName).TargetPath; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(target) || !seen.Add(target)) continue;
                    bool isDir;
                    // Don't touch network paths: a missing server can stall for many seconds.
                    if (target.StartsWith(@"\\")) isDir = false;
                    else
                    {
                        isDir = Directory.Exists(target);
                        if (!isDir && !File.Exists(target)) continue;
                    }
                    string name = System.IO.Path.GetFileName(target.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(name)) name = target;
                    list.Add(new FileEntry { Name = name, Path = target, IsDir = isDir, Modified = lnk.LastWriteTime });
                }
            }
            finally { Marshal.ReleaseComObject(shellObj); }
            return list;
        }

        /// <summary>Files whose names contain every word of the query, best matches first.</summary>
        public static List<FileEntry> Search(string q, int max)
        {
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new List<FileEntry>();
            var hits = new List<KeyValuePair<FileEntry, int>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rec = recent;
            var all = files;
            for (int pass = 0; pass < 2; pass++)
            {
                var source = pass == 0 ? rec : all;
                foreach (var f in source)
                {
                    bool match = true;
                    foreach (var w in words)
                        if (f.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { match = false; break; }
                    if (!match || !seen.Add(f.Path)) continue;
                    string bare = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                    int s = Math.Max(AppCatalog.Score(f.Name, q), AppCatalog.Score(bare, q));
                    if (s == 0) s = 20;              // every word matched, just not side by side
                    if (pass == 0) s += 15;          // recently used files float up
                    hits.Add(new KeyValuePair<FileEntry, int>(f, s));
                }
            }
            return hits.OrderByDescending(h => h.Value).ThenByDescending(h => h.Key.Modified)
                       .Take(max).Select(h => h.Key).ToList();
        }
    }
}
