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
        var staged = destination + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.Copy(source, staged, false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // The staged file is in the destination directory, so Windows can replace
                    // the installed executable with one same-volume rename. A failed copy or
                    // rename leaves the last working executable untouched.
                    File.Move(staged, destination, true);
                    break;
                }
                catch (IOException ex) when (attempt < 20 && ((ex.HResult & 0xFFFF) == 32 || (ex.HResult & 0xFFFF) == 33))
                {
                    Thread.Sleep(250);
                }
            }
            return destination;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    public static string InstallExecutableTransactionally(
        string source,
        string destinationDirectory,
        Action prepareInstall,
        Action<string> finishInstall,
        Action prepareRollback,
        Action finishRollback)
    {
        ArgumentNullException.ThrowIfNull(prepareInstall);
        ArgumentNullException.ThrowIfNull(finishInstall);
        ArgumentNullException.ThrowIfNull(prepareRollback);
        ArgumentNullException.ThrowIfNull(finishRollback);
        if (!AppInstall.CanInstall(source)) throw new InvalidOperationException(UiText.InstallNeedsPublished());
        Directory.CreateDirectory(destinationDirectory);
        var destination = AppInstall.Executable(destinationDirectory);
        var hadPreviousExecutable = File.Exists(destination);
        var backup = hadPreviousExecutable ? destination + "." + Guid.NewGuid().ToString("N") + ".rollback" : null;
        if (backup != null)
        {
            try { File.Copy(destination, backup, false); }
            catch
            {
                TryDeleteBackup(backup);
                throw;
            }
        }

        var committed = false;
        var rollbackCompleted = false;
        try
        {
            prepareInstall();
            var installed = CopyExecutable(source, destinationDirectory);
            finishInstall(installed);
            committed = true;
            return installed;
        }
        catch (Exception installError)
        {
            try
            {
                prepareRollback();
                if (backup != null) File.Move(backup, destination, true);
                else if (File.Exists(destination)) File.Delete(destination);
                finishRollback();
                rollbackCompleted = true;
            }
            catch (Exception rollbackError)
            {
                var retainedBackup = backup != null && File.Exists(backup) ? backup : null;
                var recovery = retainedBackup == null ? "" : " 복구 백업 위치: " + retainedBackup;
                Trace.TraceError("Executable install rollback failed. Backup: {0}; error: {1}", retainedBackup ?? "none", rollbackError);
                throw new AggregateException("설치에 실패했고 복구 작업도 완료하지 못했습니다." + recovery, installError, rollbackError);
            }
            throw;
        }
        finally
        {
            if (backup != null && (committed || rollbackCompleted)) TryDeleteBackup(backup);
        }
    }

    private static void TryDeleteBackup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) { Trace.TraceWarning("Could not remove installer backup {0}: {1}", path, error.GetType().Name); }
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
        var machineDirectory = AppInstall.MachineDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var shortcut = AppInstall.ShortcutPath(commonPrograms);
        var hadShortcut = File.Exists(shortcut);
        var autostart = CaptureUserAutostart();
        var serviceState = default(ServiceUpdateState);
        var serviceStateKnown = false;
        _ = InstallExecutableTransactionally(source, machineDirectory,
            prepareInstall: () => serviceState = StopServiceForUpdate(observed =>
            {
                serviceState = observed;
                serviceStateKnown = true;
            }),
            finishInstall: installed =>
            {
                CreateShortcut(shortcut, installed);
                ClearUserAutostart();
                InstallService(installed);
                StartService();
                SecureAttention.TryEnsurePolicy();
                HostFirewall.TryApplySavedPort(installed);
            },
            prepareRollback: () =>
            {
                if (!serviceStateKnown) return;
                if (serviceState.Exists) _ = StopServiceForUpdate();
                else StopAndDeleteService();
            },
            finishRollback: () =>
            {
                if (serviceStateKnown) RestoreMachineInstallState(serviceState, autostart, shortcut, hadShortcut);
            });
    }

    private static void RestoreMachineInstallState(ServiceUpdateState serviceState, RegistryValueSnapshot autostart, string shortcut, bool hadShortcut)
    {
        var failures = new List<Exception>();
        try { RestoreUserAutostart(autostart); }
        catch (Exception error) { failures.Add(error); }
        if (!hadShortcut && File.Exists(shortcut))
        {
            try { File.Delete(shortcut); }
            catch (Exception error) { failures.Add(error); }
        }
        if (!serviceState.Exists)
        {
            try { SecureAttention.RestorePolicy(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (serviceState.WasRunning)
        {
            try { StartService(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException("이전 설치 상태로 완전히 돌아가지 못했습니다.", failures);
    }

    public static void UninstallMachineHere()
    {
        var executable = Environment.ProcessPath;
        var restoreFirewall = HostFirewall.HasRuleForProgram(executable);
        if (!HostFirewall.RemoveForProgram(executable))
            throw new InvalidOperationException(UiText.FirewallRemoveFailed());
        try { StopAndDeleteService(); }
        catch (Exception error)
        {
            if (restoreFirewall && !string.IsNullOrWhiteSpace(executable) && !HostFirewall.TryApplySavedPort(executable))
                Trace.TraceError("Machine uninstall could not stop the service and failed to restore its firewall rule: {0}", error);
            throw;
        }
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

    private static RegistryValueSnapshot CaptureUserAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        if (key == null || !key.GetValueNames().Contains(AppInstall.RunValueName, StringComparer.OrdinalIgnoreCase))
            return default;
        return new(true, key.GetValue(AppInstall.RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(AppInstall.RunValueName));
    }

    private static void RestoreUserAutostart(RegistryValueSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, true);
            existing?.DeleteValue(AppInstall.RunValueName, false);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new InvalidOperationException("로그인 자동 실행을 복원하지 못했습니다.");
        key.SetValue(AppInstall.RunValueName, snapshot.Value!, snapshot.Kind);
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
    const uint StartPending = 2;
    const uint Running = 4;
    const uint ErrorServiceSpecific = 1066;

    private readonly record struct ServiceUpdateState(bool Exists, bool WasRunning);
    private readonly record struct RegistryValueSnapshot(bool Exists, object? Value, RegistryValueKind Kind);

    static ServiceUpdateState StopServiceForUpdate(Action<ServiceUpdateState>? observe = null)
    {
        var manager = OpenSCManager(null, null, ManagerAccess);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, AppInstall.ServiceName, ServiceAccess);
            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060)
                {
                    var absent = new ServiceUpdateState(false, false);
                    observe?.Invoke(absent);
                    return absent;
                }
                throw new Win32Exception(error);
            }
            try
            {
                var status = new ServiceStatus();
                if (!QueryServiceStatus(service, ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var previousState = new ServiceUpdateState(true, status.CurrentState != Stopped);
                observe?.Invoke(previousState);
                if (!previousState.WasRunning) return previousState;
                if (!ControlService(service, StopControl, ref status))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1062) throw new Win32Exception(error);
                }
                var until = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < until)
                {
                    if (!QueryServiceStatus(service, ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (status.CurrentState == Stopped) return previousState;
                    Thread.Sleep(200);
                }
                throw new TimeoutException("기존 Windows 서비스를 중지하지 못했습니다.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

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
                try
                {
                    try { ConfigureServiceRecovery(created); }
                    catch
                    {
                        _ = DeleteService(created);
                        throw;
                    }
                }
                finally { CloseServiceHandle(created); }
                return;
            }
            try
            {
                if (!ChangeServiceConfig(existing, OwnProcess, AutoStart, ErrorNormal, binary, null, 0, null, null, null, AppInstall.ServiceDisplayName))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                ConfigureServiceRecovery(existing);
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
                WaitForServiceRunning(service);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    static void WaitForServiceRunning(nint service)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var status = new ServiceStatus();
            if (!QueryServiceStatus(service, ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (status.CurrentState == Running) return;
            if (status.CurrentState == Stopped)
            {
                var error = status.Win32ExitCode == ErrorServiceSpecific ? status.ServiceSpecificExitCode : status.Win32ExitCode;
                if (error == 0) error = 1066;
                throw new Win32Exception(unchecked((int)error), "Windows service stopped before it reported ready.");
            }
            if (status.CurrentState != StartPending) throw new InvalidOperationException("Windows service entered an unexpected state.");
            Thread.Sleep(200);
        }
        throw new TimeoutException("Windows service did not report ready within 30 seconds.");
    }

    static void ConfigureServiceRecovery(nint service)
    {
        var actionSize = Marshal.SizeOf<ServiceAction>();
        var actionBuffer = Marshal.AllocHGlobal(actionSize * ServiceRecoveryPolicy.Actions.Count);
        try
        {
            for (var index = 0; index < ServiceRecoveryPolicy.Actions.Count; index++)
            {
                var action = ServiceRecoveryPolicy.Actions[index];
                Marshal.StructureToPtr(new ServiceAction { Type = (uint)action.Type, Delay = action.DelayMilliseconds }, IntPtr.Add(actionBuffer, index * actionSize), false);
            }
            var recovery = new ServiceFailureActions
            {
                ResetPeriod = ServiceRecoveryPolicy.ResetPeriodSeconds,
                ActionCount = (uint)ServiceRecoveryPolicy.Actions.Count,
                Actions = actionBuffer
            };
            if (!ChangeServiceConfig2(service, 2, ref recovery)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(actionBuffer); }
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
                var deleted = ServiceStopPolicy.StopAndDeleteWhenStopped(
                    () =>
                    {
                        if (!QueryServiceStatus(service, ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        return status.CurrentState == Stopped;
                    },
                    () =>
                    {
                        if (DeleteService(service)) return;
                        var error = Marshal.GetLastWin32Error();
                        if (error != 1072) throw new Win32Exception(error);
                    },
                    maxChecks: 41,
                    wait: () => Thread.Sleep(200));
                if (!deleted)
                {
                    throw new TimeoutException("Windows 서비스를 멈추지 못해 설치 제거를 중단했습니다.");
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceAction { public uint Type, Delay; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ServiceFailureActions { public uint ResetPeriod; public nint RebootMessage, Command; public uint ActionCount; public nint Actions; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateService(nint manager, string name, string display, uint access, uint serviceType, uint startType, uint errorControl, string binary, string? group, nint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, string binary, string? group, nint tag, string? dependencies, string? account, string? password, string display);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ChangeServiceConfig2(nint service, uint infoLevel, ref ServiceFailureActions info);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartService(nint service, int argc, nint argv);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(nint service, uint control, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(nint service, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DeleteService(nint service);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(nint handle);
}
