using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteDesktop;

public static class AppInstaller
{
    public readonly record struct PerUserRequest(string SourceExecutable, string DestinationDirectory, string ProgramsDirectory, bool StartWithWindows);

    public static string CopyExecutable(string source, string destinationDirectory)
    {
        if (!AppInstall.CanInstall(source)) throw new InvalidOperationException(UiText.InstallNeedsPublished());
        Directory.CreateDirectory(destinationDirectory);
        var destination = AppInstall.Executable(destinationDirectory);
        File.Copy(source, destination, true);
        return destination;
    }

    public static string InstallPerUser(PerUserRequest request)
    {
        var destination = CopyExecutable(request.SourceExecutable, request.DestinationDirectory);
        CreateShortcut(AppInstall.ShortcutPath(request.ProgramsDirectory), destination);
        if (request.StartWithWindows) SetUserAutostart(destination);
        else ClearUserAutostart();
        return destination;
    }

    public static void InstallMachineHere()
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException(UiText.InstallNeedsPublished());
        var destination = CopyExecutable(source, AppInstall.MachineDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        CreateShortcut(AppInstall.ShortcutPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)), destination);
        ClearUserAutostart();
        InstallService(destination);
        SecureAttention.TryEnsurePolicy();
        HostFirewall.TryApplySavedPort(destination);
        StartService();
    }

    public static void UninstallMachineHere()
    {
        if (!HostFirewall.RemoveForProgram(Environment.ProcessPath))
            throw new InvalidOperationException(UiText.FirewallRemoveFailed());
        StopAndDeleteService();
        SecureAttention.RestorePolicy();
        var shortcut = AppInstall.ShortcutPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        if (File.Exists(shortcut)) File.Delete(shortcut);
        DeleteAfterExit(AppInstall.MachineDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }

    public static void CreateShortcut(string shortcutPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("바로 가기를 만들지 못했습니다.");
        dynamic shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException("바로 가기를 만들지 못했습니다.");
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = AppInstall.ServiceDisplayName;
            link.Save();
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    public static void SetUserAutostart(string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new InvalidOperationException("로그인 자동 실행을 저장하지 못했습니다.");
        key.SetValue(AppInstall.RunValueName, AppInstall.Quoted(executable));
    }

    public static void ClearUserAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(AppInstall.RunValueName, false);
    }

    public static bool UserAutostartEnabled(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return AppInstall.AutostartMatches(key?.GetValue(AppInstall.RunValueName) as string, executable);
    }

    public static void RetirePerUserInstall(string localAppData, string programsDirectory)
    {
        ClearUserAutostart();
        var shortcut = AppInstall.ShortcutPath(programsDirectory);
        if (File.Exists(shortcut)) File.Delete(shortcut);
        var directory = AppInstall.PerUserDirectory(localAppData);
        if (Directory.Exists(directory)) DeleteAfterExit(directory);
    }

    public static void DeleteAfterExit(string directory)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!AppInstall.CanRemoveDirectory(directory, programFiles, localAppData))
            throw new InvalidOperationException("설치 폴더가 아니라 지우지 않습니다.");
        if (directory.Contains((char)34)) throw new InvalidOperationException("설치 폴더를 지우지 못했습니다.");
        var mark = ((char)34).ToString();
        var command = "ping 127.0.0.1 -n 4 >nul & rmdir /s /q " + mark + directory + mark;
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void Relaunch(string executable)
    {
        Process.Start(new ProcessStartInfo(executable, AppCommand.WaitUi) { UseShellExecute = true });
        AppLifetime.RequestExit(force: true);
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const uint ManagerAccess = 0xF003F;
    const uint ServiceAccess = 0xF01FF;
    const uint OwnProcess = 0x10;
    const uint AutoStart = 2;
    const uint ErrorNormal = 1;
    const uint StopControl = 1;
    const uint Stopped = 1;

    static void InstallService(string executable)
    {
        var manager = OpenSCManager(null, null, ManagerAccess);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var binary = AppInstall.ServiceCommand(executable);
            var existing = OpenService(manager, AppInstall.ServiceName, ServiceAccess);
            if (existing == 0)
            {
                var created = CreateService(manager, AppInstall.ServiceName, AppInstall.ServiceDisplayName, ServiceAccess, OwnProcess, AutoStart, ErrorNormal, binary, null, 0, null, null, null);
                if (created == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseServiceHandle(created);
                return;
            }
            try
            {
                if (!ChangeServiceConfig(existing, OwnProcess, AutoStart, ErrorNormal, binary, null, 0, null, null, null, AppInstall.ServiceDisplayName))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { CloseServiceHandle(existing); }
        }
        finally { CloseServiceHandle(manager); }
    }

    static void StartService()
    {
        var manager = OpenSCManager(null, null, ManagerAccess);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, AppInstall.ServiceName, ServiceAccess);
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!StartService(service, 0, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1056) throw new Win32Exception(error);
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    static void StopAndDeleteService()
    {
        var manager = OpenSCManager(null, null, ManagerAccess);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, AppInstall.ServiceName, ServiceAccess);
            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060) return;
                throw new Win32Exception(error);
            }
            try
            {
                var status = new ServiceStatus();
                if (!ControlService(service, StopControl, ref status))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1062) throw new Win32Exception(error);
                }
                var until = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < until)
                {
                    if (!QueryServiceStatus(service, ref status)) break;
                    if (status.CurrentState == Stopped) break;
                    Thread.Sleep(200);
                }
                if (!DeleteService(service))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1072) throw new Win32Exception(error);
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateService(nint manager, string name, string display, uint access, uint serviceType, uint startType, uint errorControl, string binary, string? group, nint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, string binary, string? group, nint tag, string? dependencies, string? account, string? password, string display);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartService(nint service, int argc, nint argv);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(nint service, uint control, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(nint service, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DeleteService(nint service);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(nint handle);
}
