using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MacLook
{
    /// <summary>A glass drop-down that hangs under its menu bar icon.</summary>
    internal class StatusMenu : GlassPanel
    {
        protected readonly StackPanel body = new StackPanel { Width = 290 };
        private double anchorRight = SystemParameters.PrimaryScreenWidth - 160;

        public StatusMenu()
        {
            var card = GlassCard(GlassStyles.Panel, 16);
            card.Padding = new Thickness(6, 8, 6, 8);
            card.Child = body;
            var outer = WithShadow(card, 16);
            outer.Margin = new Thickness(14, 4, 16, 28);
            Content = outer;
        }

        public void ToggleFrom(FrameworkElement anchor)
        {
            try
            {
                var src = PresentationSource.FromVisual(anchor);
                double s = src == null ? 1 : src.CompositionTarget.TransformToDevice.M11;
                var p = anchor.PointToScreen(new Point(anchor.ActualWidth, 0));
                anchorRight = p.X / s;
            }
            catch { anchorRight = SystemParameters.PrimaryScreenWidth - 10; }
            Toggle();
        }

        protected override void Place()
        {
            Left = Math.Min(SystemParameters.PrimaryScreenWidth - ActualWidth + 6, anchorRight - ActualWidth + 30);
            Top = MenuBarWindow.BarHeight - 2;
        }

        // ---- rows ----
        protected static TextBlock Header(string text)
        {
            return new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Opacity = 0.55, Margin = new Thickness(10, 6, 10, 4) };
        }

        protected static Border Sep()
        {
            var b = new Border { Height = 1, Margin = new Thickness(10, 6, 10, 6) };
            b.SetResourceReference(Border.BackgroundProperty, "MenuSep");
            return b;
        }

        protected Border Row(UIElement left, string text, string right, Action click, bool bold = false)
        {
            var dp = new DockPanel();
            if (right != null)
            {
                var r = new TextBlock { Text = right, FontFamily = new FontFamily(Theme.IconFont), FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(r, Dock.Right);
                dp.Children.Add(r);
            }
            if (left != null) dp.Children.Add(left);
            dp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 6), Child = dp, Background = Brushes.Transparent };
            if (click != null)
            {
                b.MouseEnter += (s, e) => { b.SetResourceReference(Border.BackgroundProperty, "Accent"); dp.SetValue(TextBlock.ForegroundProperty, Brushes.White); };
                b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; dp.ClearValue(TextBlock.ForegroundProperty); };
                b.MouseLeftButtonDown += (s, e) => e.Handled = true;
                b.MouseLeftButtonUp += (s, e) => { e.Handled = true; HidePanel(); click(); };
            }
            return b;
        }

        protected static Border Bubble(string glyph, bool active)
        {
            var t = new TextBlock { Text = glyph, FontFamily = new FontFamily(Theme.IconFont), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (active) t.Foreground = Brushes.White;
            var b = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0), Child = t };
            b.SetResourceReference(Border.BackgroundProperty, active ? "Accent" : "Inactive");
            return b;
        }

        protected static string Run(string exe, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                });
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return o;
            }
            catch { return ""; }
        }
    }

    internal class WifiMenuWindow : StatusMenu
    {
        private class Net { public string Ssid; public int Signal; public bool Secure; }

        public WifiMenuWindow() { Title = "Wi-Fi"; }

        protected override void OnOpening()
        {
            body.Children.Clear();
            var eth = EthernetName();
            body.Children.Add(Header("Wi-Fi"));
            var status = new TextBlock { Text = "Looking for networks…", Opacity = 0.6, Margin = new Thickness(10, 2, 10, 6) };
            body.Children.Add(status);
            if (eth != null)
            {
                body.Children.Add(Sep());
                body.Children.Add(Header("Ethernet"));
                body.Children.Add(Row(Bubble("", true), eth, null, null, true));
            }
            body.Children.Add(Sep());
            body.Children.Add(Row(null, "Other Networks…", null, () => Launcher.Open("ms-availablenetworks:")));
            body.Children.Add(Row(null, "Network & Internet Settings…", null, () => Launcher.Open("ms-settings:network")));

            // netsh can take a moment, so scan off the UI thread and fill in the list when ready.
            var t = new Thread(() =>
            {
                string iface = Run("netsh", "wlan show interfaces");
                string nets = Run("netsh", "wlan show networks mode=bssid");
                string profiles = Run("netsh", "wlan show profiles");
                Dispatcher.BeginInvoke(new Action(() => Fill(status, iface, nets, profiles)));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Fill(TextBlock status, string iface, string nets, string profiles)
        {
            int at = body.Children.IndexOf(status);
            if (at < 0) return;
            body.Children.Remove(status);
            if (iface.IndexOf("There is no wireless interface", StringComparison.OrdinalIgnoreCase) >= 0 || iface.Trim().Length == 0)
            {
                body.Children.Insert(at, new TextBlock { Text = "Wi-Fi isn't available on this PC.", Opacity = 0.6, Margin = new Thickness(10, 2, 10, 6) });
                return;
            }
            string current = null;
            var m = Regex.Match(iface, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
            if (m.Success && Regex.IsMatch(iface, @"State\s*:\s*connected", RegexOptions.IgnoreCase)) current = m.Groups[1].Value.Trim();
            var known = new HashSet<string>(Regex.Matches(profiles, @"All User Profile\s*:\s*(.+)$", RegexOptions.Multiline).Cast<Match>().Select(x => x.Groups[1].Value.Trim()));

            var list = new List<Net>();
            Net cur = null;
            foreach (var raw in nets.Split('\n'))
            {
                string line = raw.Trim();
                var ms = Regex.Match(line, @"^SSID \d+\s*:\s*(.*)$");
                if (ms.Success) { cur = new Net { Ssid = ms.Groups[1].Value.Trim() }; if (cur.Ssid.Length > 0) list.Add(cur); continue; }
                if (cur == null) continue;
                var sig = Regex.Match(line, @"^Signal\s*:\s*(\d+)%");
                if (sig.Success) cur.Signal = Math.Max(cur.Signal, int.Parse(sig.Groups[1].Value));
                var auth = Regex.Match(line, @"^Authentication\s*:\s*(.+)$");
                if (auth.Success) cur.Secure = !auth.Groups[1].Value.Trim().Equals("Open", StringComparison.OrdinalIgnoreCase);
            }
            list = list.GroupBy(n => n.Ssid).Select(g => g.OrderByDescending(n => n.Signal).First()).OrderByDescending(n => n.Ssid == current).ThenByDescending(n => n.Signal).Take(10).ToList();

            if (current != null)
            {
                body.Children.Insert(at++, Row(Bubble("", true), current, null, null, true));
                body.Children.Insert(at++, Sep());
                body.Children.Insert(at++, Header("Other Networks"));
            }
            if (list.Count(n => n.Ssid != current) == 0)
                body.Children.Insert(at++, new TextBlock { Text = "No other networks found.", Opacity = 0.6, Margin = new Thickness(10, 2, 10, 6) });
            foreach (var n in list.Where(n => n.Ssid != current))
            {
                string ssid = n.Ssid;
                bool isKnown = known.Contains(ssid);
                var bubble = Bubble("", false);
                bubble.Child.Opacity = 0.35 + 0.65 * n.Signal / 100.0;
                body.Children.Insert(at++, Row(bubble, ssid, n.Secure ? "" : null, () =>
                {
                    // Known networks connect directly; new ones open Windows' own password prompt.
                    if (isKnown) Run("netsh", "wlan connect name=\"" + ssid.Replace("\"", "") + "\"");
                    else Launcher.Open("ms-availablenetworks:");
                }));
            }
        }

        private static string EthernetName()
        {
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                    if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                        && !n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
                        && n.Description.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) < 0
                        && n.Description.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) < 0)
                        return n.Name + " — Connected";
            }
            catch { }
            return null;
        }
    }

    internal class SoundMenuWindow : StatusMenu
    {
        private ElasticSlider slider;

        public SoundMenuWindow() { Title = "Sound"; }

        protected override void OnOpening()
        {
            body.Children.Clear();
            body.Children.Add(Header("Sound"));
            slider = new ElasticSlider("") { Margin = new Thickness(10, 2, 10, 8), Value = Audio.GetVolume() };
            slider.ValueChanged += v => { Audio.SetVolume((float)v); Glyph(); };
            slider.Icon.MouseLeftButtonUp += (s, e) => { Audio.SetMute(!Audio.GetMute()); Glyph(); e.Handled = true; };
            Glyph();
            body.Children.Add(slider);
            body.Children.Add(Sep());
            body.Children.Add(Header("Output"));
            body.Children.Add(Row(Bubble("", true), "Choose Output Device…", null, () => Launcher.Later(150, () => Native.PressKeys(Native.VK_WIN, Native.VK_CTRL, (byte)'V'))));
            body.Children.Add(Sep());
            body.Children.Add(Row(null, Audio.GetMute() ? "Unmute" : "Mute", null, () => Audio.SetMute(!Audio.GetMute())));
            body.Children.Add(Row(null, "Sound Settings…", null, () => Launcher.Open("ms-settings:sound")));
        }

        private void Glyph()
        {
            float v = Audio.GetVolume();
            slider.Icon.Text = Audio.GetMute() || v < 0.01f ? "" : v < 0.34f ? "" : v < 0.67f ? "" : "";
        }
    }
}
