using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SharpClip;

internal static class Images
{
    public static byte[]? ThumbnailPng(byte[] raw, int maxEdge = 360)
    {
        try
        {
            using var source = Load(raw);
            if (source is null) return null;
            var scale = Math.Min(1.0, maxEdge / (double)Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var thumb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, width, height);
            }
            using var stream = new MemoryStream();
            thumb.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? ToPng(byte[] raw)
    {
        if (raw.Length >= 8 && raw[0] == 0x89 && raw[1] == 0x50) return raw;
        try
        {
            using var source = Load(raw);
            if (source is null) return null;
            using var stream = new MemoryStream();
            source.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? ToDib(byte[] pngOrDib)
    {
        if (pngOrDib.Length >= 4 && pngOrDib[0] != 0x89)
        {
            return pngOrDib;
        }
        try
        {
            using var source = Load(pngOrDib);
            if (source is null) return null;
            using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }
            var width = bitmap.Width;
            var height = bitmap.Height;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, pixels, (height - 1 - y) * stride, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
            var dib = new byte[40 + pixels.Length];
            BitConverter.GetBytes(40).CopyTo(dib, 0);
            BitConverter.GetBytes(width).CopyTo(dib, 4);
            BitConverter.GetBytes(height).CopyTo(dib, 8);
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);
            BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20);
            pixels.CopyTo(dib, 40);
            return dib;
        }
        catch
        {
            return null;
        }
    }

    private static Image? Load(byte[] raw)
    {
        if (raw.Length >= 8 && raw[0] == 0x89 && raw[1] == 0x50)
        {
            return Image.FromStream(new MemoryStream(raw), false, false);
        }
        var handle = DibToHbitmap(raw);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return Image.FromHbitmap(handle);
        }
        finally
        {
            Win32.DeleteObject(handle);
        }
    }

    private static IntPtr DibToHbitmap(byte[] dib)
    {
        if (dib.Length < 40) return IntPtr.Zero;
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || dib.Length < headerSize) return IntPtr.Zero;
        var bitCount = BitConverter.ToInt16(dib, 14);
        var palette = bitCount <= 8 ? ((1 << bitCount) * 4) : 0;
        var info = Marshal.AllocHGlobal(dib.Length);
        try
        {
            Marshal.Copy(dib, 0, info, dib.Length);
            var bits = IntPtr.Add(info, headerSize + palette);
            var dc = Win32.GetDC(IntPtr.Zero);
            var bitmap = Win32.CreateDIBitmap(dc, info, Win32.CBM_INIT, bits, info, Win32.DIB_RGB_COLORS);
            Win32.ReleaseDC(IntPtr.Zero, dc);
            return bitmap;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }
}
