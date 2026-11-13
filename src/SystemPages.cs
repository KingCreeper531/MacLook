using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacLook
{
    /// <summary>
    /// The Windows side of System Settings: Mac-style pages with real controls where Windows allows it,
    /// and a clear "Open in Windows Settings" for everything else.
    /// </summary>
    internal partial class SettingsWindow
    {
        private static Border Link(string text, string uri) { return Button(text, () => Launcher.Open(uri)); }
        private UIElement Jump(string text, string section) { return Button(text, () => { current = section; BuildSidebar(); BuildPage(); }); }

        private static TextBlock Muted(string text)
        {
            return new TextBlock { Text = text, Opacity = 0.6, FontSize = 12, Margin = new Thickness(6, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        }

        private void WindowsPage(string name)
        {
            switch (name)
            {
                case "Wi-Fi": WifiPage(); break;
                case "Bluetooth":
                    page.Children.Add(Group(
                        Row("Bluetooth", "Turn Bluetooth on or off and see your devices.", Link("Bluetooth Settings…", "ms-settings:bluetooth")),
                        Row("Add a device", "Pair headphones, a controller, a mouse and more.", Link("Add Device…", "ms-settings:connecteddevices"))));
                    break;
                case "Network": NetworkPage(); break;
                case "Notifications":
                    page.Children.Add(Group(
                        Row("Notification Center", "Your notifications and widgets.", Button("Open", () => Program.NotificationCenter.Toggle())),
                        Row("Do Not Disturb", "Silence notifications.", Link("Notification Settings…", "ms-settings:notifications")),
                        Row("Windows notification list", null, Button("Show", () => Native.PressKeys(Native.VK_WIN, (byte)'N')))));
                    break;
                case "Sound": SoundPage(); break;
                case "Focus":
                    page.Children.Add(Group(
                        Row("Do Not Disturb", "Mute notifications while you work or play.", Link("Focus Settings…", "ms-settings:quiethours")),
                        Row("Focus sessions", "Timed focus sessions in the Clock app.", Link("Open Clock…", "ms-clock:"))));
                    break;
                case "Accessibility":
                    page.Children.Add(Caption("Vision"));
                    page.Children.Add(Group(
                        Row("Text size", null, Link("Change…", "ms-settings:easeofaccess-display")),
                        Row("Zoom (Magnifier)", null, Link("Open…", "ms-settings:easeofaccess-magnifier")),
                        Row("Color filters", null, Link("Open…", "ms-settings:easeofaccess-colorfilter")),
                        Row("Narrator", null, Link("Open…", "ms-settings:easeofaccess-narrator")),
                        Row("Pointer size and color", null, Link("Open…", "ms-settings:easeofaccess-mousepointer"))));
                    page.Children.Add(Caption("Motion"));
                    page.Children.Add(Group(
                        Row("Animate windows", "Genie effect when minimizing and a fade when closing.", Toggle(Settings.AnimateWindows, v => Settings.AnimateWindows = v)),
                        Row("Dock magnification", null, Toggle(Settings.Magnify, v => Settings.Magnify = v))));
                    break;
                case "Displays": DisplaysPage(); break;
                case "Wallpaper": WallpaperPage(); break;
                case "Battery & Power": PowerPage(); break;
                case "Lock Screen":
                    page.Children.Add(Group(
                        Row("Lock now", "Ctrl+Alt+Del or Win+L also lock your PC.", Button("Lock Screen", Power.Lock)),
                        Row("Lock screen picture and widgets", null, Link("Lock Screen Settings…", "ms-settings:lockscreen")),
                        Row("Screen saver", null, Button("Screen Saver…", () => Launcher.Open("control.exe", "desk.cpl,,@screensaver"))),
                        Row("Sign-in options", "PIN, fingerprint, face and password.", Link("Open…", "ms-settings:signinoptions"))));
                    break;
                case "Privacy & Security":
                    page.Children.Add(Group(
                        Row("Windows Security", "Virus protection, firewall and device health.", Link("Open…", "windowsdefender:")),
                        Row("Location Services", null, Link("Open…", "ms-settings:privacy-location")),
                        Row("Camera", null, Link("Open…", "ms-settings:privacy-webcam")),
                        Row("Microphone", null, Link("Open…", "ms-settings:privacy-microphone")),
                        Row("All privacy settings", null, Link("Open…", "ms-settings:privacy"))));
                    break;
                case "Users":
                    page.Children.Add(Group(
                        Row(Environment.UserName, "Signed in on " + Environment.MachineName, Link("Your Info…", "ms-settings:yourinfo")),
                        Row("Other users", null, Link("Open…", "ms-settings:otherusers")),
                        Row("Log out", "Quit all apps and log out.", Button("Log Out…", Power.LogOut))));
                    break;
                case "Keyboard":
                    page.Children.Add(Group(
                        Row("Keyboard", "Repeat rate, layouts and more.", Link("Keyboard Settings…", "ms-settings:keyboard")),
                        Row("Input sources", "Languages and keyboard layouts.", Link("Open…", "ms-settings:regionlanguage")),
                        Row("Text input", "Autocorrect and suggestions.", Link("Open…", "ms-settings:typing")),
                        Row("Keyboard shortcuts", "Screenshots, Force Quit, window switcher.", Jump("Shortcuts…", "Hot Corners & Keys"))));
                    break;
                case "Mouse":
                    page.Children.Add(Group(
                        Row("Mouse", "Pointer speed, scrolling and buttons.", Link("Mouse Settings…", "ms-settings:mousetouchpad")),
                        Row("Trackpad", null, Link("Open…", "ms-settings:devices-touchpad")),
                        Row("Hot Corners", null, Jump("Hot Corners…", "Hot Corners & Keys"))));
                    break;
                case "Printers":
                    page.Children.Add(Group(Row("Printers & Scanners", "Add a printer or scanner and manage print jobs.", Link("Open…", "ms-settings:printers"))));
                    break;
                case "Storage": StoragePage(); break;
                case "Apps":
                    page.Children.Add(Group(
                        Row("Installed apps", "Uninstall or change apps.", Link("Open…", "ms-settings:appsfeatures")),
                        Row("Default apps", "Choose your browser, mail and more.", Link("Open…", "ms-settings:defaultapps")),
                        Row("Login Items", "Apps that open when you sign in.", Link("Open…", "ms-settings:startupapps")),
                        Row("Launchpad", "Arrange, pin and hide apps.", Jump("Launchpad…", "Launchpad"))));
                    break;
                case "Date & Time":
                    var now = DateTime.Now;
                    page.Children.Add(Group(
                        Row(now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture), now.ToString("h:mm tt", CultureInfo.InvariantCulture) + "  ·  " + TimeZoneInfo.Local.DisplayName, null),
                        Row("Set date and time", null, Link("Date & Time Settings…", "ms-settings:dateandtime")),
                        Row("Clock in the menu bar", null, Jump("Menu Bar…", "Menu Bar"))));
                    break;
                case "Software Update":
                    page.Children.Add(Caption("Mac Look"));
                    page.Children.Add(Group(
                        Row("Mac Look " + Updater.CurrentText, "Updates come from GitHub.", Button("Check for Updates", () => Updater.Check(true))),
                        Row("Install updates automatically", null, Toggle(Settings.AutoUpdate, v => Settings.AutoUpdate = v))));
                    page.Children.Add(Caption("Windows"));
                    page.Children.Add(Group(
                        Row("Windows Update", "Security fixes, drivers and feature updates.", Link("Check for Updates…", "ms-settings:windowsupdate-action")),
                        Row("Update history", null, Link("Open…", "ms-settings:windowsupdate-history"))));
                    break;
                default:
                    page.Children.Add(Muted("Nothing here yet."));
                    break;
            }
        }

        // ------------------------------------------------------------------ Wi-Fi / network
        private void WifiPage()
        {
            var status = new TextBlock { Text = "Checking…", Opacity = 0.6, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
            var title = new TextBlock { Text = "Wi-Fi" };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(title);
            info.Children.Add(status);
            var head = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
            var btn = Button("Show Networks…", () => Launcher.Open("ms-availablenetworks:"));
            DockPanel.SetDock(btn, Dock.Right);
            head.Children.Add(btn);
            head.Children.Add(info);
            page.Children.Add(Group(head,
                Row("Known networks", null, Link("Manage…", "ms-settings:network-wifisettings")),
                Row("Wi-Fi settings", null, Link("Open…", "ms-settings:network-wifi"))));

            var t = new Thread(() =>
            {
                string s = "Wi-Fi isn't available on this PC";
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", "wlan show interfaces") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    var m = Regex.Match(o, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                    if (m.Success && Regex.IsMatch(o, @"State\s*:\s*connected", RegexOptions.IgnoreCase)) s = "Connected to " + m.Groups[1].Value.Trim();
                    else if (o.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0) s = "Not connected";
                }
                catch { }
                Dispatcher.BeginInvoke(new Action(() => status.Text = s));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void NetworkPage()
        {
            var rows = new System.Collections.Generic.List<UIElement>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string ip = n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).FirstOrDefault();
                    string kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : n.NetworkInterfaceType.ToString();
                    rows.Add(Row(n.Name, kind + "  ·  Connected" + (ip != null ? "  ·  " + ip : ""), null));
                }
            }
            catch { }
            if (rows.Count == 0) rows.Add(Row("Not connected", null, null));
            page.Children.Add(Group(rows.ToArray()));
            page.Children.Add(Group(
                Row("Network & Internet", null, Link("Open…", "ms-settings:network")),
                Row("VPN", null, Link("Open…", "ms-settings:network-vpn")),
                Row("Proxy", null, Link("Open…", "ms-settings:network-proxy")),
                Row("Advanced network settings", null, Link("Open…", "ms-settings:network-advancedsettings"))));
        }

        // ------------------------------------------------------------------ sound
        private void SoundPage()
        {
            page.Children.Add(Group(
                Row("Output volume", null, Slider(0, 100, Math.Round(Audio.GetVolume() * 100), "0", v => Audio.SetVolume((float)(v / 100)))),
                Row("Mute", null, Toggle(Audio.GetMute(), v => Audio.SetMute(v)))));
            page.Children.Add(Group(
                Row("Output device", "Speakers, headphones and more.", Button("Choose…", () => Native.PressKeys(Native.VK_WIN, Native.VK_CTRL, (byte)'V'))),
                Row("Volume for each app", null, Link("Volume Mixer…", "ms-settings:apps-volume")),
                Row("Input (microphones)", null, Link("Open…", "ms-settings:sound")),
                Row("Show Sound in the menu bar", null, Toggle(Settings.ShowSound, v => Settings.ShowSound = v))));
        }

        // ------------------------------------------------------------------ displays
        private void DisplaysPage()
        {
            var rows = System.Windows.Forms.Screen.AllScreens.Select((s, i) =>
                Row("Display " + (i + 1) + (s.Primary ? " (main)" : ""), s.Bounds.Width + " × " + s.Bounds.Height + "  ·  " + s.DeviceName.TrimStart('\\', '.'), null)).ToArray();
            page.Children.Add(Group(rows));
            page.Children.Add(Group(
                Row("Night Shift", "Warmer colors at night (Night light).", Link("Open…", "ms-settings:nightlight")),
                Row("Resolution and scale", null, Link("Display Settings…", "ms-settings:display")),
                Row("Refresh rate and HDR", null, Link("Advanced…", "ms-settings:display-advanced"))));
        }

        // ------------------------------------------------------------------ wallpaper
        private void WallpaperPage()
        {
            var preview = new Border { CornerRadius = new CornerRadius(12), Height = 210, Margin = new Thickness(0, 0, 0, 14), ClipToBounds = true };
            preview.SetResourceReference(Border.BackgroundProperty, "ModuleBg");
            var img = CurrentWallpaper();
            if (img != null) preview.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            page.Children.Add(preview);
            page.Children.Add(Group(
                Row("Choose a picture", "Any photo on your PC.", Button("Choose…", () =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose wallpaper", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
                    if (dlg.ShowDialog(this) != true) return;
                    Wallpaper.SetImage(dlg.FileName);
                    Program.RecaptureDesktop();
                    Launcher.Later(600, BuildPage);
                })),
                Row("Mac-style wallpaper", "Original rolling hills at dusk, drawn by Mac Look.", Button("Use", () => { try { Wallpaper.SetMacStyle(); Program.RecaptureDesktop(); Launcher.Later(600, BuildPage); } catch (Exception ex) { Program.Log(ex); } })),
                Row("Restore your original wallpaper", null, Button("Restore", () => { Wallpaper.Restore(); Program.RecaptureDesktop(); Launcher.Later(600, BuildPage); })),
                Row("Slideshow, colors and more", null, Link("Open…", "ms-settings:personalization-background"))));
        }

        private static BitmapSource CurrentWallpaper()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (!File.Exists(path))
                {
                    var sb = new System.Text.StringBuilder(1024);
                    Native.SystemParametersInfoGet(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
                    path = sb.ToString();
                }
                if (!File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.DecodePixelWidth = 900;
                using (var fs = File.OpenRead(path)) { bi.StreamSource = fs; bi.EndInit(); }
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ power
        private void PowerPage()
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            {
                string state = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "Charging" : "On battery";
                page.Children.Add(Group(Row("Battery", Math.Round(ps.BatteryLifePercent * 100) + "%  ·  " + state, UsageBar(ps.BatteryLifePercent))));
            }
            page.Children.Add(Group(
                Row("Sleep now", null, Button("Sleep", Power.Sleep)),
                Row("Power mode, sleep and screen timeout", null, Link("Open…", "ms-settings:powersleep")),
                Row("Energy saver", null, Link("Open…", "ms-settings:batterysaver"))));
        }

        // ------------------------------------------------------------------ storage
        private void StoragePage()
        {
            var rows = new System.Collections.Generic.List<UIElement>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                    double used = 1 - d.AvailableFreeSpace / (double)d.TotalSize;
                    string label = string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                    rows.Add(Row(label + " (" + d.Name.TrimEnd('\\') + ")", Math.Round(d.AvailableFreeSpace / 1e9) + " GB available of " + Math.Round(d.TotalSize / 1e9) + " GB", UsageBar(used)));
                }
                catch { }
            }
            if (rows.Count > 0) page.Children.Add(Group(rows.ToArray()));
            page.Children.Add(Group(
                Row("Storage Sense", "Free up space automatically.", Link("Open…", "ms-settings:storagesense")),
                Row("Disk Cleanup", null, Button("Open…", () => Launcher.Open("cleanmgr.exe")))));
        }

        private static UIElement UsageBar(double fraction)
        {
            var g = new Grid { Width = 180, Height = 8 };
            var track = new Border { CornerRadius = new CornerRadius(4) };
            track.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            var fill = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(8, 180 * Math.Max(0, Math.Min(1, fraction))) };
            fill.SetResourceReference(Border.BackgroundProperty, "Accent");
            g.Children.Add(track);
            g.Children.Add(fill);
            return g;
        }
    }
}
