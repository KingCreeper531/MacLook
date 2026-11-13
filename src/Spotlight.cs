using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MacLook
{
    /// <summary>Spotlight: one search box over four tabs (apps, files, actions and recently copied text).</summary>
    internal class SpotlightWindow : GlassPanel
    {
        private class Result
        {
            public string Title, Subtitle, IconGlyph;
            public ImageSource Icon;
            public Func<ImageSource> LoadIcon;   // slow shell icons arrive just after the row is on screen
            public Action Run;
            public Action Reveal;                // Ctrl+Enter / Ctrl+click
            public bool KeepOpen;
        }

        private class SpotAction
        {
            public string Title, Subtitle, Glyph, Words;
            public Action Run;
        }

        private const int AppsTab = 0, FilesTab = 1, ActionsTab = 2, ClipboardTab = 3, TabCount = 4;
        private static readonly string[] TabNames = { "Apps", "Files", "Actions", "Clipboard" };
        private static readonly string[] TabGlyphs = { "", "", "", "" };
        private static readonly string[] Hints =
        {
            "↩  Open", "↩  Open      Ctrl+↩  Show in Explorer", "↩  Run", "↩  Copy"
        };

        private const double RowHeight = 46;
        private const double ListHeight = RowHeight * 8 + 22;   // eight rows plus a section header
        private const double DimTab = 0.55;
        private const double SlideBy = 56;                      // how far the lists travel when the tab changes
        private const double OpenScale = 0.955;                 // on top of GlassPanel's own 0.96: about 0.92 overall
        private const int PillMs = 240, SlideInMs = 180, SlideOutMs = 140, OpenMs = 300, LabelFadeMs = 120;
        private const int MaxApps = 8, FrequentApps = 8, MaxFiles = 40, RecentFiles = 30;
        private const int CloseFirstMs = 140;                   // let Spotlight fade before another panel photographs the screen
        private const int ScreenshotDelayMs = 250;
        private const int IconBudgetMs = 4;                     // icon loading per dispatcher turn, so frames keep flowing
        private const int IndexWarmUpMs = 15000;                // first file index, a little after startup
        private const uint SHERB_NOCONFIRMATION = 0x1;

        private TextBox box;
        private TextBlock placeholder, hint;
        private Grid listHost;
        private ScrollViewer page;
        private Border pill;
        private TranslateTransform pillMove;
        private ScaleTransform opener;
        private readonly List<Border> tabs = new List<Border>();
        private List<Border> rows = new List<Border>();
        private List<Result> current = new List<Result>();
        private int selected = -1, tab, iconGen;
        private bool quiet;
        private Point lastMouse;

        public SpotlightWindow()
        {
            Title = "Spotlight";
            SizeToContent = SizeToContent.Height;
            Width = 740;
            Build();
            PreviewKeyDown += OnKey;
            FileIndex.Updated += () => { if (IsVisible && tab == FilesTab) Refresh(); };
            ClipboardHistory.Changed += () => { if (IsVisible && tab == ClipboardTab) Refresh(); };
            Launcher.Later(IndexWarmUpMs, FileIndex.RefreshIfStale);
        }

        // ------------------------------------------------------------------ layout

        private void Build()
        {
            var card = GlassCard(GlassStyles.Panel, 28);
            card.Padding = new Thickness(16, 10, 16, 8);

            var stack = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var glass = new TextBlock
            {
                Text = "", FontFamily = new FontFamily(Theme.IconFont), FontSize = 20, Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0)
            };
            box = new TextBox
            {
                FontSize = 24, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 4, 0, 4), FocusVisualStyle = null
            };
            box.SetResourceReference(TextBox.ForegroundProperty, "SpotFg");
            box.SetResourceReference(TextBox.CaretBrushProperty, "SpotFg");
            box.TextChanged += (s, e) =>
            {
                placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (!quiet) Search(0);
            };
            placeholder = new TextBlock
            {
                Text = "Spotlight Search", FontSize = 24, IsHitTestVisible = false, Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0)
            };
            Grid.SetColumn(box, 1);
            Grid.SetColumn(placeholder, 1);
            row.Children.Add(glass);
            row.Children.Add(box);
            row.Children.Add(placeholder);

            var divider = new Border { Height = 1, Margin = new Thickness(-16, 8, -16, 6) };
            divider.SetResourceReference(Border.BackgroundProperty, "MenuSep");
            // Clipped, so the outgoing and incoming lists slide past the edges instead of over the tabs.
            listHost = new Grid { Height = ListHeight, ClipToBounds = true };

            var foot = new Grid { Margin = new Thickness(8, 6, 8, 0) };
            hint = FootText("", HorizontalAlignment.Left);
            foot.Children.Add(hint);
            foot.Children.Add(FootText("Ctrl+Tab  Next Tab", HorizontalAlignment.Right));

            stack.Children.Add(row);
            stack.Children.Add(BuildTabs());
            stack.Children.Add(divider);
            stack.Children.Add(listHost);
            stack.Children.Add(foot);
            card.Child = stack;

            var body = WithShadow(card, 28);
            body.Margin = new Thickness(30, 10, 30, 40);
            opener = new ScaleTransform(1, 1);
            body.RenderTransformOrigin = new Point(0.5, 0.25);
            body.RenderTransform = opener;
            // GlassPanel animates Content itself; our own spring scale sits one level in, so the two combine.
            var root = new Grid();
            root.Children.Add(body);
            Content = root;
        }

        private static TextBlock FootText(string text, HorizontalAlignment align)
        {
            var t = new TextBlock { Text = text, FontSize = 11, Opacity = 0.45, HorizontalAlignment = align };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SpotFg");
            return t;
        }

        private FrameworkElement BuildTabs()
        {
            var strip = new Grid { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            pillMove = new TranslateTransform();
            pill = new Border { CornerRadius = new CornerRadius(15), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, RenderTransform = pillMove };
            pill.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            strip.Children.Add(pill);

            var labels = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < TabCount; i++)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock
                {
                    Text = TabGlyphs[i], FontFamily = new FontFamily(Theme.IconFont), FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0)
                });
                // Same weight selected or not: a width change would re-snap the pill mid-slide.
                content.Children.Add(new TextBlock { Text = TabNames[i], FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                var t = new Border { Padding = new Thickness(13, 6, 14, 6), Background = Brushes.Transparent, Child = content, Opacity = DimTab };
                t.SetResourceReference(TextBlock.ForegroundProperty, "SpotFg");
                int index = i;
                t.MouseLeftButtonDown += (s, e) => { e.Handled = true; SelectTab(index); };
                t.SizeChanged += (s, e) => SnapPill();
                labels.Children.Add(t);
                tabs.Add(t);
            }
            strip.Children.Add(labels);
            return strip;
        }

        // ------------------------------------------------------------------ panel lifecycle

        protected override void OnOpening()
        {
            FileIndex.RefreshIfStale();
            listHost.Children.Clear();
            page = null;
            tab = AppsTab;
            quiet = true;
            box.Text = "";
            quiet = false;
            TabLook(false);
            SnapPill();
            Search(0);
        }

        protected override void Place()
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.2;
        }

        protected override void OnShown()
        {
            box.Focus();
            Keyboard.Focus(box);
            // A slightly stronger spring than GlassPanel's own entrance, like Spotlight popping open.
            var ease = new SpringEase(0.62, 12);
            var d = TimeSpan.FromMilliseconds(OpenMs);
            opener.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(OpenScale, 1, d) { EasingFunction = ease });
            opener.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(OpenScale, 1, d) { EasingFunction = ease });
        }

        // ------------------------------------------------------------------ keys and tabs

        private void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            int digit = DigitOf(e.Key);
            if (e.Key == Key.Tab) { SelectTab(tab + (shift ? -1 : 1)); e.Handled = true; }
            else if (ctrl && digit >= 1 && digit <= TabCount) { SelectTab(digit - 1); e.Handled = true; }
            else if (e.Key == Key.Down) { Select(selected + 1); e.Handled = true; }
            else if (e.Key == Key.Up) { Select(selected - 1); e.Handled = true; }
            else if (e.Key == Key.PageDown) { Select(selected + 7); e.Handled = true; }
            else if (e.Key == Key.PageUp) { Select(selected - 7); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (selected >= 0 && selected < current.Count) RunResult(current[selected], ctrl);
            }
        }

        private static int DigitOf(Key k)
        {
            if (k >= Key.D0 && k <= Key.D9) return k - Key.D0;
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return k - Key.NumPad0;
            return -1;
        }

        private void SelectTab(int i)
        {
            i = ((i % TabCount) + TabCount) % TabCount;
            if (i == tab) return;
            int dir = i > tab ? 1 : -1;
            tab = i;
            TabLook(true);
            MovePill(true);
            Search(dir);
            Keyboard.Focus(box);
        }

        private void TabLook(bool animate)
        {
            for (int k = 0; k < tabs.Count; k++)
            {
                double to = k == tab ? 1 : DimTab;
                if (animate) tabs[k].BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(LabelFadeMs)));
                else { tabs[k].BeginAnimation(OpacityProperty, null); tabs[k].Opacity = to; }
            }
            placeholder.Text = tab == AppsTab ? "Spotlight Search" : "Search " + TabNames[tab];
            hint.Text = Hints[tab];
        }

        private bool PillTarget(out double x, out double w)
        {
            x = 0;
            for (int k = 0; k < tab; k++) x += tabs[k].ActualWidth;
            w = tabs[tab].ActualWidth;
            return w > 0;
        }

        /// <summary>Jump the pill under the current tab (first layout, or reopening).</summary>
        private void SnapPill()
        {
            double x, w;
            if (!PillTarget(out x, out w)) return;
            pillMove.BeginAnimation(TranslateTransform.XProperty, null);
            pill.BeginAnimation(WidthProperty, null);
            pillMove.X = x;
            pill.Width = w;
        }

        /// <summary>Spring the pill to the current tab, starting from wherever it is now (even mid-slide).</summary>
        private void MovePill(bool animate)
        {
            double x, w;
            if (!PillTarget(out x, out w)) return;
            if (!animate) { SnapPill(); return; }
            var ease = new SpringEase(0.72, 12);
            var d = TimeSpan.FromMilliseconds(PillMs);
            pillMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, d) { EasingFunction = ease });
            pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        }

        // ------------------------------------------------------------------ results

        /// <summary>Rebuild the current tab's list in place (new data arrived), keeping the selection.</summary>
        private void Refresh()
        {
            int keep = selected;
            Search(0);
            if (keep > 0) Select(keep);
        }

        /// <param name="dir">0 swaps the list instantly; 1 / -1 slides it in from the right / left.</param>
        private void Search(int dir)
        {
            string q = box.Text.Trim();
            string header = null, empty = null;
            List<Result> list;
            try
            {
                switch (tab)
                {
                    case FilesTab: list = FileResults(q, out header, out empty); break;
                    case ActionsTab: list = ActionResults(q, out header, out empty); break;
                    case ClipboardTab: list = ClipboardResults(q, out header, out empty); break;
                    default: list = AppResults(q, out header, out empty); break;
                }
            }
            catch (Exception ex) { Program.Log(ex); list = new List<Result>(); }
            ShowList(list, header, empty, dir);
        }

        private List<Result> AppResults(string q, out string header, out string empty)
        {
            header = null;
            empty = AppCatalog.Loaded ? "No Results" : "Loading apps…";
            var list = new List<Result>();
            if (q.Length == 0)
            {
                var visible = AppCatalog.All.Where(a => !LaunchpadPrefs.Hidden.Contains(a.Id));
                var used = visible.Select(a => new { a, n = UseCount(a) }).Where(x => x.n > 0)
                    .OrderByDescending(x => x.n).ThenBy(x => AppCatalog.DisplayName(x.a), StringComparer.OrdinalIgnoreCase)
                    .Take(FrequentApps).Select(x => x.a).ToList();
                header = "Frequently Used";
                if (used.Count == 0)
                {
                    // Nothing launched yet: show the start of Launchpad's first page instead.
                    List<AppEntry> pinned, rest;
                    AppCatalog.Arrange(out pinned, out rest);
                    used = pinned.Concat(rest).Take(FrequentApps).ToList();
                    header = "Applications";
                }
                foreach (var a in used) list.Add(AppResult(a));
                return list;
            }

            string calc = TryCalc(q);
            if (calc != null)
                list.Add(new Result { Title = calc, Subtitle = "Calculator — press Enter to copy", IconGlyph = "", Run = () => CopyText(calc) });

            var ranked = AppCatalog.All
                .Where(a => !LaunchpadPrefs.Hidden.Contains(a.Id))
                .Select(a => new { a, s = AppCatalog.Score(AppCatalog.DisplayName(a), q) })
                .Where(x => x.s > 0)
                .OrderByDescending(x => x.s).ThenBy(x => x.a.Name.Length)
                .Take(MaxApps);
            foreach (var x in ranked) list.Add(AppResult(x.a));
            list.Add(new Result
            {
                Title = "Search the web for “" + q + "”", Subtitle = "Web", IconGlyph = "",
                Run = () => Launcher.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q))
            });
            return list;
        }

        private static int UseCount(AppEntry a)
        {
            int n;
            LaunchpadPrefs.Uses.TryGetValue(a.Id, out n);
            return n;
        }

        private static Result AppResult(AppEntry a)
        {
            return new Result
            {
                Title = AppCatalog.DisplayName(a), Subtitle = "Application",
                Icon = IconStyler.Get(a.Key, 64),
                Run = () => AppCatalog.Launch(a)
            };
        }

        private List<Result> FileResults(string q, out string header, out string empty)
        {
            header = null;
            List<FileEntry> found;
            if (q.Length == 0)
            {
                header = "Recent Files";
                found = FileIndex.Recent.Take(RecentFiles).ToList();
                if (found.Count == 0)
                {
                    // Windows' recent items are switched off (or empty): show what changed last instead.
                    header = "Recently Modified";
                    found = FileIndex.Latest.Take(RecentFiles).ToList();
                }
            }
            else found = FileIndex.Search(q, MaxFiles);
            empty = !FileIndex.Ready ? "Indexing your files…"
                  : q.Length == 0 ? "No recent files"
                  : "No files match “" + q + "”";
            var list = new List<Result>();
            foreach (var f in found)
            {
                string path = f.Path;
                list.Add(new Result
                {
                    Title = f.Name, Subtitle = FolderLabel(path),
                    IconGlyph = f.IsDir ? "" : "",
                    LoadIcon = () => IconLoader.Get(path, 64),
                    Run = () => Launcher.Open(path),
                    Reveal = () => Reveal(path)
                });
            }
            return list;
        }

        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>"Documents › Projects" for files under the user's folder, the plain path otherwise.</summary>
        private static string FolderLabel(string path)
        {
            string dir;
            try { dir = Path.GetDirectoryName(path); } catch { return path; }
            if (string.IsNullOrEmpty(dir)) return path;
            if (!string.IsNullOrEmpty(Home) && dir.StartsWith(Home + "\\", StringComparison.OrdinalIgnoreCase))
                return dir.Substring(Home.Length + 1).Replace("\\", " › ");
            if (string.Equals(dir, Home, StringComparison.OrdinalIgnoreCase)) return "Home";
            return dir;
        }

        private static void Reveal(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception ex) { Program.Log(ex); }
        }

        private List<Result> ActionResults(string q, out string header, out string empty)
        {
            header = null;
            empty = "No actions match “" + q + "”";
            var scored = new List<KeyValuePair<SpotAction, int>>();
            foreach (var a in Actions())
            {
                int s = q.Length == 0 ? 1 : Math.Max(AppCatalog.Score(a.Title, q), WordScore(a.Words, q));
                if (s > 0) scored.Add(new KeyValuePair<SpotAction, int>(a, s));
            }
            // OrderBy is stable, so equal scores keep the list's own order.
            return scored.OrderByDescending(x => x.Value).Select(x =>
            {
                var a = x.Key;
                return new Result { Title = a.Title, Subtitle = a.Subtitle, IconGlyph = a.Glyph, Run = a.Run };
            }).ToList();
        }

        private static int WordScore(string words, string q)
        {
            if (string.IsNullOrEmpty(words)) return 0;
            foreach (var w in words.Split(' '))
                if (w.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 40;
            return words.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? 20 : 0;
        }

        private static SpotAction Act(string title, string subtitle, string glyph, string words, Action run)
        {
            return new SpotAction { Title = title, Subtitle = subtitle, Glyph = glyph, Words = words, Run = run };
        }

        /// <summary>Open after Spotlight has faded, so the next panel's glass doesn't photograph it.</summary>
        private static Action AfterClose(Action a)
        {
            return () => Launcher.Later(CloseFirstMs, () => { try { a(); } catch (Exception ex) { Program.Log(ex); } });
        }

        /// <summary>
        /// Everything here reuses what the menu bar, hot corners and Control Center already call.
        /// Anything that asks for confirmation runs at once, while Mac Look still has the foreground,
        /// so its message box isn't hidden behind another app.
        /// </summary>
        private static List<SpotAction> Actions()
        {
            return new List<SpotAction>
            {
                Act("Lock Screen", "System", "", "lock computer away password", Power.Lock),
                Act("Sleep", "System", "", "suspend standby moon", AfterClose(Power.Sleep)),
                Act("Restart", "System", "", "reboot reset", Power.Restart),
                Act("Shut Down", "System", "", "power off turn off shutdown", Power.ShutDown),
                Act("Log Out", "System", "", "sign out logoff logout user", Power.LogOut),
                Act("Mission Control", "Mac Look", "", "windows overview task view expose", AfterClose(() => Program.MissionControl.Toggle())),
                Act("Launchpad", "Mac Look", "", "apps all applications grid", AfterClose(() => Program.Launchpad.Toggle())),
                Act("Control Center", "Mac Look", "", "wifi bluetooth volume brightness controls", AfterClose(() => Program.ControlCenter.Toggle())),
                Act("Notification Center", "Mac Look", "", "notifications widgets calendar", AfterClose(() => Program.NotificationCenter.Toggle())),
                Act("System Settings", "Settings", "", "preferences control panel options", AfterClose(() =>
                {
                    if (Settings.MacSettingsForWindows) Program.SettingsWin.Open("General"); else Launcher.Open("ms-settings:");
                })),
                Act("Mac Look Settings", "Settings", "", "preferences options dock menu bar", AfterClose(() => Program.SettingsWin.Open("General"))),
                Act("Screenshot", "Select an area of the screen", "", "capture snip screen grab print", () => Launcher.Later(ScreenshotDelayMs, Screenshots.Region)),
                Act("Screenshot Whole Screen", "Saved to the Desktop", "", "capture screen grab print full", () => Launcher.Later(ScreenshotDelayMs, Screenshots.FullScreen)),
                Act("Force Quit", "Mac Look", "", "kill close app not responding task manager end", AfterClose(() => Program.ForceQuitWin.Toggle())),
                Act("Toggle Dark Mode", Theme.Dark ? "Switch to Light Mode" : "Switch to Dark Mode", "", "dark light theme appearance night",
                    () => ThemeSwitch.SetDark(!Theme.Dark)),
                Act("Empty Recycle Bin", "Asks first", "", "trash delete erase bin", EmptyRecycleBin),
                Act("Show Desktop", "Mac Look", "", "hide windows minimize all", AfterClose(() =>
                    Screenshots.AfterModifiersReleased(() => Native.PressKeys(Native.VK_WIN, (byte)'D'))))
            };
        }

        private static void EmptyRecycleBin()
        {
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
            long n = Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : -1;
            if (n == 0)
            {
                MessageBox.Show("The Recycle Bin is already empty.", "Mac Look", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string what = n > 0 ? n + (n == 1 ? " item" : " items") : "the items";
            if (MessageBox.Show("Are you sure you want to permanently erase " + what + " in the Recycle Bin?\n\nYou can't undo this action.",
                    "Mac Look", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            Native.SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION);   // we already asked
        }

        private List<Result> ClipboardResults(string q, out string header, out string empty)
        {
            header = null;
            var list = new List<Result>();
            if (!Settings.ClipboardHistory)
            {
                empty = "Clipboard history is off. Turn it on in Mac Look Settings.";
                return list;
            }
            var items = ClipboardHistory.Items;
            empty = items.Count == 0 ? "Nothing copied yet. Text you copy shows up here." : "Nothing copied matches “" + q + "”";
            if (q.Length == 0 && items.Count > 0) header = "Recently Copied";
            foreach (var text in items)
            {
                if (q.Length > 0 && text.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string copy = text;
                list.Add(new Result { Title = FirstLine(text), Subtitle = Describe(text), IconGlyph = "", Run = () => CopyText(copy) });
            }
            if (items.Count > 0)
                list.Add(new Result
                {
                    Title = "Clear History", Subtitle = "Forget everything copied so far", IconGlyph = "", KeepOpen = true,
                    Run = ClipboardHistory.Clear   // Changed then redraws this tab
                });
            return list;
        }

        private const int MaxTitleChars = 200;

        private static string FirstLine(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                string t = line.Replace('\t', ' ').Trim();
                if (t.Length > 0) return t.Length > MaxTitleChars ? t.Substring(0, MaxTitleChars) : t;
            }
            return text.Trim();
        }

        private static string Describe(string text)
        {
            int lines = text.TrimEnd('\r', '\n').Split('\n').Length;
            return lines + (lines == 1 ? " line" : " lines") + " · " + text.Length.ToString("N0", CultureInfo.CurrentCulture) + (text.Length == 1 ? " char" : " chars");
        }

        /// <summary>The clipboard can be briefly locked by another app; don't let that throw.</summary>
        private static void CopyText(string text)
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { Program.Log(ex); }
        }

        private const int MaxCalcLength = 200;   // deep nesting can overflow DataTable's recursive parser

        private static string TryCalc(string q)
        {
            if (q.Length > MaxCalcLength) return null;
            if (!Regex.IsMatch(q, @"^[\d\s\.\+\-\*/\(\)%]+$") || !Regex.IsMatch(q, @"\d\s*[\+\-\*/%]")) return null;
            try
            {
                // Write every whole number as a decimal ("7" -> "7.0") so DataTable does floating-point math; ".5" is left alone.
                string expr = Regex.Replace(q, @"\d*\.\d+|\d+", m => m.Value.Contains(".") ? m.Value : m.Value + ".0");
                object v = new System.Data.DataTable().Compute(expr, null);
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) return null;
                return d.ToString("G12", CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ list view

        private void ShowList(List<Result> list, string header, string empty, int dir)
        {
            current = list;
            int gen = ++iconGen;
            rows = new List<Border>();
            var panel = new StackPanel();
            if (header != null && list.Count > 0) panel.Children.Add(Header(header));
            for (int i = 0; i < list.Count; i++)
            {
                var row = Row(list[i], i);
                rows.Add(row);
                panel.Children.Add(row);
            }
            if (list.Count == 0 && empty != null) panel.Children.Add(EmptyState(empty));
            var next = new ScrollViewer
            {
                Content = panel, Focusable = false, CanContentScroll = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var old = page;
            page = next;
            listHost.Children.Add(next);
            if (old != null)
            {
                if (dir == 0) listHost.Children.Remove(old);
                else SlideOut(old, dir);
            }
            if (dir != 0) SlideIn(next, dir);
            selected = -1;
            Select(0);
            if (list.Any(r => r.LoadIcon != null)) LoadIconsLater(gen);
        }

        private static void SlideIn(FrameworkElement el, int dir)
        {
            var move = new TranslateTransform(dir * SlideBy, 0);
            el.RenderTransform = move;
            var d = TimeSpan.FromMilliseconds(SlideInMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * SlideBy, 0, d) { EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        }

        /// <summary>The old list leaves the way the pill is travelling, then removes itself.</summary>
        private void SlideOut(FrameworkElement el, int dir)
        {
            el.IsHitTestVisible = false;
            var move = el.RenderTransform as TranslateTransform;
            if (move == null || move.IsFrozen) { move = new TranslateTransform(); el.RenderTransform = move; }
            var d = TimeSpan.FromMilliseconds(SlideOutMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(0, d) { EasingFunction = ease };
            fade.Completed += (s, e) => listHost.Children.Remove(el);
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dir * SlideBy, d) { EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, fade);
        }

        private static TextBlock Header(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Opacity = 0.55, Margin = new Thickness(8, 2, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SpotFg");
            return t;
        }

        private static FrameworkElement EmptyState(string text)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = 14, Opacity = 0.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40, 0, 40, 0)
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SpotFg");
            return new Grid { Height = ListHeight, Children = { t } };
        }

        private Border Row(Result r, int index)
        {
            var row = new Border { Height = RowHeight, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 4, 8, 4), Background = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var holder = new Border
            {
                Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = r.Icon != null ? Picture(r.Icon) : (UIElement)Glyph(r.IconGlyph)
            };
            row.Tag = holder;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = r.Title, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(r.Subtitle))
                text.Children.Add(new TextBlock { Text = r.Subtitle, FontSize = 11.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            g.Children.Add(holder);
            g.Children.Add(text);
            row.Child = g;
            // Only a real mouse move selects: a list sliding in under a still pointer must not steal the selection.
            row.MouseMove += (s, e) =>
            {
                var p = e.GetPosition(this);
                if (p == lastMouse) return;
                lastMouse = p;
                if (selected != index) Select(index, false);
            };
            row.MouseLeftButtonUp += (s, e) => RunResult(r, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            return row;
        }

        private static Image Picture(ImageSource src)
        {
            var img = new Image { Source = src, Width = 30, Height = 30 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        private static TextBlock Glyph(string glyph)
        {
            return new TextBlock
            {
                Text = glyph ?? "", FontFamily = new FontFamily(Theme.IconFont), FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>Shell icons (file thumbnails especially) can be slow: fill them in a few at a time between frames.</summary>
        private void LoadIconsLater(int gen)
        {
            int i = 0;
            Action step = null;
            step = () =>
            {
                try
                {
                    if (gen != iconGen) return;   // the list was replaced
                    var sw = Stopwatch.StartNew();
                    while (i < current.Count && sw.ElapsedMilliseconds < IconBudgetMs)
                    {
                        var r = current[i];
                        var row = rows[i];
                        i++;
                        if (r.LoadIcon == null) continue;
                        var img = r.LoadIcon();
                        if (img != null) ((Border)row.Tag).Child = Picture(img);
                    }
                    if (i < current.Count) Dispatcher.BeginInvoke(DispatcherPriority.Background, step);
                }
                catch (Exception ex) { Program.Log(ex); }
            };
            Dispatcher.BeginInvoke(DispatcherPriority.Background, step);
        }

        private void Select(int i, bool scroll = true)
        {
            if (current.Count == 0) { selected = -1; return; }
            selected = Math.Max(0, Math.Min(current.Count - 1, i));
            for (int k = 0; k < rows.Count; k++)
            {
                var b = rows[k];
                if (k == selected)
                {
                    b.SetResourceReference(Border.BackgroundProperty, "Accent");
                    b.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                }
                else
                {
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(TextBlock.ForegroundProperty, "SpotFg");
                }
            }
            if (!scroll || page == null) return;
            if (selected == 0) page.ScrollToTop();   // keeps the section header in view too
            else rows[selected].BringIntoView();
        }

        private void RunResult(Result r, bool alternate)
        {
            Action a = alternate && r.Reveal != null ? r.Reveal : r.Run;
            if (a == null) return;
            if (!r.KeepOpen) HidePanel();
            try { a(); }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    /// <summary>
    /// A damped spring as an easing curve: a quick start, a small overshoot, then it settles exactly on 1.
    /// Damping below 1 overshoots (lower is bouncier); stiffness is how many radians the spring turns over the animation.
    /// </summary>
    internal class SpringEase : EasingFunctionBase
    {
        private readonly double damping, stiffness;

        public SpringEase() : this(0.7, 12) { }

        public SpringEase(double damping, double stiffness)
        {
            this.damping = Math.Max(0.05, Math.Min(0.99, damping));
            this.stiffness = stiffness;
            EasingMode = EasingMode.EaseIn;   // the curve below is already the whole motion; don't let WPF mirror it
        }

        protected override double EaseInCore(double t)
        {
            if (t >= 1) return 1;
            if (t <= 0) return 0;
            double z = damping, w = stiffness;
            double wd = w * Math.Sqrt(1 - z * z);
            return 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
        }

        protected override Freezable CreateInstanceCore() { return new SpringEase(damping, stiffness); }
    }
}
