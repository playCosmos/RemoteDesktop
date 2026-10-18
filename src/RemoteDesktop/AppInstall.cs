using System.IO;

namespace RemoteDesktop;

public enum InstallScope
{
    Portable,
    PerUser,
    Machine
}

public static class AppCommand
{
    public const string Service = "--service";
    public const string InstallMachine = "--install-machine";
    public const string UninstallMachine = "--uninstall-machine";
    public const string RemoveFirewall = "--remove-firewall";
    public const string WaitUi = "--wait-ui";
    public const string Firewall = "--firewall";

    public static bool Has(IEnumerable<string> args, string name)
    {
        foreach (var arg in args)
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public static class AppInstall
{
    public const string FolderName = "Remote Desktop";
    public const string ExeFileName = "RemoteDesktop.exe";
    public const string ShortcutName = "Remote Desktop.lnk";
    public const string RunValueName = "PersonalRemoteDesktop";
    public const string ServiceName = "PersonalRemoteDesktop";
    public const string ServiceDisplayName = "Personal Remote Desktop";

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string PerUserDirectory(string localAppData)
        => Path.Combine(localAppData, "Programs", FolderName);

    public static string MachineDirectory(string programFiles)
        => Path.Combine(programFiles, FolderName);

    public static string Executable(string directory)
        => Path.Combine(directory, ExeFileName);

    public static string ShortcutPath(string programsDirectory)
        => Path.Combine(programsDirectory, ShortcutName);

    public static string Quoted(string path)
    {
        var mark = ((char)34).ToString();
        return mark + path + mark;
    }

    public static string ServiceCommand(string executable)
        => Quoted(executable) + " " + AppCommand.Service;

    public static InstallScope ScopeOf(string executablePath, string perUserDirectory, string machineDirectory)
    {
        var parent = Normalize(Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? executablePath);
        if (parent.Equals(Normalize(machineDirectory), StringComparison.OrdinalIgnoreCase)) return InstallScope.Machine;
        if (parent.Equals(Normalize(perUserDirectory), StringComparison.OrdinalIgnoreCase)) return InstallScope.PerUser;
        return InstallScope.Portable;
    }

    public static bool CanInstall(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        string full;
        try { full = Path.GetFullPath(executablePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!Path.GetFileName(full).Equals(ExeFileName, StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory)) return false;
        if (File.Exists(Path.Combine(directory, "RemoteDesktop.dll"))) return false;
        if (File.Exists(Path.Combine(directory, "RemoteDesktop.deps.json"))) return false;
        return true;
    }

    public static bool CanRemoveDirectory(string directory, string programFiles, string localAppData)
    {
        string full;
        try { full = Normalize(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return full.Equals(Normalize(MachineDirectory(programFiles)), StringComparison.OrdinalIgnoreCase)
            || full.Equals(Normalize(PerUserDirectory(localAppData)), StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShowUserInstall(InstallScope scope) => scope == InstallScope.Portable;
    public static bool ShowMachineInstall(InstallScope scope) => scope != InstallScope.Machine;
    public static bool ShowAutostart(InstallScope scope) => scope is InstallScope.Portable or InstallScope.PerUser;
    public static bool ShowUninstall(InstallScope scope) => scope != InstallScope.Portable;

    public static bool AutostartMatches(string? registered, string? executable)
    {
        if (string.IsNullOrWhiteSpace(registered) || string.IsNullOrWhiteSpace(executable)) return false;
        var value = registered.Trim();
        if (value.Length >= 2 && value[0] == (char)34 && value[^1] == (char)34) value = value[1..^1];
        try { return Normalize(value).Equals(Normalize(executable), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
