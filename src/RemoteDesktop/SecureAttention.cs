using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace RemoteDesktop;

public static class SecureAttention
{
    public const string Kind = "attention";
    public const string PolicyValueName = "SoftwareSASGeneration";
    const string PolicyKey = "SOFTWARE" + @"\Microsoft\Windows\CurrentVersion\Policies\System";

    public enum Delivery
    {
        Send,
        Forward,
        Decline
    }

    public static bool CanSend(InstallScope scope) => scope == InstallScope.Machine;

    public static Delivery Decide(bool serviceOwnsDesktop, bool serviceListening)
        => serviceOwnsDesktop ? Delivery.Send : serviceListening ? Delivery.Forward : Delivery.Decline;

    public static bool Ready(DateTime previousUtc, DateTime nowUtc)
        => nowUtc >= previousUtc && nowUtc - previousUtc >= TimeSpan.FromSeconds(1);

    // 0 blocks a software secure attention sequence. 1 allows services, 2 allows Ease of Access, and 3 allows both.
    public static int? PolicyValue(int? current) => current switch
    {
        null or 0 => 1,
        2 => 3,
        _ => null
    };

    public static string? ReadNotice(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!json.RootElement.TryGetProperty("notice", out var notice) || notice.ValueKind != JsonValueKind.String) return null;
            var text = notice.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 240) return null;
            return text.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void TryEnsurePolicy()
    {
        try { EnsurePolicy(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException or ArgumentException)
        {
        }
    }

    public static void EnsurePolicy()
    {
        using var key = Registry.LocalMachine.CreateSubKey(PolicyKey, true) ?? throw new InvalidOperationException("Ctrl+Alt+Del 정책을 열지 못했습니다.");
        var current = key.GetValue(PolicyValueName);
        if (current is not null and not int) return;
        var next = PolicyValue(current is int number ? number : null);
        if (next is int value) key.SetValue(PolicyValueName, value, RegistryValueKind.DWord);
    }

    public static void Invoke() => SendSAS(false);

    [DllImport("sas.dll")]
    static extern void SendSAS(bool asUser);
}
