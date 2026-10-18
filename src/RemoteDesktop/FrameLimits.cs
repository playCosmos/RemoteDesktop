using System.IO;

namespace RemoteDesktop;

public static class FrameLimits
{
    public const int MaxResponseBytes = 64 * 1024 * 1024;
    public const int MaxDimension = 16_384;
    public const long MaxPixels = 40_000_000;
    public const long MaxDecodedBytes = MaxPixels * sizeof(int);
    public const long MaxNv12Bytes = MaxPixels * 2;

    public static bool IsSupported(int width, int height)
        => width > 1 && height > 1
            && width <= MaxDimension && height <= MaxDimension
            && (long)width * height <= MaxPixels;

    public static bool TryReadJpegDimensions(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;
        var offset = 2;
        while (offset < jpeg.Length)
        {
            if (jpeg[offset++] != 0xFF) return false;
            while (offset < jpeg.Length && jpeg[offset] == 0xFF) offset++;
            if (offset >= jpeg.Length) return false;
            var marker = jpeg[offset++];
            if (marker is 0xD9 or 0xDA) return false;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8) continue;
            if (jpeg.Length - offset < 2) return false;
            var segmentLength = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (segmentLength < 2 || segmentLength > jpeg.Length - offset) return false;
            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7) return false;
                height = (jpeg[offset + 3] << 8) | jpeg[offset + 4];
                width = (jpeg[offset + 5] << 8) | jpeg[offset + 6];
                return width > 0 && height > 0;
            }
            offset += segmentLength;
        }
        return false;
    }

    private static bool IsStartOfFrame(byte marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    public static int CheckedByteCount(int width, int height, int bytesPerPixel)
    {
        if (!IsSupported(width, height) || bytesPerPixel <= 0)
            throw new InvalidOperationException("원격 화면 크기가 지원 범위를 벗어났습니다.");
        return checked((int)((long)width * height * bytesPerPixel));
    }
}
