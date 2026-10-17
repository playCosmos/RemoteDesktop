using System.IO;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
    Thread? desktopThread;
    TcpListener? listener;

    public void Start()
    {
        SecureDesktop.OwnsDesktop = true;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        SecureDesktop.WritePort(((IPEndPoint)listener.LocalEndpoint).Port);
        _ = AcceptInputs(cancel.Token);
        desktopThread = new Thread(DesktopLoop) { IsBackground = true, Name = "Secure desktop" };
        desktopThread.SetApartmentState(ApartmentState.STA);
        desktopThread.Start();
        LaunchUi();
    }

    public void Stop()
    {
        cancel.Cancel();
        try { listener?.Stop(); } catch (SocketException) { }
        desktopThread?.Join(2000);
        SecureDesktop.ClearFrame();
        SecureDesktop.ClearPort();
    }

    public void SessionChanged(int reason)
    {
        if (reason is 5 or 8) LaunchUi();
    }

    async Task AcceptInputs(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener != null)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = ReadInputs(client, token);
        }
    }

    async Task ReadInputs(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var lengthBytes = new byte[4];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await stream.ReadExactlyAsync(lengthBytes, token);
                    var length = BitConverter.ToInt32(lengthBytes);
                    if (length is < 2 or > 8192) return;
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, token);
                    var input = SecureDesktop.DecodeInput(body);
                    while (inputs.Count > 32 && inputs.TryDequeue(out _)) { }
                    inputs.Enqueue(input);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or OperationCanceledException or ArgumentException) { return; }
            }
        }
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
                else while (inputs.TryDequeue(out _)) { }
            }
            catch (Exception ex) when (ex is ExternalException or IOException or InvalidOperationException or Win32Exception or ArgumentException) { }
            Thread.Sleep(80);
        }
        if (onSecure) injector.ReleaseCore();
        SecureDesktop.ClearFrame();
    }

    static bool PublishIfSecure()
    {
        var desktop = OpenInputDesktop(0, false, DesktopRights);
        if (desktop == 0) { SecureDesktop.ClearFrame(); return false; }
        try
        {
            if (!SetThreadDesktop(desktop)) return false;
            var name = DesktopName(desktop);
            if (!SecureDesktop.IsSecureName(name)) { SecureDesktop.ClearFrame(); return false; }
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var width = Math.Clamp(GetSystemMetrics(78), 1, 7680);
            var height = Math.Clamp(GetSystemMetrics(79), 1, 4320);
            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            SecureDesktop.Publish(ScreenJpeg.Encode(bitmap, 55), width, height);
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

    static void LaunchUi()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return;
        if (!WTSQueryUserToken(session, out var user)) return;
        try
        {
            if (!DuplicateTokenEx(user, 0x10000000, 0, 2, 1, out var primary)) return;
            try
            {
                if (!CreateEnvironmentBlock(out var environment, primary, false)) environment = 0;
                try
                {
                    var executable = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(executable)) return;
                    var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = "winsta0" + (char)92 + "default", showWindow = 5, flags = 1 };
                    if (!CreateProcessAsUser(primary, null, AppInstall.Quoted(executable), 0, 0, false, 0x400, environment, Path.GetDirectoryName(executable), ref startup, out var process)) return;
                    CloseHandle(process.Thread);
                    CloseHandle(process.Process);
                }
                finally { if (environment != 0) DestroyEnvironmentBlock(environment); }
            }
            finally { CloseHandle(primary); }
        }
        finally { CloseHandle(user); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int cb;
        public string? reserved, desktop, title;
        public int x, y, width, height, charsX, charsY, fill, flags;
        public short showWindow, reserved2;
        public nint reserved3, stdIn, stdOut, stdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessHandles { public nint Process, Thread; }
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformation(nint handle, int index, nint info, int length, out int needed);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(nint existing, uint access, nint attributes, int level, int type, out nint primary);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessAsUser(nint token, string? application, string command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessHandles process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint handle);
}
