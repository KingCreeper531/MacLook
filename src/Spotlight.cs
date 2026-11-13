using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacLook
{
    internal class SpotlightWindow : GlassPanel
    {
        private class Result { public string Title, Subtitle; public ImageSource Icon; public string IconGlyph; public Action Run; }

        private TextBox box;
        private TextBlock placeholder;
        private StackPanel results;
        private Border divider;
        private List<Result> current = new List<Result>();
        private int selected;

        public SpotlightWindow()
        {
            Title = "Spotlight";
            SizeToContent = SizeToContent.Height;
            Width = 740;
            Build();
            PreviewKeyDown += OnKey;
        }

        private void Build()
        {
            var card = GlassCard(GlassStyles.Panel, 28);
            card.Padding = new Thickness(16, 10, 16, 10);

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
            box.TextChanged += (s, e) => { placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Search(); };
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

            divider = new Border { Height = 1, Margin = new Thickness(-16, 10, -16, 6), Visibility = Visibility.Collapsed };
            divider.SetResourceReference(Border.BackgroundProperty, "MenuSep");
            results = new StackPanel();

            stack.Children.Add(row);
            stack.Children.Add(divider);
            stack.Children.Add(results);
            card.Child = stack;
            var outer = WithShadow(card, 28);
            outer.Margin = new Thickness(30, 10, 30, 40);
            Content = outer;
        }

        protected override void OnOpening()
        {
            box.Text = "";
            Search();
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
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down) { Select(selected + 1); e.Handled = true; }
            else if (e.Key == Key.Up) { Select(selected - 1); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (selected >= 0 && selected < current.Count) RunResult(current[selected]);
            }
        }

        private void RunResult(Result r)
        {
            HidePanel();
            try { r.Run(); }
            catch (Exception ex) { Program.Log(ex); }
        }

        private void Search()
        {
            string q = box.Text.Trim();
            current = new List<Result>();
            if (q.Length > 0)
            {
                string calc = TryCalc(q);
                if (calc != null)
                    current.Add(new Result { Title = calc, Subtitle = "Calculator — press Enter to copy", IconGlyph = "", Run = () => CopyText(calc) });

                var ranked = AppCatalog.All
                    .Where(a => !LaunchpadPrefs.Hidden.Contains(a.Id))
                    .Select(a => new { a, s = AppCatalog.Score(AppCatalog.DisplayName(a), q) })
                    .Where(x => x.s > 0)
                    .OrderByDescending(x => x.s).ThenBy(x => x.a.Name.Length)
                    .Take(7);
                foreach (var x in ranked)
                {
                    var a = x.a;
                    current.Add(new Result
                    {
                        Title = AppCatalog.DisplayName(a), Subtitle = "Application",
                        Icon = IconStyler.Get(a.Key, 64),
                        Run = () => AppCatalog.Launch(a)
                    });
                }
                current.Add(new Result
                {
                    Title = "Search the web for “" + q + "”", Subtitle = "Web", IconGlyph = "",
                    Run = () => Launcher.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q))
                });
            }
            Render();
            Select(0);
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

        private void Render()
        {
            results.Children.Clear();
            divider.Visibility = current.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < current.Count; i++)
            {
                var r = current[i];
                int index = i;
                var rowBorder = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                FrameworkElement icon;
                if (r.Icon != null)
                {
                    var img = new Image { Source = r.Icon, Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    icon = img;
                }
                else
                {
                    icon = new TextBlock
                    {
                        Text = r.IconGlyph ?? "", FontFamily = new FontFamily(Theme.IconFont), FontSize = 22,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
                    };
                }
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = r.Title, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = r.Subtitle, FontSize = 11.5, Opacity = 0.6 });
                Grid.SetColumn(text, 1);
                g.Children.Add(icon);
                g.Children.Add(text);
                rowBorder.Child = g;
                rowBorder.MouseEnter += (s, e) => Select(index);
                rowBorder.MouseLeftButtonUp += (s, e) => RunResult(r);
                results.Children.Add(rowBorder);
            }
        }

        private void Select(int i)
        {
            if (current.Count == 0) { selected = -1; return; }
            selected = Math.Max(0, Math.Min(current.Count - 1, i));
            for (int k = 0; k < results.Children.Count; k++)
            {
                var b = (Border)results.Children[k];
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
        }
    }
}
