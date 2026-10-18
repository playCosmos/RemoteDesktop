using System.Text;
using RemoteDesktop;

internal static class H264SpsLimitsCheck
{
    public static int Run()
    {
        H264SpsLimits.Validate(Sps(119, 67, 4));
        Reject(Sps(511, 511, 0));
        Reject(ExtremeExpGolomb());
        return 3;
    }

    private static void Reject(byte[] sps)
    {
        try { H264SpsLimits.Validate(sps); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("An oversized or overflowing H.264 SPS was accepted.");
    }

    private static byte[] Sps(int widthMbsMinusOne, int heightMapUnitsMinusOne, int cropBottom)
    {
        var writer = new BitWriter();
        writer.U(66, 8); writer.U(0, 8); writer.U(30, 8);
        writer.Ue(0); writer.Ue(0); writer.Ue(0); writer.Ue(0); writer.Ue(0); writer.Bit(0);
        writer.Ue(widthMbsMinusOne); writer.Ue(heightMapUnitsMinusOne);
        writer.Bit(1); writer.Bit(1); writer.Bit(cropBottom > 0 ? 1 : 0);
        if (cropBottom > 0) { writer.Ue(0); writer.Ue(0); writer.Ue(0); writer.Ue(cropBottom); }
        writer.Bit(0);
        return Wrap(writer.Finish());
    }

    private static byte[] ExtremeExpGolomb()
    {
        var writer = new BitWriter();
        writer.U(66, 8); writer.U(0, 8); writer.U(30, 8);
        for (var i = 0; i < 31; i++) writer.Bit(0);
        writer.Bit(1);
        for (var i = 0; i < 31; i++) writer.Bit(0);
        return Wrap(writer.Finish());
    }

    private static byte[] Wrap(byte[] rbsp)
    {
        var nal = new byte[rbsp.Length + 5];
        nal[3] = 1; nal[4] = 0x67;
        Buffer.BlockCopy(rbsp, 0, nal, 5, rbsp.Length);
        return nal;
    }

    private sealed class BitWriter
    {
        private readonly StringBuilder bits = new();
        public void Bit(int value) => bits.Append(value == 0 ? '0' : '1');
        public void U(int value, int count) { for (var i = count - 1; i >= 0; i--) Bit((value >> i) & 1); }
        public void Ue(int value)
        {
            var code = value + 1;
            var zeros = Convert.ToString(code, 2).Length - 1;
            for (var i = 0; i < zeros; i++) Bit(0);
            U(code, zeros + 1);
        }
        public byte[] Finish()
        {
            Bit(1);
            while ((bits.Length & 7) != 0) Bit(0);
            var output = new byte[bits.Length / 8];
            for (var i = 0; i < bits.Length; i++)
                if (bits[i] == '1') output[i / 8] |= (byte)(1 << (7 - (i & 7)));
            return output;
        }
    }
}
