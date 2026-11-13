using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacLook
{
    /// <summary>
    /// Makes every animation run at the monitor's real refresh rate (WPF caps at 60 fps by default,
    /// which looks choppy on 120/144/165 Hz screens).
    /// </summary>
    internal static class Smooth
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);

        private const int VREFRESH = 116;

        public static int RefreshRate { get; private set; }
        private static int users;
        private static readonly TranslateTransform pump = new TranslateTransform();

        /// <summary>Call once at startup, before any animation is created.</summary>
        public static void Init()
        {
            int hz = 60;
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                hz = GetDeviceCaps(dc, VREFRESH);
                ReleaseDC(IntPtr.Zero, dc);
            }
            if (hz < 50 || hz > 500) hz = 60;
            RefreshRate = hz;
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)hz });
        }

        /// <summary>
        /// Hand-written frame loops (Dock magnification, genie, switcher...) only get the high frame rate
        /// while some animation is running, so keep a silent one going while they are busy.
        /// </summary>
        public static void Begin()
        {
            if (users++ > 0) return;
            pump.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        public static void End()
        {
            if (users == 0) return;
            if (--users > 0) return;
            pump.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}
