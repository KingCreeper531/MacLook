using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace MacLook
{
    internal static class Theme
    {
        public static bool Dark;
        public const string UiFont = "Segoe UI Variable Text, Segoe UI";
        public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

        public static readonly string[][] Accents =
        {
            new[] { "blue", "#3478F6" }, new[] { "purple", "#A550A7" }, new[] { "pink", "#F74F9E" }, new[] { "red", "#FF5257" },
            new[] { "orange", "#F7821B" }, new[] { "yellow", "#E8B100" }, new[] { "green", "#62BA46" }, new[] { "graphite", "#8C8C92" },
        };

        public static string AccentHex
        {
            get
            {
                foreach (var a in Accents) if (a[0] == Settings.Accent) return a[1];
                return Accents[0][1];
            }
        }

        public static void Load()
        {
            if (Settings.ThemeMode == "light") { Dark = false; return; }
            if (Settings.ThemeMode == "dark") { Dark = true; return; }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    Dark = v is int && (int)v == 0;
                }
            }
            catch (Exception ex) { Program.Log(ex); Dark = false; }
        }

        /// <summary>Menu bar tint behind the acrylic blur (0xAARRGGBB).</summary>
        public static uint BarTint { get { return Dark ? 0x8C1E1E1Eu : 0x99EDEDF0u; } }

        private static SolidColorBrush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static void Apply(ResourceDictionary r)
        {
            if (Dark)
            {
                r["BarFg"] = B("#F5F5F7");
                r["BarFallbackBg"] = B("#E6242426");
                r["BarHighlight"] = B("#38FFFFFF");
                r["DockBg"] = B("#A6262628");
                r["DockBorder"] = B("#33FFFFFF");
                r["Dot"] = B("#D9F5F5F7");
                r["LabelBg"] = B("#EB2C2C2E");
                r["LabelFg"] = B("#F5F5F7");
                r["LabelBorder"] = B("#40FFFFFF");
                r["MenuBg"] = B("#F5272729");
                r["MenuBorder"] = B("#45FFFFFF");
                r["MenuFg"] = B("#F5F5F7");
                r["MenuSep"] = B("#26FFFFFF");
                r["SpotBg"] = B("#F21F1F21");
                r["SpotFg"] = B("#F5F5F7");
                r["SpotDim"] = B("#8CFFFFFF");
                r["SpotBorder"] = B("#40FFFFFF");
                r["ModuleBg"] = B("#22FFFFFF");
                r["Inactive"] = B("#40FFFFFF");
                r["TrackBg"] = B("#33FFFFFF");
                r["TrackFill"] = B("#F2FFFFFF");
                r["Red"] = B("#FF453A");
            }
            else
            {
                r["BarFg"] = B("#1D1D1F");
                r["BarFallbackBg"] = B("#E6F2F2F4");
                r["BarHighlight"] = B("#24000000");
                r["DockBg"] = B("#A8F4F4F6");
                r["DockBorder"] = B("#73FFFFFF");
                r["Dot"] = B("#CC1D1D1F");
                r["LabelBg"] = B("#F0EFEFF1");
                r["LabelFg"] = B("#1D1D1F");
                r["LabelBorder"] = B("#26000000");
                r["MenuBg"] = B("#F7F4F4F6");
                r["MenuBorder"] = B("#26000000");
                r["MenuFg"] = B("#1D1D1F");
                r["MenuSep"] = B("#1F000000");
                r["SpotBg"] = B("#F5F3F3F5");
                r["SpotFg"] = B("#1D1D1F");
                r["SpotDim"] = B("#8A000000");
                r["SpotBorder"] = B("#26000000");
                r["ModuleBg"] = B("#7AFFFFFF");
                r["Inactive"] = B("#24000000");
                r["TrackBg"] = B("#24000000");
                r["TrackFill"] = B("#FFFFFFFF");
                r["Red"] = B("#FF3B30");
            }
            r["Accent"] = B(AccentHex);   // same in light and dark: the user's chosen accent colour
        }

        public static ResourceDictionary Styles()
        {
            const string xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='{x:Type ContextMenu}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='FontFamily' Value='" + UiFont + @"'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ContextMenu}'>
          <Grid Margin='12,2,16,20' UseLayoutRounding='True' RenderTransformOrigin='0.5,0'>
            <Grid.RenderTransform>
              <ScaleTransform ScaleX='1' ScaleY='1'/>
            </Grid.RenderTransform>
            <Grid.Triggers>
              <EventTrigger RoutedEvent='FrameworkElement.Loaded'>
                <BeginStoryboard>
                  <Storyboard>
                    <DoubleAnimation Storyboard.TargetProperty='Opacity' From='0' To='1' Duration='0:0:0.14'/>
                    <DoubleAnimation Storyboard.TargetProperty='RenderTransform.ScaleX' From='0.95' To='1' Duration='0:0:0.22'>
                      <DoubleAnimation.EasingFunction><BackEase Amplitude='0.3' EasingMode='EaseOut'/></DoubleAnimation.EasingFunction>
                    </DoubleAnimation>
                    <DoubleAnimation Storyboard.TargetProperty='RenderTransform.ScaleY' From='0.95' To='1' Duration='0:0:0.22'>
                      <DoubleAnimation.EasingFunction><BackEase Amplitude='0.3' EasingMode='EaseOut'/></DoubleAnimation.EasingFunction>
                    </DoubleAnimation>
                  </Storyboard>
                </BeginStoryboard>
              </EventTrigger>
            </Grid.Triggers>
            <Border CornerRadius='12' Background='#40000000'>
              <Border.Effect>
                <DropShadowEffect BlurRadius='26' ShadowDepth='6' Direction='270' Opacity='0.32'/>
              </Border.Effect>
            </Border>
            <Border x:Name='GlassBd' Background='{DynamicResource MenuBg}' CornerRadius='12' Padding='6' MinWidth='220'>
              <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
            </Border>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type MenuItem}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type MenuItem}'>
          <Border x:Name='Bd' CornerRadius='7' Padding='4,3,12,4' Background='Transparent'
                  TextBlock.Foreground='{DynamicResource MenuFg}' SnapsToDevicePixels='True'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='18'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='Auto'/>
              </Grid.ColumnDefinitions>
              <TextBlock x:Name='Check' Text='&#x2713;' Visibility='Hidden' FontWeight='SemiBold' VerticalAlignment='Center' HorizontalAlignment='Center'/>
              <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='False' VerticalAlignment='Center'/>
              <TextBlock Grid.Column='2' Text='{TemplateBinding InputGestureText}' Margin='30,0,0,0' Opacity='0.5' VerticalAlignment='Center'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource Accent}'/>
              <Setter TargetName='Bd' Property='TextBlock.Foreground' Value='White'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='Bd' Property='Opacity' Value='0.42'/>
              <Setter TargetName='Bd' Property='Background' Value='Transparent'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='{x:Type Separator}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Separator}'>
          <Border Height='1' Margin='9,5' Background='{DynamicResource MenuSep}'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }
    }
}
