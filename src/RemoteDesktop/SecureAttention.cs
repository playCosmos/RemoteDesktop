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
    const string InstallStateKey = @"SOFTWARE\PersonalRemoteDesktop";
    const string PreviousPolicyValueName = "SoftwareSASGenerationPrevious";
    const int MissingPolicy = int.MinValue;

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

    public static int? RestorePolicyValue(int? current, int? previous)
    {
        var managed = PolicyValue(previous);
        return managed is int expected && current == expected ? previous : current;
    }

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
        if (next is not int value) return;
        using var installState = Registry.LocalMachine.CreateSubKey(InstallStateKey, true) ?? throw new InvalidOperationException("Ctrl+Alt+Del 정책 변경을 기록하지 못했습니다.");
        var previous = installState.GetValue(PreviousPolicyValueName);
        if (previous is not int saved || (saved != MissingPolicy && PolicyValue(saved) is null))
            installState.SetValue(PreviousPolicyValueName, current is int original ? original : MissingPolicy, RegistryValueKind.DWord);
        key.SetValue(PolicyValueName, value, RegistryValueKind.DWord);
    }

    public static void RestorePolicy()
    {
        var removeInstallState = false;
        using (var installState = Registry.LocalMachine.OpenSubKey(InstallStateKey, true))
        {
            if (installState is null) return;
            if (installState.GetValue(PreviousPolicyValueName) is int saved)
            {
                int? previous = saved == MissingPolicy ? null : saved;
                using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey, true);
                var current = policy?.GetValue(PolicyValueName) is int value ? value : (int?)null;
                var restored = RestorePolicyValue(current, previous);
                if (restored != current)
                {
                    if (restored is int original)
                    {
                        using var writable = Registry.LocalMachine.CreateSubKey(PolicyKey, true) ?? throw new InvalidOperationException("Ctrl+Alt+Del 정책을 복원하지 못했습니다.");
                        writable.SetValue(PolicyValueName, original, RegistryValueKind.DWord);
                    }
                    else policy?.DeleteValue(PolicyValueName, false);
                }
                installState.DeleteValue(PreviousPolicyValueName, false);
            }
            removeInstallState = installState.ValueCount == 0 && installState.SubKeyCount == 0;
        }
        if (removeInstallState) Registry.LocalMachine.DeleteSubKey(InstallStateKey, false);
    }

    public static void Invoke() => SendSAS(false);

    [DllImport("sas.dll")]
    static extern void SendSAS(bool asUser);
}
