using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RemoteDesktop;

public readonly record struct CursorShot(bool Visible, int HotspotX, int HotspotY, int Width, int Height, string Hash, byte[]? Png);

public static class CursorGlyph
{
    static string? cachedHash;
    static byte[]? cachedPng;

    public static CursorShot Capture()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || info.Cursor == 0 || (info.Flags & 1) == 0) return new(false, 0, 0, 0, 0, "", null);
        var hotX = 0;
        var hotY = 0;
        var width = GetSystemMetrics(13);
        var height = GetSystemMetrics(14);
        nint mask = 0;
        nint color = 0;
        if (GetIconInfo(info.Cursor, out var icon))
        {
            hotX = icon.HotspotX;
            hotY = icon.HotspotY;
            mask = icon.Mask;
            color = icon.Color;
            var measured = Measure(color != 0 ? color : mask, color == 0);
            if (measured.Width is > 0 and <= 256 && measured.Height is > 0 and <= 256)
            {
                width = measured.Width;
                height = measured.Height;
            }
        }
        try
        {
            if (width is < 1 or > 256 || height is < 1 or > 256) return new(true, hotX, hotY, 0, 0, "", null);
            var pixels = Draw(info.Cursor, width, height);
            var identity = new byte[pixels.Length + 8];
            Buffer.BlockCopy(pixels, 0, identity, 0, pixels.Length);
            BitConverter.TryWriteBytes(identity.AsSpan(pixels.Length), hotX);
            BitConverter.TryWriteBytes(identity.AsSpan(pixels.Length + 4), hotY);
            var hash = Convert.ToHexString(SHA256.HashData(identity));
            if (hash != cachedHash || cachedPng == null)
            {
                cachedPng = Encode(pixels, width, height);
                cachedHash = hash;
            }
            return new(true, hotX, hotY, width, height, hash, cachedPng);
        }
        finally
        {
            if (mask != 0) DeleteObject(mask);
            if (color != 0) DeleteObject(color);
        }
    }

    static byte[] Draw(nint cursor, int width, int height)
    {
        using var onBlack = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var onWhite = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        Paint(onBlack, cursor, width, height, Color.Black);
        Paint(onWhite, cursor, width, height, Color.White);
        var black = Read(onBlack);
        var white = Read(onWhite);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var b = black[i * 4];
            var g = black[i * 4 + 1];
            var r = black[i * 4 + 2];
            var alpha = 255 - (white[i * 4 + 2] - r);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            var o = i * 4;
            if (alpha == 0)
            {
                pixels[o] = pixels[o + 1] = pixels[o + 2] = pixels[o + 3] = 0;
                continue;
            }
            pixels[o] = (byte)Math.Min(255, b * 255 / alpha);
            pixels[o + 1] = (byte)Math.Min(255, g * 255 / alpha);
            pixels[o + 2] = (byte)Math.Min(255, r * 255 / alpha);
            pixels[o + 3] = (byte)alpha;
        }
        return pixels;
    }

    static void Paint(Bitmap bitmap, nint cursor, int width, int height, Color color)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        var dc = graphics.GetHdc();
        try { DrawIconEx(dc, 0, 0, cursor, width, height, 0, 0, 3); }
        finally { graphics.ReleaseHdc(dc); }
    }

    static byte[] Read(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            if (data.Stride == bitmap.Width * 4)
            {
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                return bytes;
            }
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var offset = data.Stride >= 0 ? y * data.Stride : (bitmap.Height - 1 - y) * data.Stride;
                Marshal.Copy(IntPtr.Add(data.Scan0, offset), row, 0, row.Length);
                Buffer.BlockCopy(row, 0, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            }
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }

    static byte[] Encode(byte[] pixels, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var offset = data.Stride >= 0 ? y * data.Stride : (height - 1 - y) * data.Stride;
                Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, offset), rowBytes);
            }
        }
        finally { bitmap.UnlockBits(data); }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    static Size Measure(nint bitmap, bool mask)
    {
        var info = new NativeBitmap();
        if (bitmap == 0 || GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), ref info) == 0 || info.Width <= 0 || info.Height <= 0) return Size.Empty;
        return new Size(info.Width, mask ? Math.Max(1, info.Height / 2) : info.Height);
    }

    [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public int Size, Flags; public nint Cursor; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public int HotspotX, HotspotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] static extern bool GetIconInfo(nint cursor, out IconInfo info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, int step, nint brush, int flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern int GetObject(nint handle, int size, ref NativeBitmap bitmap);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint handle);
}
