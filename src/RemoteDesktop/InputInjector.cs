using System.Runtime.InteropServices;
using System.IO;
using System.Net.Sockets;

namespace RemoteDesktop;

internal sealed class InputInjector
{
    private readonly HashSet<int> keys = [];
    private readonly HashSet<int> buttons = [];
    private DateTime lastAttentionUtc = DateTime.MinValue;

    public string? ApplyCore(InputEvent e)
    {
        if (e.Kind == SecureAttention.Kind) return Attention();
        char[]? textCodeUnits = null;
        if (e.Kind == "text" && !UnicodeInputText.TryGetCodeUnits(e.Text, out textCodeUnits))
            throw new ArgumentException("Invalid text");
        if (!SecureDesktop.OwnsDesktop && SecureDesktop.Enabled)
        {
            var view = SecureDesktop.CachedObservation(DateTime.UtcNow).View;
            if (view != SecureView.Off)
            {
                if (view == SecureView.Live)
                {
                    if (!SecureDesktop.TryForward(e)) SecureDesktop.RecordSecureInputDrop(view);
                }
                else SecureDesktop.RecordSecureInputDrop(view);
                return null;
            }
        }
        lock (keys)
        {
            switch (e.Kind)
            {
                case "move":
                    if (!double.IsFinite(e.X) || !double.IsFinite(e.Y)) throw new ArgumentException("Invalid coordinates");
                    var point = Desktop.MapPoint(Desktop.Bounds(e.Display), System.Windows.Forms.SystemInformation.VirtualScreen, e.X, e.Y);
                    Send(new NativeInput { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = point.X, Y = point.Y, Flags = 0xC001 } } });
                    break;
                case "button":
                    if (e.Code is < 0 or > 2) throw new ArgumentException("Invalid button");
                    if (Mouse((uint)(e.Code == 0 ? (e.Down ? 2 : 4) : e.Code == 1 ? (e.Down ? 8 : 16) : (e.Down ? 32 : 64))))
                    { if (e.Down) buttons.Add(e.Code); else buttons.Remove(e.Code); }
                    break;
                case "wheel": Mouse(0x800, unchecked((uint)Math.Clamp(e.Code, -InputEvent.MaxWheelDelta, InputEvent.MaxWheelDelta))); break;
                case "key":
                    if (e.Code is < 1 or > 254) throw new ArgumentException("Invalid key");
                    if (Key((ushort)e.Code, 0, e.Down ? 0u : 2u))
                    { if (e.Down) keys.Add(e.Code); else keys.Remove(e.Code); }
                    break;
                case "text":
                    foreach (var codeUnit in textCodeUnits!) { Key(0, codeUnit, 4); Key(0, codeUnit, 6); }
                    break;
                case "release": ReleaseCore(); break;
                default: throw new ArgumentException("Unknown input");
            }
        }
        return null;
    }

    string? Attention()
    {
        try
        {
            switch (SecureAttention.Decide(SecureDesktop.OwnsDesktop, SecureDesktop.Enabled && SecureDesktop.ReadPort() != 0))
            {
                case SecureAttention.Delivery.Send:
                    var now = DateTime.UtcNow;
                    if (!SecureAttention.Ready(lastAttentionUtc, now)) return null;
                    lastAttentionUtc = now;
                    SecureAttention.Invoke();
                    return UiText.SecureAttentionSent();
                case SecureAttention.Delivery.Forward:
                    return SecureDesktop.TryForward(new InputEvent(SecureAttention.Kind))
                        ? UiText.SecureAttentionSent()
                        : UiText.SecureAttentionUnavailable();
                default:
                    return UiText.SecureAttentionUnavailable();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or IOException or InvalidOperationException or SocketException)
        {
            return UiText.SecureAttentionUnavailable();
        }
    }

    public void ReleaseCore()
    {
        lock (keys)
        {
            foreach (var key in keys) Key((ushort)key, 0, 2);
            foreach (var button in buttons) Mouse((uint)(button == 0 ? 4 : button == 1 ? 16 : 64));
            keys.Clear(); buttons.Clear();
        }
    }

    private static bool Mouse(uint flags, uint data = 0) => Send(new NativeInput { Data = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data } } });
    private static bool Key(ushort key, ushort scan, uint flags)
    {
        if (key is >= 33 and <= 46 or 91 or 92 or 111 or 163 or 165) flags |= 1;
        return Send(new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Scan = scan, Flags = flags } } });
    }
    private static bool Send(NativeInput input) => SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1;
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
