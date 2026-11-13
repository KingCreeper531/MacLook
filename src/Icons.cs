using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacLook
{
    /// <summary>Loads high-resolution shell icons (exe, lnk, folders, Store apps via shell:AppsFolder\AUMID).</summary>
    internal static class IconLoader
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);

        private const string IID_IShellItem = "43826d1e-e718-42ee-bc55-a1e261c37bfe";
        private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
        private const uint DIB_RGB_COLORS = 0;

        private static readonly Dictionary<string, BitmapSource> cache = new Dictionary<string, BitmapSource>();
        private static readonly Dictionary<string, DateTime> failedAt = new Dictionary<string, DateTime>();
        private const double RetryAfterSec = 1;

        public static bool Exists(string path)
        {
            object o = null;
            Guid iid = new Guid(IID_IShellItem);
            int hr;
            try { hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out o); }
            catch { return false; }
            if (o != null) Marshal.ReleaseComObject(o);
            return hr == 0;
        }

        public static BitmapSource Get(string path, int size, bool refresh = false)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path.ToLowerInvariant() + "|" + size;
            BitmapSource bs;
            if (!refresh && cache.TryGetValue(key, out bs)) return bs;
            DateTime failed;
            if (!refresh && failedAt.TryGetValue(key, out failed) && (DateTime.Now - failed).TotalSeconds < RetryAfterSec) return null;
            bs = Load(path, size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK);
            if (bs == null) bs = Load(path, size, SIIGBF_RESIZETOFIT);   // thumbnail, for items without an icon
            // A failure is only remembered briefly: the shell is often still extracting the icon.
            if (bs != null) { cache[key] = bs; failedAt.Remove(key); }
            else failedAt[key] = DateTime.Now;
            return bs;
        }

        private static BitmapSource Load(string path, int size, int flags)
        {
            object o = null;
            try
            {
                Guid iid = new Guid(IID_IShellItem);
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out o) != 0 || o == null) return null;
                var factory = (IShellItemImageFactory)o;
                IntPtr hbm;
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                try { return FromHBitmap(hbm); }
                finally { DeleteObject(hbm); }
            }
            catch (Exception ex) { Program.Log("Icon for " + path + ": " + ex.Message); return null; }
            finally { if (o != null) Marshal.ReleaseComObject(o); }
        }

        private static BitmapSource FromHBitmap(IntPtr hbm)
        {
            BITMAP bm;
            if (GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var bi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32
            };
            var px = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            int lines;
            try { lines = GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, DIB_RGB_COLORS); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            if (lines == 0) return null;

            bool hasAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            bs.Freeze();
            return bs;
        }
    }
}
