using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nova.Core;

/// <summary>
/// Windows Kabuğu'ndan dosya küçük resmi (fotoğraf/video kareleri) veya büyük simge alır.
/// </summary>
public static class ShellThumbnail
{
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, ref DIBSECTION ds);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    const int SIIGBF_BIGGERSIZEOK = 0x01, SIIGBF_ICONONLY = 0x04, SIIGBF_THUMBNAILONLY = 0x08;

    /// <returns>Görsel ve bunun bir simge mi (true) yoksa gerçek küçük resim mi olduğu.</returns>
    public static (BitmapSource? Image, bool IsIcon) Get(string path, int size, bool allowIcon = true)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory? f = null;
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f) != 0 || f == null)
                return (null, false);

            var sz = new SIZE { cx = size, cy = size };
            if (f.GetImage(sz, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out var hb) == 0 && hb != IntPtr.Zero)
            {
                var img = ToBitmap(hb, trim: false);
                if (img != null) return (img, false);
            }
            if (allowIcon && f.GetImage(sz, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hb) == 0 && hb != IntPtr.Zero)
                return (ToBitmap(hb, trim: true), true);
        }
        catch { }
        finally
        {
            if (f != null) Marshal.ReleaseComObject(f);
        }
        return (null, false);
    }

    static BitmapSource? ToBitmap(IntPtr hbm, bool trim)
    {
        try
        {
            var ds = new DIBSECTION();
            int got = GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds);
            if (got == 0) return null;

            int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight);
            if (ds.dsBm.bmBitsPixel != 32 || ds.dsBm.bmBits == IntPtr.Zero || got < Marshal.SizeOf<DIBSECTION>())
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }

            // DIBSECTION başlığındaki yön bilgisi küçük resimlerde güvenilir değil (ters görünüyorlar);
            // GDI'dan pikselleri yukarıdan aşağıya düzende istemek her iki durumda da doğru sonucu verir.
            int stride = w * 4;
            var buf = new byte[stride * h];
            var bi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
            };
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(hdc, hbm, 0, (uint)h, buf, ref bi, 0) == 0) return null;
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }

            bool anyAlpha = false, premultiplied = true;
            for (int i = 0; i < buf.Length; i += 4)
            {
                byte a = buf[i + 3];
                if (a != 0) anyAlpha = true;
                if (buf[i] > a || buf[i + 1] > a || buf[i + 2] > a) premultiplied = false;
            }
            var fmt = !anyAlpha ? PixelFormats.Bgr32 : premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;

            BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, fmt, null, buf, stride);

            // Küçük simgeler büyük tuvalin bir köşesine çizilmiş olabilir: boşlukları kırp.
            if (trim && anyAlpha && TrimRect(buf, w, h, stride) is { } r && (r.Width < w - 4 || r.Height < h - 4))
                bmp = new CroppedBitmap(bmp, r);

            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
        finally { DeleteObject(hbm); }
    }

    static Int32Rect? TrimRect(byte[] buf, int w, int h, int stride)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (buf[y * stride + x * 4 + 3] > 12)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return null;

        int pad = 2;
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
        return new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
