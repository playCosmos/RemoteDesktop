using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RemoteDesktop;

public static class ScreenJpeg
{
    static readonly Guid JpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    static readonly Guid FactoryId = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
    const int NoCache = 2;
    const ushort FloatType = 4;
    const ushort ByteType = 17;

    public static byte[] Encode(Bitmap bitmap, int quality)
    {
        quality = Math.Clamp(quality, 25, 90);
        var bgr = PackBgr(bitmap);
        var factoryType = Type.GetTypeFromCLSID(FactoryId) ?? throw new InvalidOperationException("화면 압축기를 찾지 못했습니다.");
        var factory = (IWicFactory)Activator.CreateInstance(factoryType)!;
        IWicEncoder? encoder = null;
        IWicFrame? frame = null;
        IPropertyBag2? options = null;
        IStream? stream = null;
        var pixels = GCHandle.Alloc(bgr.Bytes, GCHandleType.Pinned);
        try
        {
            var hr = CreateStreamOnHGlobal(IntPtr.Zero, true, out stream);
            if (hr < 0 || stream == null) throw new InvalidOperationException("화면 출력을 만들지 못했습니다.");
            var container = JpegContainer;
            hr = factory.CreateEncoder(ref container, IntPtr.Zero, out encoder);
            if (hr < 0 || encoder == null) throw new InvalidOperationException("화면 압축기를 열지 못했습니다.");
            hr = encoder.Initialize(stream, NoCache);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = encoder.CreateNewFrame(out frame, out options);
            if (hr < 0 || frame == null || options == null) throw new InvalidOperationException("화면 프레임을 열지 못했습니다.");
            WriteOption(options, "ImageQuality", FloatType, BitConverter.SingleToInt32Bits(quality / 100f));
            WriteOption(options, "JpegYCrCbSubsampling", ByteType, UiText.JpegFullColor(quality) ? 3 : 1);
            hr = frame.Initialize(options);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = frame.SetSize((uint)bgr.Width, (uint)bgr.Height);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = frame.SetResolution(96, 96);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var format = Bgr24;
            hr = frame.SetPixelFormat(ref format);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (format != Bgr24) throw new InvalidOperationException("화면 압축기가 색 형식을 바꾸려 합니다.");
            hr = frame.WritePixels((uint)bgr.Height, (uint)bgr.Stride, (uint)bgr.Bytes.Length, pixels.AddrOfPinnedObject());
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = frame.Commit();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = encoder.Commit();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return ReadStream(stream!);
        }
        finally
        {
            pixels.Free();
            if (frame != null) Marshal.ReleaseComObject(frame);
            if (options != null) Marshal.ReleaseComObject(options);
            if (encoder != null) Marshal.ReleaseComObject(encoder);
            if (stream != null) Marshal.ReleaseComObject(stream);
            Marshal.ReleaseComObject(factory);
        }
    }

    public static int Chroma(byte[] jpeg)
    {
        for (var i = 0; i + 3 < jpeg.Length;)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            while (i + 1 < jpeg.Length && jpeg[i + 1] == 0xFF) i++;
            if (i + 1 >= jpeg.Length) break;
            var marker = jpeg[i + 1];
            if (marker == 0xD8) { i += 2; continue; }
            if (marker is 0xD9 or 0xDA) break;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) { i += 2; continue; }
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length) break;
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (length < 8) break;
                var sampling = jpeg[i + 11];
                return sampling == 0x11 ? 444 : sampling == 0x22 ? 420 : sampling;
            }
            i += 2 + length;
        }
        return 0;
    }

    static Packed PackBgr(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("빈 화면은 압축할 수 없습니다.");
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var srcStride = data.Stride;
            var rowBytes = Math.Abs(srcStride);
            var stride = width * 3;
            var bytes = new byte[stride * height];
            var row = new byte[rowBytes];
            for (var y = 0; y < height; y++)
            {
                var offset = srcStride >= 0 ? y * srcStride : (height - 1 - y) * srcStride;
                Marshal.Copy(IntPtr.Add(data.Scan0, offset), row, 0, rowBytes);
                var dest = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var source = x * 4;
                    bytes[dest++] = row[source];
                    bytes[dest++] = row[source + 1];
                    bytes[dest++] = row[source + 2];
                }
            }
            return new Packed(width, height, stride, bytes);
        }
        finally { bitmap.UnlockBits(data); }
    }

    static void WriteOption(IPropertyBag2 bag, string name, ushort type, int value)
    {
        var option = new PropBag { vt = type, name = Marshal.StringToCoTaskMemUni(name) };
        var variant = new Variant { vt = type, number = value };
        try
        {
            var hr = bag.Write(1, ref option, ref variant);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally { Marshal.FreeCoTaskMem(option.name); }
    }

    static byte[] ReadStream(IStream stream)
    {
        stream.Stat(out var stat, 1);
        var length = (int)stat.cbSize;
        if (length <= 0) throw new InvalidOperationException("압축된 화면이 비어 있습니다.");
        stream.Seek(0, 0, IntPtr.Zero);
        var bytes = new byte[length];
        stream.Read(bytes, length, IntPtr.Zero);
        return bytes;
    }

    readonly record struct Packed(int Width, int Height, int Stride, byte[] Bytes);

    [StructLayout(LayoutKind.Sequential)]
    struct PropBag
    {
        public uint kind;
        public ushort vt;
        public ushort clip;
        public uint hint;
        public IntPtr name;
        public Guid id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int number;
    }

    [DllImport("ole32.dll")]
    static extern int CreateStreamOnHGlobal(IntPtr memory, bool deleteOnRelease, out IStream stream);

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWicFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        [PreserveSig] int CreateEncoder(ref Guid container, IntPtr vendor, out IWicEncoder encoder);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWicEncoder
    {
        [PreserveSig] int Initialize(IStream stream, int cache);
        void GetContainerFormat();
        void GetEncoderInfo();
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void SetPreview();
        [PreserveSig] int CreateNewFrame(out IWicFrame frame, out IPropertyBag2 options);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWicFrame
    {
        [PreserveSig] int Initialize(IPropertyBag2 options);
        [PreserveSig] int SetSize(uint width, uint height);
        [PreserveSig] int SetResolution(double dpiX, double dpiY);
        [PreserveSig] int SetPixelFormat(ref Guid format);
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        [PreserveSig] int WritePixels(uint lines, uint stride, uint size, IntPtr pixels);
        void WriteSource();
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("22F55882-280B-11d0-A8A9-00A0C90C2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyBag2
    {
        void Read();
        [PreserveSig] int Write(uint count, ref PropBag option, ref Variant value);
    }
}
