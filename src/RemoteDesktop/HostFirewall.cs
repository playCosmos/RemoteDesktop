using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemoteDesktop;

public static class HostFirewall
{
    public const string RuleName = "Personal Remote Desktop";
    public const int DomainAndPrivate = 1 | 2;
    public const int Tcp = 6;
    public const int Inbound = 1;
    public const int Allow = 1;

    public enum Choice
    {
        Keep,
        Replace
    }

    public readonly record struct Plan(int Port, string Program);

    public readonly record struct Observed(bool Present, int Port, string Program, bool Enabled, int Profiles, int Protocol, int Direction, int Action);

    public static Observed Absent => new(false, 0, "", false, 0, 0, 0, 0);

    public static bool TryPlan(int port, string? program, out Plan plan)
    {
        plan = default;
        if (port is < 1024 or > 65535 || string.IsNullOrWhiteSpace(program)) return false;
        if (!Path.IsPathRooted(program)) return false;
        string full;
        try { full = Path.GetFullPath(program); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (full.IndexOf((char)34) >= 0 || full.IndexOf((char)13) >= 0 || full.IndexOf((char)10) >= 0) return false;
        if (!Path.GetFileName(full).Equals(AppInstall.ExeFileName, StringComparison.OrdinalIgnoreCase)) return false;
        plan = new Plan(port, full);
        return true;
    }

    public static Choice Decide(Plan plan, Observed? observed)
    {
        if (observed is not Observed rule) return Choice.Replace;
        if (!rule.Present || !rule.Enabled) return Choice.Replace;
        if (rule.Protocol != Tcp || rule.Direction != Inbound || rule.Action != Allow) return Choice.Replace;
        if (rule.Port != plan.Port || rule.Profiles != DomainAndPrivate) return Choice.Replace;
        return SameProgram(rule.Program, plan.Program) ? Choice.Keep : Choice.Replace;
    }

    public static string Command(int port) => AppCommand.Firewall + " " + port.ToString(CultureInfo.InvariantCulture);

    public static bool TryCommand(IReadOnlyList<string> args, out int port)
    {
        port = 0;
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], AppCommand.Firewall, StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && port is >= 1024 and <= 65535;
        }
        return false;
    }

    public static string DeleteArguments()
        => "advfirewall firewall delete rule name=" + AppInstall.Quoted(RuleName);

    public static bool ShouldRemove(Observed? observed, string? executable)
        => observed is Observed rule && rule.Present && SameProgram(rule.Program, executable);

    public static string AddArguments(Plan plan)
        => "advfirewall firewall add rule name=" + AppInstall.Quoted(RuleName)
            + " dir=in action=allow protocol=TCP localport=" + plan.Port.ToString(CultureInfo.InvariantCulture)
            + " program=" + AppInstall.Quoted(plan.Program)
            + " profile=domain,private enable=yes";

    public static Observed? ParseShow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("No rules match the specified criteria.", StringComparison.OrdinalIgnoreCase)) return Absent;
        var count = 0;
        Observed? parsed = null;
        foreach (var block in text.Split("Rule Name:", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseBlock(block, out var observed)) continue;
            count++;
            parsed = observed;
        }
        if (count == 0) return null;
        if (count > 1) return new Observed(true, 0, "", true, 0, 0, 0, 0);
        return parsed;
    }

    public static string Ensure(int port, string? program)
    {
        try
        {
            if (port is < 1024 or > 65535) return "";
            if (!TryPlan(port, program, out var plan)) return UiText.FirewallLeft();
            if (Decide(plan, TryObserve()) == Choice.Keep) return "";
            if (IsElevated()) return Apply(plan) ? UiText.FirewallOpened(port) : UiText.FirewallLeft();
            using var elevated = StartElevated(plan);
            if (elevated == null) return UiText.FirewallLeft();
            elevated.WaitForExit();
            return elevated.ExitCode == 0 ? UiText.FirewallOpened(port) : UiText.FirewallLeft();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return UiText.FirewallDeclined();
        }
        catch (Exception)
        {
            return UiText.FirewallLeft();
        }
    }

    public static bool ApplyCommand(IReadOnlyList<string> args)
    {
        try
        {
            if (!TryCommand(args, out var port) || !TryPlan(port, Environment.ProcessPath, out var plan)) return false;
            return Apply(plan);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryApplySavedPort(string executable)
    {
        try
        {
            return TryPlan(SavedPort(), executable, out var plan) && Apply(plan);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool HasRuleForProgram(string? executable)
        => ShouldRemove(TryObserve(), executable);

    public static bool RemoveForProgram(string? executable)
    {
        var observed = TryObserve();
        if (observed is null) return false;
        return !ShouldRemove(observed, executable) || RunNetsh(DeleteArguments());
    }

    static int SavedPort()
    {
        try
        {
            var port = HostSettings.Load().Port;
            return port is >= 1024 and <= 65535 ? port : 45831;
        }
        catch (Exception)
        {
            return 45831;
        }
    }

    static bool Apply(Plan plan)
    {
        RunNetsh(DeleteArguments());
        return RunNetsh(AddArguments(plan));
    }

    static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static Process? StartElevated(Plan plan)
        => Process.Start(new ProcessStartInfo(plan.Program, Command(plan.Port))
        {
            UseShellExecute = true,
            Verb = "runas"
        });

    static bool RunNetsh(string arguments)
    {
        using var process = Process.Start(Netsh(arguments));
        if (process == null) return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    static string? ReadShow()
    {
        try
        {
            var start = Netsh(ShowArguments());
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = Process.Start(start);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string ShowArguments()
        => "advfirewall firewall show rule name=" + AppInstall.Quoted(RuleName) + " verbose";

    static ProcessStartInfo Netsh(string arguments)
        => new(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

    static Observed? TryObserve()
    {
        var observed = TryObserveCom();
        return observed != null ? observed : ParseShow(ReadShow());
    }

    static Observed? TryObserveCom()
    {
        object? policy = null;
        object? rules = null;
        object? rule = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) return null;
            policy = Activator.CreateInstance(policyType);
            if (policy == null) return null;
            rules = policy.GetType().InvokeMember("Rules", BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, policy, null);
            if (rules == null) return null;
            try
            {
                rule = rules.GetType().InvokeMember("Item", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, rules, new object?[] { RuleName });
            }
            catch (Exception ex) when (IsMissing(ex))
            {
                return Absent;
            }
            if (rule == null) return Absent;
            return Read(rule);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Release(rule);
            Release(rules);
            Release(policy);
        }
    }

    static Observed Read(object rule)
    {
        var ports = Text(rule, "LocalPorts").Trim();
        var port = int.TryParse(ports, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return new Observed(true, port, Text(rule, "ApplicationName").Trim(), Flag(rule, "Enabled"), Number(rule, "Profiles"), Number(rule, "Protocol"), Number(rule, "Direction"), Number(rule, "Action"));
    }

    static string Text(object target, string name) => Convert.ToString(Property(target, name), CultureInfo.InvariantCulture) ?? "";

    static int Number(object target, string name) => Convert.ToInt32(Property(target, name), CultureInfo.InvariantCulture);

    static bool Flag(object target, string name)
    {
        var value = Property(target, name);
        if (value is bool flag) return flag;
        if (value is string text) return text.Equals("Yes", StringComparison.OrdinalIgnoreCase) || text.Equals("True", StringComparison.OrdinalIgnoreCase);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    static object? Property(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, null);

    static bool TryParseBlock(string block, out Observed observed)
    {
        observed = Absent;
        var enabled = Field(block, "Enabled");
        var direction = Field(block, "Direction");
        var profiles = Field(block, "Profiles");
        var protocol = Field(block, "Protocol");
        var localPort = Field(block, "LocalPort");
        var program = Field(block, "Program");
        var action = Field(block, "Action");
        if (enabled == null || direction == null || profiles == null || protocol == null || localPort == null || program == null || action == null) return false;
        var port = int.TryParse(localPort, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        observed = new Observed(true, port, program, Yes(enabled), ProfilesOf(profiles), ProtocolOf(protocol), DirectionOf(direction), ActionOf(action));
        return true;
    }

    static string? Field(string block, string label)
    {
        foreach (var raw in block.Split((char)10))
        {
            var line = raw.Trim().Trim((char)13);
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line[..colon].Trim().Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
            return line[(colon + 1)..].Trim();
        }
        return null;
    }

    static bool Yes(string value) => value.Equals("Yes", StringComparison.OrdinalIgnoreCase) || value.Equals("True", StringComparison.OrdinalIgnoreCase) || value == "1";

    static int DirectionOf(string value)
        => value.Equals("In", StringComparison.OrdinalIgnoreCase) ? Inbound : value.Equals("Out", StringComparison.OrdinalIgnoreCase) ? 2 : 0;

    static int ProtocolOf(string value)
        => value.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? Tcp : value.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? 17 : 0;

    static int ActionOf(string value)
        => value.Equals("Allow", StringComparison.OrdinalIgnoreCase) ? Allow : 0;

    static int ProfilesOf(string text)
    {
        if (text.Equals("Any", StringComparison.OrdinalIgnoreCase) || text.Equals("All", StringComparison.OrdinalIgnoreCase)) return 7;
        var value = 0;
        if (text.Contains("Domain", StringComparison.OrdinalIgnoreCase)) value |= 1;
        if (text.Contains("Private", StringComparison.OrdinalIgnoreCase)) value |= 2;
        if (text.Contains("Public", StringComparison.OrdinalIgnoreCase)) value |= 4;
        return value;
    }

    static bool IsMissing(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current.HResult == unchecked((int)0x80070002) || current.HResult == unchecked((int)0x80070490)) return true;
        return false;
    }

    static bool SameProgram(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(NormalizeProgram(left), NormalizeProgram(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    static string NormalizeProgram(string path)
    {
        var mark = (char)34;
        var text = path.Trim().Trim(mark);
        if (HasDevicePrefix(text)) text = text[4..];
        var full = Path.GetFullPath(text);
        var buffer = new char[32768];
        var length = GetLongPathName(full, buffer, buffer.Length);
        if (length <= 0 || length >= buffer.Length) return full;
        return new string(buffer, 0, length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetLongPathName(string shortPath, char[] buffer, int bufferSize);

    static bool HasDevicePrefix(string text)
    {
        var slash = (char)92;
        return text.Length >= 4
            && ((text[0] == slash && text[1] == slash && text[2] == '?' && text[3] == slash)
                || (text[0] == slash && text[1] == '?' && text[2] == '?' && text[3] == slash));
    }

    static void Release(object? com)
    {
        if (com == null || !Marshal.IsComObject(com)) return;
        try { Marshal.ReleaseComObject(com); }
        catch (ArgumentException) { }
    }
}
