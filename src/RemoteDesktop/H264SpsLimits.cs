using System.IO;

namespace RemoteDesktop;

public static class H264SpsLimits
{
    public static void Validate(ReadOnlySpan<byte> accessUnit)
    {
        var data = VideoCodec.ToAnnexB(accessUnit);
        if (!FindStartCode(data, 0, out var start, out var payload)) return;
        while (true)
        {
            var hasNext = FindStartCode(data, payload, out var nextStart, out var nextPayload);
            var end = hasNext ? nextStart : data.Length;
            if (payload < end && (data[payload] & 0x80) == 0 && (data[payload] & 0x1F) == 7)
                ValidateSps(data.AsSpan(payload + 1, end - payload - 1));
            if (!hasNext) return;
            start = nextStart;
            payload = nextPayload;
        }
    }

    private static void ValidateSps(ReadOnlySpan<byte> nal)
    {
        var rbsp = RemoveEmulationPrevention(nal);
        if (rbsp.Length < 4) throw new InvalidOperationException("H.264 SPS가 잘못되었습니다.");
        var profile = rbsp[0];
        var bits = new Bits(rbsp, 3);
        _ = bits.Ue();
        var chroma = 1;
        var separate = false;
        if (HighProfile(profile))
        {
            chroma = bits.Ue();
            if (chroma > 3) throw new InvalidOperationException("H.264 색 형식이 지원 범위를 벗어났습니다.");
            if (chroma == 3) separate = bits.U(1) != 0;
            _ = bits.Ue();
            _ = bits.Ue();
            _ = bits.U(1);
            if (bits.U(1) != 0)
            {
                var lists = chroma == 3 ? 12 : 8;
                for (var i = 0; i < lists; i++)
                    if (bits.U(1) != 0) SkipScaling(bits, i < 6 ? 16 : 64);
            }
        }
        _ = bits.Ue();
        var order = bits.Ue();
        if (order == 0) _ = bits.Ue();
        else if (order == 1)
        {
            _ = bits.U(1);
            _ = bits.Se();
            _ = bits.Se();
            var cycle = bits.Ue();
            if (cycle > 255) throw new InvalidOperationException("H.264 picture order cycle is too large.");
            for (var i = 0; i < cycle; i++) _ = bits.Se();
        }
        else if (order != 2) throw new InvalidOperationException("H.264 picture order is invalid.");
        _ = bits.Ue();
        _ = bits.U(1);
        var codedWidth = (bits.Ue() + 1L) * 16L;
        var mapUnits = bits.Ue() + 1L;
        var frameOnly = bits.U(1);
        if (frameOnly == 0) _ = bits.U(1);
        _ = bits.U(1);
        long cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (bits.U(1) != 0)
        {
            cropLeft = bits.Ue();
            cropRight = bits.Ue();
            cropTop = bits.Ue();
            cropBottom = bits.Ue();
        }
        var codedHeight = mapUnits * 16L * (2L - frameOnly);
        var arrayType = separate ? 0 : chroma;
        var cropX = arrayType switch { 0 => 1, 1 or 2 => 2, _ => 1 };
        var cropY = arrayType switch { 0 => 2 - frameOnly, 1 => 2 * (2 - frameOnly), _ => 2 - frameOnly };
        var width = codedWidth - (cropLeft + cropRight) * cropX;
        var height = codedHeight - (cropTop + cropBottom) * cropY;
        if (codedWidth < 2 || codedHeight < 2 || codedWidth > FrameLimits.MaxDimension || codedHeight > FrameLimits.MaxDimension
            || width <= 1 || height <= 1 || width > FrameLimits.MaxDimension || height > FrameLimits.MaxDimension
            || width * height > FrameLimits.MaxPixels)
            throw new InvalidOperationException("H.264 프레임 크기가 지원 범위를 벗어났습니다.");
    }

    private static void SkipScaling(Bits bits, int size)
    {
        var last = 8;
        var next = 8;
        for (var i = 0; i < size; i++)
        {
            if (next != 0) next = ((last + bits.Se()) % 256 + 256) % 256;
            if (next != 0) last = next;
        }
    }

    private static bool HighProfile(int profile)
        => profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135;

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> nal)
    {
        var rbsp = new List<byte>(nal.Length);
        var zeros = 0;
        foreach (var value in nal)
        {
            if (zeros == 2 && value == 3) { zeros = 0; continue; }
            rbsp.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return rbsp.ToArray();
    }

    private static bool FindStartCode(byte[] data, int from, out int start, out int payload)
    {
        for (var i = Math.Max(0, from); i + 3 <= data.Length; i++)
        {
            if (i + 4 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            { start = i; payload = i + 4; return true; }
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            { start = i; payload = i + 3; return true; }
        }
        start = payload = -1;
        return false;
    }

    private sealed class Bits
    {
        private readonly byte[] data;
        private int bit;
        public Bits(byte[] data, int byteOffset) { this.data = data; bit = byteOffset * 8; }
        public int U(int count)
        {
            if (count is < 1 or > 30) throw new InvalidOperationException("H.264 bit field is invalid.");
            var value = 0;
            for (var i = 0; i < count; i++) value = (value << 1) | One();
            return value;
        }
        public int Ue()
        {
            var zeros = 0;
            while (One() == 0)
            {
                if (++zeros > 30) throw new InvalidOperationException("H.264 Exp-Golomb value is too large.");
            }
            var value = zeros == 0 ? 0L : ((1L << zeros) - 1L) + U(zeros);
            if (value > int.MaxValue) throw new InvalidOperationException("H.264 Exp-Golomb value is too large.");
            return (int)value;
        }
        public int Se()
        {
            var code = Ue();
            var magnitude = (code + 1L) >> 1;
            var value = (code & 1) == 0 ? -magnitude : magnitude;
            if (value < int.MinValue || value > int.MaxValue) throw new InvalidOperationException("H.264 signed Exp-Golomb value is too large.");
            return (int)value;
        }
        private int One()
        {
            var index = bit >> 3;
            if (index >= data.Length) throw new InvalidOperationException("H.264 SPS ended unexpectedly.");
            var value = (data[index] >> (7 - (bit & 7))) & 1;
            bit++;
            return value;
        }
    }
}
