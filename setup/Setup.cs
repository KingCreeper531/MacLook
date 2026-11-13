using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacLook;
using Path = System.IO.Path;

[assembly: AssemblyTitle("Mac Look Setup")]
[assembly: AssemblyDescription("Installs Mac Look, a macOS-style desktop for Windows.")]
[assembly: AssemblyCompany("Mac Look (github.com/KingCreeper531/MacLook)")]
[assembly: AssemblyProduct("Mac Look")]
[assembly: AssemblyCopyright("Copyright (c) 2026 KingCreeper531")]
[assembly: AssemblyVersion("2.5.0.0")]
[assembly: AssemblyFileVersion("2.5.0.0")]

namespace MacLookSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var app = new Application();
            var w = new SetupWindow();
            if (Array.IndexOf(args, "--silent") >= 0)
            {
                // Unattended install with the default options: MacLookSetup.exe --silent [--no-launch]
                return w.InstallSilently(Array.IndexOf(args, "--no-launch") < 0) ? 0 : 1;
            }
            app.Run(w);
            return 0;
        }
    }

    internal class SetupWindow : Window
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x34, 0x78, 0xF6));
        private readonly StackPanel options = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        private readonly TextBlock status = new TextBlock { Foreground = Brushes.White, Opacity = 0.7, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly Border button;
        private readonly TextBlock buttonText;
        private bool desktop = true, startMenu = true, login = false, launch = true, done;
        private static readonly string SourceExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MacLook.exe");

        private static string AppVersion
        {
            get
            {
                try { var v = FileVersionInfo.GetVersionInfo(SourceExe); return v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart; }
                catch
                {
                    // MacLook.exe missing / unreadable: setup is always built together with it, so use our own version.
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v.Major + "." + v.Minor + "." + v.Build;
                }
            }
        }

        public SetupWindow()
        {
            Title = "Mac Look Setup";
            Width = 480; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            Icon = AppIcon.Render(64);

            var card = new Border { CornerRadius = new CornerRadius(20), Margin = new Thickness(24), Padding = new Thickness(30, 18, 30, 24), ClipToBounds = true };
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.4, 1) };
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x1B, 0x1C, 0x4A), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x2A, 0x6E), 0.6));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x6A, 0x34, 0x6E), 1));
            card.Background = bg;
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            card.BorderThickness = new Thickness(1);
            card.Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.45 };

            var st = new StackPanel();
            st.Children.Add(Lights());
            var icon = new Image { Source = AppIcon.Render(256), Width = 112, Height = 112, Margin = new Thickness(0, 6, 0, 10) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            st.Children.Add(icon);
            st.Children.Add(new TextBlock { Text = "Mac Look", Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            st.Children.Add(new TextBlock { Text = "Version " + AppVersion, Foreground = Brushes.White, Opacity = 0.55, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 8) });
            st.Children.Add(new TextBlock
            {
                Text = "A macOS-style desktop for Windows, in liquid glass:\nmenu bar, Dock, Launchpad, Mission Control and more.",
                Foreground = Brushes.White, Opacity = 0.8, FontSize = 13.5, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            });

            options.Children.Add(Option("Add to Desktop", desktop, v => desktop = v));
            options.Children.Add(Option("Add to Start menu", startMenu, v => startMenu = v));
            options.Children.Add(Option("Open Mac Look when I sign in", login, v => login = v));
            options.Children.Add(Option("Open Mac Look when setup finishes", launch, v => launch = v));
            var optCard = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 22, 0, 0), Child = options };
            options.Margin = new Thickness(0);
            st.Children.Add(optCard);

            buttonText = new TextBlock { Text = InstallInfo.IsInstalled ? "Update" : "Install", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            button = new Border { Background = Accent, CornerRadius = new CornerRadius(12), Padding = new Thickness(0, 10, 0, 11), Margin = new Thickness(0, 22, 0, 0), Child = buttonText, Cursor = Cursors.Hand };
            button.MouseLeftButtonDown += (s, e) => { button.Opacity = 0.8; e.Handled = true; };
            button.MouseLeave += (s, e) => button.Opacity = 1;
            button.MouseLeftButtonUp += (s, e) => { button.Opacity = 1; e.Handled = true; OnButton(); };
            st.Children.Add(button);

            status.Text = "Installs to " + InstallInfo.Dir.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%UserProfile%") + "\nNo admin rights needed. Uninstall any time from Settings › Apps.";
            st.Children.Add(status);

            card.Child = st;
            Content = card;
            card.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            Opacity = 0;
            Loaded += (s, e) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        private UIElement Lights()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Light("#FF5F57", Close));
            row.Children.Add(Light("#FEBC2E", () => WindowState = WindowState.Minimized));
            row.Children.Add(Light("#4A4A55", null));
            return row;
        }

        private static UIElement Light(string color, Action a)
        {
            var e = new Ellipse { Width = 13, Height = 13, Margin = new Thickness(0, 0, 8, 0), Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) };
            if (a != null)
            {
                e.Cursor = Cursors.Hand;
                e.MouseLeftButtonDown += (s, ev) => ev.Handled = true;
                e.MouseLeftButtonUp += (s, ev) => { ev.Handled = true; a(); };
            }
            return e;
        }

        private UIElement Option(string text, bool value, Action<bool> set)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 7, 0, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            var pos = new TranslateTransform(value ? 16 : 0, 0);
            knob.RenderTransform = pos;
            var track = new Border { Width = 38, Height = 22, CornerRadius = new CornerRadius(11), Child = knob, Background = value ? Accent : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) };
            DockPanel.SetDock(track, Dock.Right);
            dp.Children.Add(track);
            dp.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            bool on = value;
            dp.MouseLeftButtonUp += (s, e) =>
            {
                if (done) return;
                on = !on;
                set(on);
                track.Background = on ? Accent : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                pos.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 16 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
            };
            return dp;
        }

        public bool InstallSilently(bool launchAfter)
        {
            launch = launchAfter;
            Install();
            return done;
        }

        private void OnButton()
        {
            if (done) { Close(); return; }
            buttonText.Text = "Installing…";
            button.IsHitTestVisible = false;
            Dispatcher.BeginInvoke(new Action(Install), DispatcherPriority.Background);
        }

        private void Install()
        {
            try
            {
                bool wasRunning = Process.GetProcessesByName("MacLook").Length > 0;
                InstallInfo.StopRunning();
                Directory.CreateDirectory(InstallInfo.Dir);
                // Mac Look ships next to this installer (in the same zip / folder) rather than inside it.
                if (!File.Exists(SourceExe))
                    throw new Exception("MacLook.exe wasn't found next to this installer. Extract the whole zip first, then run MacLookSetup.exe again.");
                if (!string.Equals(Path.GetFullPath(SourceExe), Path.GetFullPath(InstallInfo.Exe), StringComparison.OrdinalIgnoreCase))
                    CopyWithRetry(SourceExe, InstallInfo.Exe);
                if (wasRunning)
                {
                    // We stopped a running copy; put the Windows taskbar back before relaunching.
                    using (var p = Process.Start(InstallInfo.Exe, "--restore"))
                        if (p != null) p.WaitForExit(RestoreTimeoutMs);
                }

                // Mac Look itself is in place now: a shortcut that can't be made (e.g. a redirected or
                // locked Desktop folder) is reported, but doesn't undo or block the install.
                string warning = "";
                if (!Shortcut(desktop, InstallInfo.DesktopLink)) warning += "\nCouldn't update the Desktop shortcut.";
                if (!Shortcut(startMenu, InstallInfo.StartLink)) warning += "\nCouldn't update the Start menu shortcut.";
                InstallInfo.SetRunAtLogin(login);
                InstallInfo.Register(AppVersion, new FileInfo(InstallInfo.Exe).Length / 1024);

                if (launch) using (Process.Start(InstallInfo.Exe)) { }
                done = true;
                buttonText.Text = "Done";
                button.IsHitTestVisible = true;
                button.Background = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                options.Opacity = 0.5;
                status.Text = "Mac Look is installed" + (launch ? " and starting now." : ". Open it from the Start menu or your Desktop.") +
                              "\nIt keeps itself up to date automatically." + warning;
            }
            catch (Exception ex)
            {
                buttonText.Text = "Try Again";
                button.IsHitTestVisible = true;
                status.Text = "Setup couldn't finish: " + ex.Message;
            }
        }

        private const int RestoreTimeoutMs = 10000, CopyAttempts = 10, CopyRetryMs = 300;

        /// <summary>
        /// The copy we just stopped can keep MacLook.exe locked for a moment after it exits (and antivirus
        /// scanners often open new files too), so retry briefly before giving up.
        /// </summary>
        private static void CopyWithRetry(string from, string to)
        {
            for (int i = 1; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (i >= CopyAttempts) throw; }
                catch (UnauthorizedAccessException) { if (i >= CopyAttempts) throw; }
                System.Threading.Thread.Sleep(CopyRetryMs);
            }
        }

        /// <summary>Creates (want) or removes (!want) a shortcut to Mac Look; false if that failed.</summary>
        private static bool Shortcut(bool want, string link)
        {
            try
            {
                if (want) InstallInfo.CreateShortcut(link, InstallInfo.Exe);
                else if (File.Exists(link)) File.Delete(link);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
