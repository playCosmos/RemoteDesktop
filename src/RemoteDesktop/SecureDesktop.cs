using System.IO;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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
    public const int InputKeyLength = 32;
    const uint PipeAccessDuplex = 0x00000003;
    const uint PipeRejectRemoteClients = 0x00000008;
    const uint FileFlagOverlapped = 0x40000000;
    const uint FileFlagFirstPipeInstance = 0x00080000;
    public static bool OwnsDesktop { get; set; }
    static int lastView;
    static NamedPipeClientStream? forwarder;
    static readonly object forwardGate = new();
    static readonly object fileAccessGate = new();
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

    public static string PipePath(string commonAppData)
        => Path.Combine(commonAppData, "PersonalRemoteDesktop", "secure-input.pipe");

    public static string FramePath()
        => FramePath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    public static string PipePath()
        => PipePath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

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

    public static void SetViewerAccess(SecurityIdentifier? viewer)
    {
        lock (fileAccessGate)
        {
            var directory = Path.GetDirectoryName(FramePath())!;
            Directory.CreateDirectory(directory);
            new DirectoryInfo(directory).SetAccessControl(BuildDirectorySecurity(viewer));
            SetFileAccess(FramePath(), viewer);
            SetFileAccess(FramePath() + ".tmp", viewer);
            SetFileAccess(PipePath(), viewer);
            SetFileAccess(PipePath() + ".tmp", viewer);
        }
    }

    public static void Publish(byte[] jpeg, int width, int height)
    {
        lock (fileAccessGate)
        {
            var path = FramePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, Pack(DateTime.UtcNow, width, height, jpeg));
            File.Move(temp, path, true);
        }
    }

    public static void ClearFrame()
    {
        lock (fileAccessGate)
        {
            try { File.Delete(FramePath()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { File.Delete(FramePath() + ".tmp"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string NewPipeName() => "PersonalRemoteDesktop.Input." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static NamedPipeServerStream CreateInputServer(string pipeName, SecurityIdentifier? viewer)
    {
        if (!IsPipeName(pipeName)) throw new ArgumentException("Invalid secure input pipe name", nameof(pipeName));
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(BuildInputPipeSddl(viewer), 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var path = @"\\.\pipe\" + pipeName;
            var handle = CreateNamedPipe(path, PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance, PipeRejectRemoteClients, 1, 8192, 8192, 0, ref attributes);
            if (handle == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, safePipeHandle: new SafePipeHandle(handle, ownsHandle: true));
        }
        finally { _ = LocalFree(descriptor); }
    }

    public static PipeSecurity BuildInputPipeSecurity(SecurityIdentifier? viewer)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (viewer != null) security.AddAccessRule(new PipeAccessRule(viewer, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var accessSddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        security.SetSecurityDescriptorSddlForm(accessSddl + "S:(ML;;NW;;;ME)");
        return security;
    }

    public static string BuildInputPipeSddl(SecurityIdentifier? viewer)
        => BuildInputPipeSecurity(viewer).GetSecurityDescriptorSddlForm(AccessControlSections.Access) + "S:(ML;;NW;;;ME)";

    public static bool IsPipeName(string? pipeName)
    {
        const string prefix = "PersonalRemoteDesktop.Input.";
        if (pipeName is null || !pipeName.StartsWith(prefix, StringComparison.Ordinal) || pipeName.Length != prefix.Length + 32) return false;
        return pipeName.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;
    }

    public static void WriteEndpoint(string pipeName, ReadOnlySpan<byte> key, SecurityIdentifier? viewer)
    {
        if (!IsPipeName(pipeName) || key.Length != InputKeyLength) throw new ArgumentException("Invalid secure input endpoint");
        var path = PipePath();
        lock (fileAccessGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, pipeName + "\n" + Convert.ToBase64String(key));
            File.Move(temporary, path, true);
            SetFileAccess(path, viewer);
        }
    }

    public static void ClearEndpoint()
    {
        lock (fileAccessGate)
        {
            try { File.Delete(PipePath()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { File.Delete(PipePath() + ".tmp"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static bool TryParseEndpoint(string? text, out string pipeName, out byte[] key)
    {
        pipeName = "";
        key = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 || !IsPipeName(lines[0])) return false;
        try
        {
            key = Convert.FromBase64String(lines[1]);
            if (key.Length == InputKeyLength) { pipeName = lines[0]; return true; }
        }
        catch (FormatException) { }
        key = [];
        return false;
    }

    public static byte[] InputProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> challenge)
    {
        if (key.Length != InputKeyLength || challenge.Length != InputKeyLength) throw new ArgumentException("Invalid secure input challenge");
        return HMACSHA256.HashData(key, challenge);
    }

    public static bool VerifyInputProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof)
        => key.Length == InputKeyLength && challenge.Length == InputKeyLength && proof.Length == InputKeyLength
            && CryptographicOperations.FixedTimeEquals(InputProof(key, challenge), proof);

    public static async Task<bool> AuthenticateInputServerAsync(Stream stream, ReadOnlyMemory<byte> key, CancellationToken token)
    {
        if (key.Length != InputKeyLength) return false;
        var expectedKey = key.ToArray();
        var challenge = RandomNumberGenerator.GetBytes(InputKeyLength);
        await stream.WriteAsync(challenge, token);
        var proof = new byte[InputKeyLength];
        await stream.ReadExactlyAsync(proof, token);
        return VerifyInputProof(expectedKey, challenge, proof);
    }

    public static void AuthenticateInputClient(Stream stream, ReadOnlySpan<byte> key)
    {
        if (key.Length != InputKeyLength) throw new ArgumentException("Invalid secure input key");
        var challenge = new byte[InputKeyLength];
        stream.ReadExactly(challenge);
        stream.Write(InputProof(key, challenge));
    }

    public static int GetPipeClientProcessId(NamedPipeServerStream pipe)
        => GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId) && processId is > 0 and <= int.MaxValue
            ? (int)processId : 0;

    static bool ReadEndpoint(out string pipeName, out byte[] key)
    {
        pipeName = "";
        key = [];
        try
        {
            return TryParseEndpoint(File.ReadAllText(PipePath()), out pipeName, out key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public static int ReadPort() => ReadEndpoint(out _, out _) ? 1 : 0;

    public static DirectorySecurity BuildDirectorySecurity(SecurityIdentifier? viewer)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        AddRule(security, system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        if (viewer != null) AddRule(security, viewer, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        return security;
    }

    public static FileSecurity BuildFileSecurity(SecurityIdentifier? viewer)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        AddRule(security, system, FileSystemRights.FullControl, InheritanceFlags.None);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.None);
        if (viewer != null) AddRule(security, viewer, FileSystemRights.ReadAndExecute, InheritanceFlags.None);
        return security;
    }

    static void SetFileAccess(string path, SecurityIdentifier? viewer)
    {
        if (!File.Exists(path)) return;
        new FileInfo(path).SetAccessControl(BuildFileSecurity(viewer));
    }

    static void AddRule(FileSystemSecurity security, SecurityIdentifier identity, FileSystemRights rights, InheritanceFlags inheritance)
        => security.AddAccessRule(new FileSystemAccessRule(identity, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));

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
            case SecureAttention.Kind:
                writer.Write((byte)7);
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
            7 => new InputEvent(SecureAttention.Kind),
            _ => throw new InvalidDataException("bad secure input")
        };
    }

    public static void Forward(InputEvent input) => TryForward(input);

    public static bool TryForward(InputEvent input)
    {
        var payload = EncodeInput(input);
        lock (forwardGate)
        {
            try
            {
                if (!EnsureForwarder()) return false;
                var length = BitConverter.GetBytes(payload.Length);
                forwarder!.Write(length);
                forwarder.Write(payload);
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                forwarder?.Dispose();
                forwarder = null;
                return false;
            }
        }
    }

    static bool EnsureForwarder()
    {
        if (forwarder is { SafePipeHandle.IsClosed: false }) return true;
        forwarder?.Dispose();
        forwarder = null;
        if (!ReadEndpoint(out var pipeName, out var key)) return false;
        NamedPipeClientStream? client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            client.Connect(200);
            AuthenticateInputClient(client, key);
            forwarder = client;
            client = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            return false;
        }
        finally { client?.Dispose(); }
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
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public nint Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);
}
