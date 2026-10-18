using System.IO;
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace RemoteDesktop;

public static class ServiceHost
{
    const int OwnProcess = 0x10;
    const int AcceptStop = 0x1;
    const int AcceptSession = 0x80;
    const int ControlStop = 0x1;
    const int ControlSession = 0xE;
    const int StartPending = 0x2;
    const int Running = 0x4;
    const int StopPending = 0x3;
    const int Stopped = 0x1;

    delegate void ServiceMainDelegate(int argc, nint argv);
    delegate int HandlerDelegate(int control, int eventType, nint eventData, nint context);

    static readonly ServiceMainDelegate MainCallback = ServiceMain;
    static readonly HandlerDelegate HandlerCallback = Handler;
    static nint statusHandle;
    static SessionService? session;

    public static void Run()
    {
        var table = new ServiceTableEntry[2];
        table[0].Name = AppInstall.ServiceName;
        table[0].Main = Marshal.GetFunctionPointerForDelegate(MainCallback);
        if (!StartServiceCtrlDispatcher(table))
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
    }

    static void ServiceMain(int argc, nint argv)
    {
        statusHandle = RegisterServiceCtrlHandlerEx(AppInstall.ServiceName, HandlerCallback, 0);
        Report(StartPending, AcceptStop | AcceptSession, 4000);
        session = new SessionService();
        session.Start();
        Report(Running, AcceptStop | AcceptSession, 0);
    }

    static int Handler(int control, int eventType, nint eventData, nint context)
    {
        if (control == ControlStop)
        {
            Report(StopPending, 0, 4000);
            session?.Stop();
            Report(Stopped, 0, 0);
        }
        else if (control == ControlSession) session?.SessionChanged(eventType);
        return 0;
    }

    static void Report(int state, int accepted, int wait)
    {
        var status = new ServiceStatus { ServiceType = OwnProcess, CurrentState = state, ControlsAccepted = accepted, WaitHint = wait };
        SetServiceStatus(statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ServiceTableEntry { public string? Name; public nint Main; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint RegisterServiceCtrlHandlerEx(string name, HandlerDelegate handler, nint context);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
}

sealed class SessionService
{
    const uint DesktopRights = 0x1FF;
    readonly CancellationTokenSource cancel = new();
    readonly ConcurrentQueue<InputEvent> inputs = new();
    readonly InputInjector injector = new();
    readonly SemaphoreSlim inputClients = new(1, 1);
    readonly ConcurrentDictionary<NamedPipeServerStream, byte> activeClients = new();
    readonly object inputChannelGate = new();
    byte[] inputKey = RandomNumberGenerator.GetBytes(SecureDesktop.InputKeyLength);
    string inputPipeName = SecureDesktop.NewPipeName();
    int allowFramePublish;
    int trustedUiPid;
    uint trustedUiSession;
    SecurityIdentifier? trustedViewer;
    Thread? desktopThread;
    NamedPipeServerStream? pendingPipe;

    public void Start()
    {
        SecureDesktop.EnsurePipeSecurityPrivilege();
        SecureAttention.TryEnsurePolicy();
        SecureDesktop.SetViewerAccess(null);
        SecureDesktop.ClearFrame();
        SecureDesktop.ClearEndpoint();
        Volatile.Write(ref allowFramePublish, 1);
        SecureDesktop.WriteEndpoint(inputPipeName, inputKey, null);
        SecureDesktop.OwnsDesktop = true;
        _ = AcceptInputs(cancel.Token);
        desktopThread = new Thread(DesktopLoop) { IsBackground = true, Name = "Secure desktop" };
        desktopThread.SetApartmentState(ApartmentState.STA);
        desktopThread.Start();
        LaunchUi();
        _ = MonitorUiProcess(cancel.Token);
    }

    public void Stop()
    {
        cancel.Cancel();
        Volatile.Write(ref allowFramePublish, 0);
        lock (inputChannelGate)
        {
            pendingPipe?.Dispose();
            pendingPipe = null;
            foreach (var client in activeClients.Keys) client.Dispose();
            while (inputs.TryDequeue(out _)) { }
        }
        desktopThread?.Join(2000);
        try { SecureDesktop.SetViewerAccess(null); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        SecureDesktop.ClearFrame();
        SecureDesktop.ClearEndpoint();
        SecureDesktop.OwnsDesktop = false;
    }

    public void SessionChanged(int reason)
    {
        if (reason is 2 or 4 or 6)
        {
            try { ResetInputChannel(null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { }
            SecureDesktop.ClearFrame();
        }
        else if (reason is 1 or 3 or 5 or 8) LaunchUi();
    }

    async Task AcceptInputs(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            string expectedPipeName;
            SecurityIdentifier? expectedViewer;
            byte[] expectedKey;
            int expectedUiPid;
            uint expectedUiSession;
            lock (inputChannelGate)
            {
                expectedPipeName = inputPipeName;
                expectedViewer = trustedViewer;
                expectedKey = inputKey;
                expectedUiPid = trustedUiPid;
                expectedUiSession = trustedUiSession;
            }
            try { pipe = SecureDesktop.CreateInputServer(expectedPipeName, expectedViewer); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                if (token.IsCancellationRequested) return;
                await Task.Delay(100, token);
                continue;
            }
            lock (inputChannelGate)
            {
                if (inputPipeName != expectedPipeName || token.IsCancellationRequested)
                {
                    pipe.Dispose();
                    continue;
                }
                pendingPipe = pipe;
            }
            try { await pipe.WaitForConnectionAsync(token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                lock (inputChannelGate) if (ReferenceEquals(pendingPipe, pipe)) pendingPipe = null;
                pipe.Dispose();
                if (token.IsCancellationRequested) return;
                continue;
            }
            lock (inputChannelGate)
            {
                if (!ReferenceEquals(pendingPipe, pipe) || inputPipeName != expectedPipeName || token.IsCancellationRequested)
                {
                    pipe.Dispose();
                    continue;
                }
                pendingPipe = null;
                activeClients.TryAdd(pipe, 0);
            }
            if (!inputClients.Wait(0)) { pipe.Dispose(); activeClients.TryRemove(pipe, out _); continue; }
            var clientPid = SecureDesktop.GetPipeClientProcessId(pipe);
            if (!SecureDesktop.AuthorizeInputClient(clientPid, expectedUiPid, WTSGetActiveConsoleSessionId(), expectedUiSession)
                || !IsTrustedUiProcess(expectedUiPid, expectedUiSession, Environment.ProcessPath ?? ""))
            {
                pipe.Dispose();
                activeClients.TryRemove(pipe, out _);
                inputClients.Release();
                continue;
            }
            await ReadInputs(pipe, expectedKey, expectedUiPid, expectedUiSession, token);
        }
    }

    async Task ReadInputs(NamedPipeServerStream pipe, byte[] expectedKey, int expectedUiPid, uint expectedUiSession, CancellationToken token)
    {
        try
        {
            using (pipe)
            {
                var executable = Environment.ProcessPath ?? "";
                if (!IsTrustedUiProcess(expectedUiPid, expectedUiSession, executable)) return;
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    if (!await SecureDesktop.AuthenticateInputServerAsync(pipe, expectedKey, handshakeTimeout.Token)) return;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
                var lengthBytes = new byte[4];
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!SecureDesktop.AuthorizeInputClient(expectedUiPid, expectedUiPid, WTSGetActiveConsoleSessionId(), expectedUiSession)
                            || !IsTrustedUiProcess(expectedUiPid, expectedUiSession, executable)) return;
                        await pipe.ReadExactlyAsync(lengthBytes, token);
                        var length = BitConverter.ToInt32(lengthBytes);
                        if (length is < 2 or > 8192) return;
                        var body = new byte[length];
                        await pipe.ReadExactlyAsync(body, token);
                        var input = SecureDesktop.DecodeInput(body);
                        while (inputs.Count >= 32 && inputs.TryDequeue(out _)) { }
                        inputs.Enqueue(input);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or OperationCanceledException or ArgumentException or FormatException) { return; }
                }
            }
        }
        finally
        {
            activeClients.TryRemove(pipe, out _);
            inputClients.Release();
        }
    }

    void ResetInputChannel(SecurityIdentifier? viewer, uint viewerSession = 0)
    {
        Volatile.Write(ref allowFramePublish, 0);
        lock (inputChannelGate)
        {
            if (viewer == null || trustedViewer == null || !trustedViewer.Equals(viewer) || trustedUiSession != viewerSession)
                trustedUiPid = 0;
            trustedViewer = viewer;
            trustedUiSession = viewer == null ? 0 : viewerSession;
            if (trustedUiPid > 0 && !IsTrustedUiProcess(trustedUiPid, trustedUiSession, Environment.ProcessPath ?? "")) trustedUiPid = 0;
            inputKey = RandomNumberGenerator.GetBytes(SecureDesktop.InputKeyLength);
            inputPipeName = SecureDesktop.NewPipeName();
            pendingPipe?.Dispose();
            pendingPipe = null;
            foreach (var client in activeClients.Keys) client.Dispose();
            while (inputs.TryDequeue(out _)) { }
            SecureDesktop.ClearFrame();
            SecureDesktop.SetViewerAccess(viewer);
            SecureDesktop.WriteEndpoint(inputPipeName, inputKey, viewer);
        }
        Volatile.Write(ref allowFramePublish, 1);
    }

    void DesktopLoop()
    {
        var onSecure = false;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var secure = PublishIfSecure();
                if (onSecure && !secure) injector.ReleaseCore();
                onSecure = secure;
                if (secure)
                {
                    while (inputs.TryDequeue(out var input)) injector.ApplyCore(input);
                }
                else
                {
                    while (inputs.TryDequeue(out var input))
                    {
                        if (input.Kind == SecureAttention.Kind) injector.ApplyCore(input);
                    }
                }
            }
            catch (Exception ex) when (ex is ExternalException or IOException or InvalidOperationException or Win32Exception or ArgumentException) { }
            Thread.Sleep(80);
        }
        if (onSecure) injector.ReleaseCore();
        SecureDesktop.ClearFrame();
    }

    bool PublishIfSecure()
    {
        var desktop = OpenInputDesktop(0, false, DesktopRights);
        if (desktop == 0) { SecureDesktop.ClearFrame(); return false; }
        try
        {
            if (!SetThreadDesktop(desktop)) return false;
            var name = DesktopName(desktop);
            if (!SecureDesktop.IsSecureName(name)) { SecureDesktop.ClearFrame(); return false; }
            if (Volatile.Read(ref allowFramePublish) == 0) { SecureDesktop.ClearFrame(); return true; }
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var width = Math.Clamp(GetSystemMetrics(78), 1, 7680);
            var height = Math.Clamp(GetSystemMetrics(79), 1, 4320);
            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            lock (inputChannelGate)
            {
                if (Volatile.Read(ref allowFramePublish) == 0) { SecureDesktop.ClearFrame(); return true; }
                SecureDesktop.Publish(ScreenJpeg.Encode(bitmap, 55), width, height);
            }
            return true;
        }
        finally { CloseDesktop(desktop); }
    }

    static string? DesktopName(nint desktop)
    {
        GetUserObjectInformation(desktop, 2, 0, 0, out var needed);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetUserObjectInformation(desktop, 2, buffer, needed, out _)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    void LaunchUi()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return;
        if (!WTSQueryUserToken(session, out var user)) return;
        try
        {
            var viewer = TokenUserSid(user);
            if (viewer == null) return;
            try { ResetInputChannel(viewer, session); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                SecureDesktop.ClearFrame();
                return;
            }
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var existingUi = FindExistingUiProcess(executable, session);
            lock (inputChannelGate)
            {
                if (trustedUiPid == 0 && trustedViewer?.Equals(viewer) == true && trustedUiSession == session)
                    trustedUiPid = existingUi;
            }
            if (!DuplicateTokenEx(user, 0x10000000, 0, 2, 1, out var primary)) return;
            try
            {
                if (!CreateEnvironmentBlock(out var environment, primary, false)) environment = 0;
                try
                {
                    var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = "winsta0" + (char)92 + "default", showWindow = 5, flags = 1 };
                    if (!CreateProcessAsUser(primary, null, AppInstall.Quoted(executable), 0, 0, false, 0x400, environment, Path.GetDirectoryName(executable), ref startup, out var process)) return;
                    var childPid = process.ProcessId is > 0 and <= int.MaxValue ? (int)process.ProcessId : 0;
                    var wait = WaitForSingleObject(process.Process, 300);
                    if (childPid > 0 && wait != 0 && IsTrustedUiProcess(childPid, session, executable))
                    {
                        lock (inputChannelGate)
                        {
                            if (trustedUiPid == 0 && trustedViewer?.Equals(viewer) == true && trustedUiSession == session)
                                trustedUiPid = childPid;
                        }
                    }
                    if (childPid > 0 && trustedUiPid == 0)
                    {
                        var candidate = FindExistingUiProcess(executable, session);
                        lock (inputChannelGate)
                        {
                            if (trustedUiPid == 0 && trustedViewer?.Equals(viewer) == true && trustedUiSession == session)
                                trustedUiPid = candidate;
                        }
                    }
                    CloseHandle(process.Thread);
                    CloseHandle(process.Process);
                }
                finally { if (environment != 0) DestroyEnvironmentBlock(environment); }
            }
            finally { CloseHandle(primary); }
        }
        finally { CloseHandle(user); }
    }

    async Task MonitorUiProcess(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
            catch (OperationCanceledException) { return; }
            int pid;
            uint session;
            SecurityIdentifier? viewer;
            lock (inputChannelGate)
            {
                pid = trustedUiPid;
                session = trustedUiSession;
                viewer = trustedViewer;
            }
            if (viewer == null || session == 0xFFFFFFFF) continue;
            if (WTSGetActiveConsoleSessionId() != session)
            {
                try { ResetInputChannel(null); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { }
                SecureDesktop.ClearFrame();
                continue;
            }
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) continue;
            if (pid > 0 && IsTrustedUiProcess(pid, session, executable)) continue;
            var channelReset = false;
            if (pid > 0)
            {
                lock (inputChannelGate)
                {
                    if (trustedUiPid == pid && trustedUiSession == session && trustedViewer?.Equals(viewer) == true)
                        trustedUiPid = 0;
                }
                try { ResetInputChannel(viewer, session); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
                {
                    SecureDesktop.ClearFrame();
                    continue;
                }
                channelReset = true;
            }
            if (!WTSQueryUserToken(session, out var user)) continue;
            SecurityIdentifier? activeViewer;
            try { activeViewer = TokenUserSid(user); }
            finally { CloseHandle(user); }
            if (activeViewer == null || !activeViewer.Equals(viewer)) continue;
            var candidate = FindExistingUiProcess(executable, session);
            if (candidate <= 0) continue;
            if (!channelReset)
            {
                try { ResetInputChannel(viewer, session); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
                {
                    SecureDesktop.ClearFrame();
                    continue;
                }
            }
            lock (inputChannelGate)
            {
                if (trustedUiPid == 0 && trustedUiSession == session && trustedViewer?.Equals(viewer) == true)
                    trustedUiPid = candidate;
            }
        }
    }

    static int FindExistingUiProcess(string executable, uint session)
    {
        var expected = AppInstall.Normalize(executable);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || (uint)process.SessionId != session) continue;
                    var image = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(image) && AppInstall.Normalize(image).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        return process.Id;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }
        return 0;
    }

    static bool IsTrustedUiProcess(int processId, uint session, string executable)
    {
        if (processId <= 0 || session == 0xFFFFFFFF) return false;
        var expected = AppInstall.Normalize(executable);
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || (uint)process.SessionId != session) return false;
            var image = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(image) && AppInstall.Normalize(image).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
    }

    static SecurityIdentifier? TokenUserSid(nint token)
    {
        if (token == 0) return null;
        _ = GetTokenInformation(token, 1, 0, 0, out var size);
        if (size <= 0) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 1, buffer, size, out _)) return null;
            var info = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            return info.Sid == 0 ? null : new SecurityIdentifier(info.Sid);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int cb;
        public string? reserved, desktop, title;
        public int x, y, width, height, charsX, charsY, fill, flags;
        public short showWindow, reserved2;
        public nint reserved3, stdIn, stdOut, stdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessHandles { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformation(nint handle, int index, nint info, int length, out int needed);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(nint existing, uint access, nint attributes, int level, int type, out nint primary);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint token, int infoClass, nint info, int length, out int returnLength);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessAsUser(nint token, string? application, string command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessHandles process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
