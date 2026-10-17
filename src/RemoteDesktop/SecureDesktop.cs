using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemoteDesktop;

public enum SecureView
{
    Off,
    Hidden,
    Live
}

public readonly record struct SecureObservation(SecureView View, byte[]? Jpeg);

public static class SecureDesktop
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);
    public static bool OwnsDesktop { get; set; }
    static int lastView;
    static TcpClient? forwarder;
    static readonly object forwardGate = new();
    static long nextProbeTicks;
    static int deniedProbe;

    public static SecureView LastView => (SecureView)Volatile.Read(ref lastView);

    public static SecureView Decide(bool inputDenied, bool frameFresh)
        => !inputDenied ? SecureView.Off : frameFresh ? SecureView.Live : SecureView.Hidden;

    public static bool IsSecureName(string? name)
        => name != null && (name.Equals("Winlogon", StringComparison.OrdinalIgnoreCase) || name.Equals("Disconnect", StringComparison.OrdinalIgnoreCase));

    public static string Header(SecureView view) => view switch
    {
        SecureView.Live => "live",
        SecureView.Hidden => "hidden",
        _ => "off"
    };

    public static string FramePath(string commonAppData)
        => Path.Combine(commonAppData, "PersonalRemoteDesktop", "secure-desktop.bin");

    public static string PortPath(string commonAppData)
        => Path.Combine(commonAppData, "PersonalRemoteDesktop", "secure-input.port");

    public static string FramePath()
        => FramePath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    public static string PortPath()
        => PortPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    public static byte[] Pack(DateTime utc, int width, int height, ReadOnlySpan<byte> jpeg)
    {
        if (width < 1 || height < 1 || jpeg.Length == 0) throw new ArgumentException("Invalid secure frame");
        var packed = new byte[24 + jpeg.Length];
        packed[0] = (byte)'P'; packed[1] = (byte)'R'; packed[2] = (byte)'S'; packed[3] = (byte)'D';
        BitConverter.TryWriteBytes(packed.AsSpan(4), utc.ToUniversalTime().Ticks);
        BitConverter.TryWriteBytes(packed.AsSpan(12), width);
        BitConverter.TryWriteBytes(packed.AsSpan(16), height);
        BitConverter.TryWriteBytes(packed.AsSpan(20), jpeg.Length);
        jpeg.CopyTo(packed.AsSpan(24));
        return packed;
    }

    public static bool TryUnpack(ReadOnlySpan<byte> packed, DateTime now, TimeSpan maxAge, out byte[] jpeg)
    {
        jpeg = [];
        if (packed.Length < 24 || packed[0] != (byte)'P' || packed[1] != (byte)'R' || packed[2] != (byte)'S' || packed[3] != (byte)'D') return false;
        var ticks = BitConverter.ToInt64(packed.Slice(4, 8));
        var width = BitConverter.ToInt32(packed.Slice(12, 4));
        var height = BitConverter.ToInt32(packed.Slice(16, 4));
        var length = BitConverter.ToInt32(packed.Slice(20, 4));
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || length < 1 || packed.Length != 24 + length) return false;
        DateTime stamp;
        try { stamp = new DateTime(ticks, DateTimeKind.Utc); }
        catch (ArgumentException) { return false; }
        var utc = now.ToUniversalTime();
        if (stamp - utc > TimeSpan.FromSeconds(2) || utc - stamp > maxAge) return false;
        jpeg = packed.Slice(24, length).ToArray();
        return true;
    }

    public static void Publish(byte[] jpeg, int width, int height)
    {
        var path = FramePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Pack(DateTime.UtcNow, width, height, jpeg));
        File.Move(temp, path, true);
    }

    public static void ClearFrame()
    {
        try { File.Delete(FramePath()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void WritePort(int port)
    {
        var path = PortPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void ClearPort()
    {
        try { File.Delete(PortPath()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static int ReadPort()
    {
        try
        {
            var text = File.ReadAllText(PortPath()).Trim();
            return int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return 0; }
    }

    public static SecureObservation Observe(DateTime now)
    {
        var denied = InputDenied();
        byte[]? jpeg = null;
        var fresh = denied && TryReadFresh(now, out jpeg);
        var view = Decide(denied, fresh);
        Volatile.Write(ref lastView, (int)view);
        return new SecureObservation(view, view == SecureView.Live ? jpeg : null);
    }

    public static byte[] EncodeInput(InputEvent input)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)1);
        switch (input.Kind)
        {
            case "move":
                writer.Write((byte)1);
                writer.Write(input.X);
                writer.Write(input.Y);
                break;
            case "button":
                writer.Write((byte)2);
                writer.Write(input.Code);
                writer.Write(input.Down);
                break;
            case "wheel":
                writer.Write((byte)3);
                writer.Write(input.Code);
                break;
            case "key":
                writer.Write((byte)4);
                writer.Write(input.Code);
                writer.Write(input.Down);
                break;
            case "text":
                if (input.Text is null || input.Text.Length > 4096) throw new ArgumentException("Invalid text");
                writer.Write((byte)5);
                writer.Write(input.Text);
                break;
            case "release":
                writer.Write((byte)6);
                break;
            default: throw new ArgumentException("Unknown input");
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static InputEvent DecodeInput(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadByte() != 1) throw new InvalidDataException("bad secure input");
        return reader.ReadByte() switch
        {
            1 => new InputEvent("move", reader.ReadDouble(), reader.ReadDouble()),
            2 => new InputEvent("button", Code: reader.ReadInt32(), Down: reader.ReadBoolean()),
            3 => new InputEvent("wheel", Code: reader.ReadInt32()),
            4 => new InputEvent("key", Code: reader.ReadInt32(), Down: reader.ReadBoolean()),
            5 => new InputEvent("text", Text: reader.ReadString()),
            6 => new InputEvent("release"),
            _ => throw new InvalidDataException("bad secure input")
        };
    }

    public static void Forward(InputEvent input)
    {
        var payload = EncodeInput(input);
        lock (forwardGate)
        {
            try
            {
                if (!EnsureForwarder()) return;
                var length = BitConverter.GetBytes(payload.Length);
                var stream = forwarder!.GetStream();
                stream.Write(length);
                stream.Write(payload);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                forwarder?.Dispose();
                forwarder = null;
            }
        }
    }

    static bool EnsureForwarder()
    {
        if (forwarder is { Connected: true }) return true;
        forwarder?.Dispose();
        forwarder = null;
        var port = ReadPort();
        if (port == 0) return false;
        var client = new TcpClient();
        var task = client.ConnectAsync(IPAddress.Loopback, port);
        if (!task.Wait(200) || !client.Connected) { client.Dispose(); return false; }
        forwarder = client;
        return true;
    }

    static bool TryReadFresh(DateTime now, out byte[]? jpeg)
    {
        jpeg = null;
        try
        {
            using var stream = new FileStream(FramePath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length is < 24 or > 12_000_000) return false;
            var packed = new byte[stream.Length];
            stream.ReadExactly(packed);
            if (!TryUnpack(packed, now, FreshFor, out var frame)) return false;
            jpeg = frame;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException) { return false; }
    }

    static bool InputDenied()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now < Volatile.Read(ref nextProbeTicks)) return Volatile.Read(ref deniedProbe) == 1;
        var desk = OpenInputDesktop(0, false, 0x0001);
        var denied = desk == 0 && Marshal.GetLastWin32Error() == 5;
        if (desk != 0) CloseDesktop(desk);
        Volatile.Write(ref deniedProbe, denied ? 1 : 0);
        Volatile.Write(ref nextProbeTicks, now + TimeSpan.TicksPerMillisecond * 100);
        return denied;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(nint desktop);
}
