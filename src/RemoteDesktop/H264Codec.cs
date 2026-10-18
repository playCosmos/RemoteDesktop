using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.IO;

namespace RemoteDesktop;

public static class VideoCodec
{
    public static string Normalize(string? codec)
        => string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase) ? "h264" : "jpeg";

    public static string Negotiate(string? requested, bool encoderReady)
        => Normalize(requested) == "h264" && encoderReady ? "h264" : "jpeg";

    public static int Bitrate(int quality, int width, int height)
    {
        var level = Math.Clamp(quality, 25, 90);
        var pixels = Math.Max(2, width) * (long)Math.Max(2, height);
        var reference = 1920L * 1080L;
        var perPicture = level <= 40 ? 1_500_000L : level >= 70 ? 8_000_000L : 4_000_000L;
        var scaled = (int)Math.Clamp(perPicture * pixels / reference, 400_000L, 20_000_000L);
        return scaled;
    }

    public static bool NeedsKeyframe(int framesSinceKey, int fps, bool force)
        => force || framesSinceKey <= 0 || framesSinceKey >= Math.Max(1, fps) * 2;

    public readonly record struct PaceStep(int Quality, int Slow, int Fast, bool Changed);

    public static PaceStep Pace(int ceiling, int current, double transferMs, int fps, int slow, int fast, bool picture)
    {
        ceiling = Math.Clamp(ceiling, 25, 90);
        current = Math.Clamp(current, 25, ceiling);
        if (!picture || transferMs < 0 || double.IsNaN(transferMs) || double.IsInfinity(transferMs)) return new(current, slow, fast, false);
        var rate = fps is 5 or 10 or 15 or 30 ? fps : 15;
        var budget = 1000.0 / rate;
        if (transferMs > budget * 1.25) { slow++; fast = 0; }
        else if (transferMs <= budget * 0.5) { fast++; slow = 0; }
        else slow = fast = 0;
        if (slow >= 3 && current > 25)
        {
            var next = LowerQuality(current);
            if (next < current) return new(next, 0, 0, true);
        }
        if (fast >= 8 && current < ceiling)
        {
            var next = RaiseQuality(current, ceiling);
            if (next > current) return new(next, 0, 0, true);
        }
        return new(current, slow, fast, false);
    }

    private static int LowerQuality(int quality)
    {
        if (quality >= 70) return 55;
        if (quality >= 50) return 40;
        return 25;
    }

    private static int RaiseQuality(int quality, int ceiling)
    {
        var next = quality < 40 ? 40 : quality < 55 ? 55 : quality < 75 ? 75 : 90;
        return Math.Min(ceiling, next);
    }

    public static bool IsAnnexB(ReadOnlySpan<byte> sample)
        => sample.Length >= 4 && sample[0] == 0 && sample[1] == 0 && (sample[2] == 1 || (sample[2] == 0 && sample[3] == 1));

    public static byte[] ToAnnexB(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0 || IsAnnexB(sample)) return sample.ToArray();
        var output = new List<byte>(sample.Length + 16);
        var offset = 0;
        while (offset + 4 <= sample.Length)
        {
            var length = (sample[offset] << 24) | (sample[offset + 1] << 16) | (sample[offset + 2] << 8) | sample[offset + 3];
            offset += 4;
            if (length <= 0 || offset + length > sample.Length) return sample.ToArray();
            output.Add(0); output.Add(0); output.Add(0); output.Add(1);
            for (var i = 0; i < length; i++) output.Add(sample[offset + i]);
            offset += length;
        }
        return output.Count == 0 ? sample.ToArray() : output.ToArray();
    }
}

public sealed class H264Encoder : IDisposable
{
    IMFTransform? transform;
    int width, height, fps = 15, bitrate;
    long clock;
    bool streaming;
    int framesSinceKey = -1;
    bool disposed;

    int quality = 55;

    public byte[] Encode(Bitmap bitmap, bool keyframe, int framesPerSecond = 15, int pictureQuality = 55)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var frame = Even(bitmap, out var owned);
        try
        {
            Ensure(frame.Width, frame.Height, Math.Clamp(framesPerSecond, 1, 30), Math.Clamp(pictureQuality, 25, 90));
            var nv12 = ToNv12(frame);
            var force = VideoCodec.NeedsKeyframe(framesSinceKey, fps, keyframe);
            if (force) ForceKeyframe();
            using var sample = CreateInput(nv12, force);
            var hr = transform!.ProcessInput(0, sample.Sample, 0);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            framesSinceKey = force ? 1 : framesSinceKey + 1;
            return ReadOutput();
        }
        finally { if (owned) frame.Dispose(); }
    }

    public byte[] EncodeJpeg(byte[] jpeg, bool keyframe, int framesPerSecond = 15, int pictureQuality = 55)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        using var bitmap = new Bitmap(stream);
        return Encode(bitmap, keyframe, framesPerSecond, pictureQuality);
    }

    void Ensure(int frameWidth, int frameHeight, int framesPerSecond, int pictureQuality)
    {
        var rate = VideoCodec.Bitrate(pictureQuality, frameWidth, frameHeight);
        if (transform != null && width == frameWidth && height == frameHeight && fps == framesPerSecond && bitrate == rate && quality == pictureQuality) return;
        ReleaseTransform();
        MediaFoundation.Startup();
        var type = Type.GetTypeFromCLSID(new Guid("6ca50344-051a-4ded-9779-a43305165e35")) ?? throw new InvalidOperationException("H.264 인코더를 찾지 못했습니다.");
        transform = (IMFTransform)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("H.264 인코더를 열지 못했습니다."));
        width = frameWidth;
        height = frameHeight;
        fps = framesPerSecond;
        bitrate = rate;
        quality = pictureQuality;
        framesSinceKey = -1;
        if (transform.GetAttributes(out var attributes) >= 0)
        {
            var lowLatency = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
            attributes.SetUINT32(ref lowLatency, 1);
            Marshal.ReleaseComObject(attributes);
        }
        try
        {
            if (transform is ICodecAPI codec)
            {
                TrySet(codec, new Guid("8d390aac-dc5c-4200-b57f-814d04babab2"), 0);
                TrySet(codec, new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), 1);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { }
        var output = MediaFoundation.VideoType(MediaFoundation.H264, width, height, fps, bitrate);
        var input = MediaFoundation.VideoType(MediaFoundation.Nv12, width, height, fps, 0);
        var hr = transform.SetOutputType(0, output, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        hr = transform.SetInputType(0, input, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        hr = transform.ProcessMessage(0x10000000, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        transform.ProcessMessage(0x10000007, 0);
        streaming = true;
    }

    static void TrySet(ICodecAPI codec, Guid api, uint value)
    {
        object boxed = value;
        codec.SetValue(ref api, ref boxed);
    }

    void ForceKeyframe()
    {
        try
        {
            if (transform is not ICodecAPI codec) return;
            TrySet(codec, new Guid("398c1b98-8353-475a-9ef2-8f265d260345"), 1u);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { }
    }

    SampleHolder CreateInput(byte[] nv12, bool key)
    {
        var hr = MediaFoundation.MFCreateSample(out var sample);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        hr = MediaFoundation.MFCreateMemoryBuffer(nv12.Length, out var buffer);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        buffer.Lock(out var pointer, out _, out _);
        try { Marshal.Copy(nv12, 0, pointer, nv12.Length); }
        finally { buffer.Unlock(); }
        buffer.SetCurrentLength(nv12.Length);
        hr = sample.AddBuffer(buffer);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        var duration = 10_000_000L / fps;
        hr = sample.SetSampleTime(clock);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        hr = sample.SetSampleDuration(duration);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        clock += duration;
        if (key)
        {
            var clean = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
            sample.SetUINT32(ref clean, 1);
        }
        Marshal.ReleaseComObject(buffer);
        return new SampleHolder(sample);
    }

    byte[] ReadOutput()
    {
        var provides = false;
        var bufferSize = Math.Max(width * height, 65536);
        if (transform!.GetOutputStreamInfo(0, out var info) >= 0)
        {
            provides = (info.Flags & 0x100) != 0;
            if (info.Size > bufferSize) bufferSize = info.Size;
        }
        var collected = new List<byte>();
        for (var i = 0; i < 8; i++)
        {
            var output = new MFT_OUTPUT_DATA_BUFFER { StreamId = 0 };
            if (!provides) output.Sample = MediaFoundation.WrapSample(new byte[bufferSize]);
            var hr = transform!.ProcessOutput(0, 1, ref output, out _);
            if (hr == unchecked((int)0xC00D6D72)) break;
            if (hr < 0) throw new InvalidOperationException("H.264 출력을 읽지 못했습니다. " + hr.ToString("X8"));
            if (output.Sample == null) continue;
            try
            {
                var read = MediaFoundation.ReadSample(output.Sample);
                if (read.Length > 0) collected.AddRange(VideoCodec.ToAnnexB(read));
            }
            finally { Marshal.ReleaseComObject(output.Sample); }
        }
        return collected.Count == 0 ? [] : collected.ToArray();
    }

    static Bitmap Even(Bitmap source, out bool owned)
    {
        var width = source.Width & ~1;
        var height = source.Height & ~1;
        if (width == source.Width && height == source.Height && source.PixelFormat == PixelFormat.Format32bppArgb)
        {
            owned = false;
            return source;
        }
        if (width < 2 || height < 2) throw new InvalidOperationException("화면이 너무 작아 영상으로 보내지 못합니다.");
        var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(copy)) graphics.DrawImage(source, new Rectangle(0, 0, width, height), new Rectangle(0, 0, width, height), GraphicsUnit.Pixel);
        owned = true;
        return copy;
    }

    static byte[] ToNv12(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var nv12 = new byte[width * height * 3 / 2];
            var row = new byte[data.Stride];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < width; x++)
                {
                    var b = row[x * 4];
                    var g = row[x * 4 + 1];
                    var r = row[x * 4 + 2];
                    nv12[y * width + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                }
            }
            var chroma = width * height;
            for (var y = 0; y < height; y += 2)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                var next = new byte[data.Stride];
                if (y + 1 < height) Marshal.Copy(data.Scan0 + (y + 1) * data.Stride, next, 0, next.Length);
                else next = row;
                for (var x = 0; x < width; x += 2)
                {
                    int u = 0, v = 0;
                    Accumulate(row, x, ref u, ref v);
                    Accumulate(row, Math.Min(width - 1, x + 1), ref u, ref v);
                    Accumulate(next, x, ref u, ref v);
                    Accumulate(next, Math.Min(width - 1, x + 1), ref u, ref v);
                    var index = chroma + (y / 2) * width + x;
                    nv12[index] = (byte)Math.Clamp(u / 4, 0, 255);
                    nv12[index + 1] = (byte)Math.Clamp(v / 4, 0, 255);
                }
            }
            return nv12;
        }
        finally { bitmap.UnlockBits(data); }
    }

    static void Accumulate(byte[] row, int x, ref int u, ref int v)
    {
        var b = row[x * 4];
        var g = row[x * 4 + 1];
        var r = row[x * 4 + 2];
        u += Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
        v += Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
    }

    public void Dispose()
    {
        disposed = true;
        ReleaseTransform();
    }

    void ReleaseTransform()
    {
        var current = transform;
        transform = null;
        var wasStreaming = streaming;
        streaming = false;
        if (current == null) return;
        try
        {
            if (wasStreaming)
            {
                try { current.ProcessMessage(0x10000001, 0); }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
            }
        }
        finally
        {
            try { Marshal.ReleaseComObject(current); }
            catch (InvalidComObjectException) { }
        }
    }

    readonly struct SampleHolder(IMFSample sample) : IDisposable
    {
        public IMFSample Sample { get; } = sample;
        public void Dispose() => Marshal.ReleaseComObject(Sample);
    }
}

public sealed class H264Decoder : IDisposable
{
    IMFTransform? transform;
    int width, height, stride;
    long clock;
    bool streaming, configured;
    bool disposed;

    public bool TryDecode(byte[] annexB, out byte[] pixels, out int frameWidth, out int frameHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        pixels = [];
        frameWidth = 0;
        frameHeight = 0;
        MediaFoundation.Startup();
        Ensure();
        var sample = MediaFoundation.WrapSample(VideoCodec.ToAnnexB(annexB));
        try
        {
            var duration = 10_000_000L / 15;
            sample.SetSampleTime(clock);
            sample.SetSampleDuration(duration);
            clock += duration;
            if (RandomAccess(annexB))
            {
                var clean = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
                sample.SetUINT32(ref clean, 1);
            }
            var hr = transform!.ProcessInput(0, sample, 0);
            if (hr == unchecked((int)0xC00D6D6A) && Drain(out pixels, out frameWidth, out frameHeight))
            {
                hr = transform.ProcessInput(0, sample, 0);
                return true;
            }
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return Drain(out pixels, out frameWidth, out frameHeight);
        }
        finally { Marshal.ReleaseComObject(sample); }
    }

    void Ensure()
    {
        if (transform != null) return;
        var type = Type.GetTypeFromCLSID(new Guid("62ce7e72-4c71-4d20-b15d-452831a87d9d")) ?? throw new InvalidOperationException("H.264 디코더를 찾지 못했습니다.");
        transform = (IMFTransform)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("H.264 디코더를 열지 못했습니다."));
        if (transform.GetAttributes(out var attributes) >= 0)
        {
            var lowLatency = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
            attributes.SetUINT32(ref lowLatency, 1);
            var software = new Guid("eaa35c29-775e-488e-9b61-b3283e49583b");
            attributes.SetUINT32(ref software, 0);
            Marshal.ReleaseComObject(attributes);
        }
        var input = MediaFoundation.VideoType(MediaFoundation.H264, 0, 0, 15, 0);
        var hr = transform.SetInputType(0, input, 0);
        Marshal.ReleaseComObject(input);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        transform.ProcessMessage(0x10000000, 0);
        transform.ProcessMessage(0x10000007, 0);
        streaming = true;
    }

    bool Drain(out byte[] pixels, out int frameWidth, out int frameHeight)
    {
        pixels = [];
        frameWidth = 0;
        frameHeight = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var output = new MFT_OUTPUT_DATA_BUFFER();
            var allocate = configured && !ProvidesSamples();
            if (allocate) output.Sample = OutputSample();
            var hr = transform!.ProcessOutput(0, 1, ref output, out _);
            if (hr == unchecked((int)0xC00D6D72))
            {
                if (output.Sample != null) Marshal.ReleaseComObject(output.Sample);
                return false;
            }
            if (hr == unchecked((int)0xC00D6D61) || hr == unchecked((int)0xC00D6D60))
            {
                if (output.Sample != null) Marshal.ReleaseComObject(output.Sample);
                configured = false;
                if (!ConfigureOutput()) return false;
                continue;
            }
            if (hr < 0)
            {
                if (output.Sample != null) Marshal.ReleaseComObject(output.Sample);
                Marshal.ThrowExceptionForHR(hr);
            }
            if (output.Sample == null) return false;
            try
            {
                var nv12 = MediaFoundation.ReadSample(output.Sample);
                pixels = FromNv12(nv12, width, height, stride);
                if (pixels.Length == 0) return false;
                frameWidth = width;
                frameHeight = height;
                return true;
            }
            finally { Marshal.ReleaseComObject(output.Sample); }
        }
        return false;
    }

    bool ConfigureOutput()
    {
        IMFAttributes? chosen = null;
        var nativeWidth = 0;
        var nativeHeight = 0;
        for (var index = 0; index < 12; index++)
        {
            var hr = transform!.GetOutputAvailableType(0, index, out var media);
            if (hr < 0) break;
            media.GetGUID(ref MediaFoundation.SubtypeKey, out var subtype);
            long packed = 0;
            media.GetUINT64(ref MediaFoundation.FrameSizeKey, out packed);
            var w = (int)(packed >> 32);
            var h = (int)(packed & 0xffffffff);
            if (index == 0) { nativeWidth = w; nativeHeight = h; }
            var nv12 = subtype == MediaFoundation.Nv12;
            var better = nv12 && (chosen == null || (w == nativeWidth && h == nativeHeight && w > 1));
            if (better)
            {
                if (chosen != null) Marshal.ReleaseComObject(chosen);
                chosen = media;
            }
            else Marshal.ReleaseComObject(media);
        }
        if (chosen == null) return false;
        var set = transform!.SetOutputType(0, chosen, 0);
        Marshal.ReleaseComObject(chosen);
        if (set < 0) return false;
        var currentHr = transform.GetOutputCurrentType(0, out var current);
        if (currentHr < 0) return false;
        try
        {
            current.GetUINT64(ref MediaFoundation.FrameSizeKey, out var packed);
            width = (int)(packed >> 32);
            height = (int)(packed & 0xffffffff);
            stride = current.GetUINT32(ref MediaFoundation.StrideKey, out var packedStride) >= 0 ? packedStride : width;
        }
        finally { Marshal.ReleaseComObject(current); }
        configured = width > 1 && height > 1;
        transform.ProcessMessage(0x10000007, 0);
        return configured;
    }

    bool ProvidesSamples()
    {
        var hr = transform!.GetOutputStreamInfo(0, out var info);
        return hr >= 0 && (info.Flags & 0x100) != 0;
    }

    int OutputSize()
    {
        if (transform!.GetOutputStreamInfo(0, out var info) >= 0 && info.Size > 0) return info.Size;
        var row = Math.Max(width, Math.Abs(stride));
        return Math.Max(row * Math.Max(height, 2) * 3 / 2, 64);
    }

    int OutputAlignment()
    {
        if (transform!.GetOutputStreamInfo(0, out var info) >= 0 && info.Alignment > 1) return info.Alignment;
        return 0;
    }

    IMFSample OutputSample()
    {
        var size = OutputSize();
        var align = OutputAlignment();
        if (align < 16) align = 16;
        return MediaFoundation.WrapSample(new byte[size], align, true);
    }

    static bool RandomAccess(ReadOnlySpan<byte> sample)
    {
        for (var i = 0; i + 4 < sample.Length; i++)
        {
            var longStart = sample[i] == 0 && sample[i + 1] == 0 && sample[i + 2] == 0 && sample[i + 3] == 1;
            var shortStart = sample[i] == 0 && sample[i + 1] == 0 && sample[i + 2] == 1;
            if (!longStart && !shortStart) continue;
            var header = i + (longStart ? 4 : 3);
            if (header >= sample.Length) break;
            var kind = sample[header] & 0x1F;
            if (kind is 5 or 7) return true;
            i = header;
        }
        return false;
    }

    public static byte[] FromNv12(byte[] nv12, int width, int height, int stride = 0)
    {
        if (width < 2 || height < 2) return [];
        var bottomUp = stride < 0;
        var rowBytes = Math.Abs(stride);
        if (rowBytes < width) rowBytes = width;
        var chroma = rowBytes * height;
        if (nv12.Length < chroma + rowBytes * (height / 2)) return [];
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var yRow = bottomUp ? height - 1 - y : y;
            var uvRow = bottomUp ? (height / 2 - 1) - (y / 2) : y / 2;
            for (var x = 0; x < width; x++)
            {
                var luma = nv12[yRow * rowBytes + x] - 16;
                var uv = chroma + uvRow * rowBytes + (x & ~1);
                if (uv + 1 >= nv12.Length) return [];
                var u = nv12[uv] - 128;
                var v = nv12[uv + 1] - 128;
                var target = (y * width + x) * 4;
                pixels[target] = Clamp((298 * luma + 516 * u + 128) >> 8);
                pixels[target + 1] = Clamp((298 * luma - 100 * u - 208 * v + 128) >> 8);
                pixels[target + 2] = Clamp((298 * luma + 409 * v + 128) >> 8);
                pixels[target + 3] = 255;
            }
        }
        return pixels;
    }

    static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    public void Dispose()
    {
        disposed = true;
        if (transform == null) return;
        if (streaming) transform.ProcessMessage(0x10000001, 0);
        Marshal.ReleaseComObject(transform);
        transform = null;
    }
}

public sealed class H264Session : IDisposable
{
    readonly H264Encoder encoder = new();
    int misses;
    bool disposed;
    bool encoderReleased;
    public bool Retired { get; private set; }
    public bool PendingKeyframe { get; private set; } = true;

    public byte[] Encode(byte[] jpeg, bool key, bool reused, int fps, int quality)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Retired || jpeg.Length == 0) return [];
        try
        {
            var bytes = encoder.EncodeJpeg(jpeg, key || reused || PendingKeyframe, fps, quality);
            if (bytes.Length == 0)
            {
                NoteMiss();
                return [];
            }
            misses = 0;
            PendingKeyframe = false;
            return bytes;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or ArgumentException)
        {
            NoteMiss();
            return [];
        }
    }

    public void NoteSkipped() => PendingKeyframe = true;

    void NoteMiss()
    {
        PendingKeyframe = true;
        if (++misses >= 3)
        {
            Retired = true;
            ReleaseEncoder();
        }
    }

    void ReleaseEncoder()
    {
        if (encoderReleased) return;
        encoderReleased = true;
        encoder.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseEncoder();
    }
}

static class MediaFoundation
{
    public static Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static Guid MajorKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static Guid BitrateKey = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static Guid InterlaceKey = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static Guid AspectKey = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static Guid StrideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static int started;

    public static void Startup()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            var hr = MFStartup(0x00020070, 0);
            if (hr < 0) { Interlocked.Exchange(ref started, 0); Marshal.ThrowExceptionForHR(hr); }
        }
    }

    public static IMFAttributes VideoType(Guid subtype, int width, int height, int fps, int bitrate)
    {
        var hr = MFCreateMediaType(out var media);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        media.SetGUID(ref MajorKey, ref Video);
        media.SetGUID(ref SubtypeKey, ref subtype);
        if (width > 0 && height > 0) media.SetUINT64(ref FrameSizeKey, unchecked((long)(((ulong)(uint)width << 32) | (uint)height)));
        if (fps > 0) media.SetUINT64(ref FrameRateKey, unchecked((long)(((ulong)(uint)fps << 32) | 1u)));
        if (bitrate > 0) media.SetUINT32(ref BitrateKey, bitrate);
        media.SetUINT32(ref InterlaceKey, 2);
        media.SetUINT64(ref AspectKey, unchecked((long)((1ul << 32) | 1u)));
        return media;
    }

    public static IMFSample WrapSample(byte[] bytes, int alignment = 0, bool empty = false)
    {
        var hr = MFCreateSample(out var sample);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        var length = Math.Max(bytes.Length, 1);
        IMFMediaBuffer buffer;
        hr = alignment > 1 ? MFCreateAlignedMemoryBuffer(length, alignment, out buffer) : MFCreateMemoryBuffer(length, out buffer);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        buffer.Lock(out var pointer, out _, out _);
        try { if (bytes.Length > 0) Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { buffer.Unlock(); }
        buffer.SetCurrentLength(empty ? 0 : bytes.Length);
        sample.AddBuffer(buffer);
        Marshal.ReleaseComObject(buffer);
        return sample;
    }

    public static byte[] ReadSample(IMFSample sample)
    {
        var hr = sample.ConvertToContiguousBuffer(out var buffer);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            buffer.Lock(out var pointer, out _, out var current);
            try
            {
                if (current <= 0) return [];
                var bytes = new byte[current];
                Marshal.Copy(pointer, bytes, 0, current);
                return bytes;
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFAttributes type);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll")] public static extern int MFCreateAlignedMemoryBuffer(int maxLength, int alignment, out IMFMediaBuffer buffer);
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaBuffer
{
    void Lock(out nint buffer, out int maxLength, out int currentLength);
    void Unlock();
    void GetCurrentLength(out int length);
    void SetCurrentLength(int length);
    void GetMaxLength(out int length);
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, nint value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, nint value, out int result);
    [PreserveSig] int Compare(IMFAttributes? other, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value);
    [PreserveSig] int GetUINT64(ref Guid key, out long value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, nint buffer, int bufferSize, out int length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out nint value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, byte[] buffer, int size, out int written);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out nint buffer, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out nint unknown);
    [PreserveSig] int SetItem(ref Guid key, nint value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, int value);
    [PreserveSig] int SetUINT64(ref Guid key, long value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, int size);
    [PreserveSig] int SetUnknown(ref Guid key, nint unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int items);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, nint value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSample
{
    [PreserveSig] int GetItem(ref Guid key, nint value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, nint value, out int result);
    [PreserveSig] int Compare(IMFAttributes? other, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value);
    [PreserveSig] int GetUINT64(ref Guid key, out long value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, nint buffer, int bufferSize, out int length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out nint value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, byte[] buffer, int size, out int written);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out nint buffer, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out nint unknown);
    [PreserveSig] int SetItem(ref Guid key, nint value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, int value);
    [PreserveSig] int SetUINT64(ref Guid key, long value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, int size);
    [PreserveSig] int SetUnknown(ref Guid key, nint unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int items);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, nint value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
    [PreserveSig] int GetSampleFlags(out int flags);
    [PreserveSig] int SetSampleFlags(int flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out int count);
    [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(int index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out int inputMin, out int inputMax, out int outputMin, out int outputMax);
    [PreserveSig] int GetStreamCount(out int inputs, out int outputs);
    [PreserveSig] int GetStreamIDs(int inputSize, nint inputIds, int outputSize, nint outputIds);
    [PreserveSig] int GetInputStreamInfo(int streamId, out MFT_INPUT_STREAM_INFO info);
    [PreserveSig] int GetOutputStreamInfo(int streamId, out MFT_OUTPUT_STREAM_INFO info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    [PreserveSig] int GetInputStreamAttributes(int streamId, out IMFAttributes attributes);
    [PreserveSig] int GetOutputStreamAttributes(int streamId, out IMFAttributes attributes);
    [PreserveSig] int DeleteInputStream(int streamId);
    [PreserveSig] int AddInputStreams(int count, nint ids);
    [PreserveSig] int GetInputAvailableType(int streamId, int index, out IMFAttributes type);
    [PreserveSig] int GetOutputAvailableType(int streamId, int index, out IMFAttributes type);
    [PreserveSig] int SetInputType(int streamId, IMFAttributes? type, int flags);
    [PreserveSig] int SetOutputType(int streamId, IMFAttributes? type, int flags);
    [PreserveSig] int GetInputCurrentType(int streamId, out IMFAttributes type);
    [PreserveSig] int GetOutputCurrentType(int streamId, out IMFAttributes type);
    [PreserveSig] int GetInputStatus(int streamId, out int flags);
    [PreserveSig] int GetOutputStatus(out int flags);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(int streamId, nint mediaEvent);
    [PreserveSig] int ProcessMessage(int message, nint parameter);
    [PreserveSig] int ProcessInput(int streamId, IMFSample sample, int flags);
    [PreserveSig] int ProcessOutput(int flags, int count, ref MFT_OUTPUT_DATA_BUFFER buffers, out int status);
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, out object min, out object max, out object stepping);
    [PreserveSig] int GetParameterValues(ref Guid api, out nint values, out int count);
    [PreserveSig] int GetDefaultValue(ref Guid api, out object value);
    [PreserveSig] int GetValue(ref Guid api, out object value);
    [PreserveSig] int SetValue(ref Guid api, ref object value);
}

[StructLayout(LayoutKind.Sequential)]
struct MFT_INPUT_STREAM_INFO
{
    public long MaxLatency;
    public int Flags, Size, Lookahead, Alignment;
}

[StructLayout(LayoutKind.Sequential)]
struct MFT_OUTPUT_STREAM_INFO
{
    public int Flags, Size, Alignment;
}

[StructLayout(LayoutKind.Sequential)]
struct MFT_OUTPUT_DATA_BUFFER
{
    public int StreamId;
    public IMFSample? Sample;
    public int Status;
    public nint Events;
}
