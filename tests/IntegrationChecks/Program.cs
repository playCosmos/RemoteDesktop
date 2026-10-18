using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Reflection;
using System.Text.Json;
using RemoteDesktop;

if (args.Length == 2 && args[0] == "--secure-pipe-client")
{
    using var pipeClient = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
    pipeClient.Connect(3000);
    _ = pipeClient.ReadByte();
    Environment.Exit(0);
}

System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

var loginThrottle = new LoginThrottle();
var throttleNow = DateTime.UtcNow;
Check(!loginThrottle.TryGetDelay("192.0.2.10", throttleNow, out _), "a new login source is not throttled");
loginThrottle.Failed("192.0.2.10", throttleNow);
Check(loginThrottle.TryGetDelay("192.0.2.10", throttleNow.AddMilliseconds(1500), out var loginRetry) && Math.Abs(loginRetry.TotalMilliseconds - 500) < 1, "failed login gets a two-second per-source backoff");
Check(!loginThrottle.TryGetDelay("192.0.2.11", throttleNow.AddMilliseconds(1500), out _), "one source cannot throttle another source");
loginThrottle.Failed("192.0.2.10", throttleNow.AddSeconds(1));
Check(loginThrottle.TryGetDelay("192.0.2.10", throttleNow.AddSeconds(2), out loginRetry) && Math.Abs(loginRetry.TotalSeconds - 3) < 0.001, "repeated failures increase source backoff");
loginThrottle.Succeeded("192.0.2.10");
Check(!loginThrottle.TryGetDelay("192.0.2.10", throttleNow.AddSeconds(2), out _), "a successful login clears its source backoff");
var parallelLoginThrottle = new LoginThrottle();
Check(parallelLoginThrottle.TryAcquire("192.0.2.10", throttleNow, out _) && parallelLoginThrottle.TryAcquire("192.0.2.11", throttleNow, out _), "different login sources can verify concurrently");
Check(!parallelLoginThrottle.TryAcquire("192.0.2.10", throttleNow, out _), "one source cannot occupy multiple login verification slots");
parallelLoginThrottle.Complete("192.0.2.10");
parallelLoginThrottle.Complete("192.0.2.11");
var ttlLoginThrottle = new LoginThrottle();
ttlLoginThrottle.Failed("192.0.2.12", throttleNow);
Check(ttlLoginThrottle.TrackedSources == 1 && !ttlLoginThrottle.TryGetDelay("192.0.2.12", throttleNow.AddMinutes(10).AddSeconds(1), out _) && ttlLoginThrottle.TrackedSources == 0, "login source failures expire after ten minutes");
var opaqueThrottle = new SourceIntervalThrottle();
Check(opaqueThrottle.TryAcquire("192.0.2.10", throttleNow, TimeSpan.FromSeconds(2), out _), "OPAQUE source can begin an exchange");
Check(!opaqueThrottle.TryAcquire("192.0.2.10", throttleNow.AddSeconds(1), TimeSpan.FromSeconds(2), out var opaqueRetry) && Math.Abs(opaqueRetry.TotalSeconds - 1) < 0.001, "OPAQUE start interval is limited per source");
Check(opaqueThrottle.TryAcquire("192.0.2.11", throttleNow.AddSeconds(1), TimeSpan.FromSeconds(2), out _), "one OPAQUE source cannot throttle another source");

var responseBytes = await RemoteResponseBounds.ReadBoundedAsync(new MemoryStream([1, 2, 3]), 3, CancellationToken.None);
Check(responseBytes.SequenceEqual(new byte[] { 1, 2, 3 }), "bounded response accepts bytes within its cap");
try { await RemoteResponseBounds.ReadBoundedAsync(new MemoryStream([1, 2, 3, 4, 5]), 4, CancellationToken.None); Check(false, "remote response stops at its streaming cap"); }
catch (IOException) { Check(true, "remote response stops at its streaming cap"); }
Check(FrameLimits.IsSupported(7680, 4320) && !FrameLimits.IsSupported(8192, 8192), "frame pixel bounds support 8K displays and reject excessive decoded area");
Check(H264SpsLimitsCheck.Run() == 3, "H.264 SPS bounds are checked before decoder input");
using var currentIdentity = WindowsIdentity.GetCurrent();
var securityViewerSid = currentIdentity.User!;
var processSddl = SecureDesktop.BuildUiProcessSddl(securityViewerSid);
var threadSddl = SecureDesktop.BuildUiThreadSddl(securityViewerSid);
Check(processSddl.StartsWith("D:P", StringComparison.Ordinal) && processSddl.Contains(";;;OW)", StringComparison.Ordinal) && processSddl.Contains("0x00101001;;;" + securityViewerSid.Value, StringComparison.Ordinal) && !processSddl.Contains("0x001FFFFF;;;" + securityViewerSid.Value, StringComparison.Ordinal) && !processSddl.Contains(";;;BA)", StringComparison.Ordinal), "trusted UI process DACL blocks same-user handle duplication");
Check(threadSddl.StartsWith("D:P", StringComparison.Ordinal) && threadSddl.Contains(";;;OW)", StringComparison.Ordinal) && threadSddl.Contains("0x00100801;;;" + securityViewerSid.Value, StringComparison.Ordinal) && !threadSddl.Contains(";;;BA)", StringComparison.Ordinal), "trusted UI thread DACL limits same-user thread access");

Check(WindowsFileClipboard.DecodeText("한글", unicode: true) == "한글", "clipboard keeps a text string");
Check(WindowsFileClipboard.DecodeText(new MemoryStream(System.Text.Encoding.Unicode.GetBytes("가").Concat(new byte[] { 0, 0 }).ToArray()), unicode: true) == "가", "clipboard decodes a Unicode text stream");
Check(WindowsFileClipboard.DecodeText(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("ok").Concat(new byte[] { 0 }).ToArray()), unicode: false) == "ok", "clipboard decodes an ANSI text stream");
Check(WindowsFileClipboard.DecodeText(null, unicode: true) == "", "clipboard ignores an empty text value");
Check(EndpointText.TrySplit("192.168.56.1:45831", out var pastedHost, out var pastedPort) && pastedHost == "192.168.56.1" && pastedPort == 45831, "pasted address keeps its port");
Check(EndpointText.TrySplit("192.168.56.1:443", out pastedHost, out pastedPort) && pastedPort == 443, "explicit https port still splits out of the address");
Check(!EndpointText.TrySplit("192.168.56.1", out _, out _), "address without a port stays in the address field");
Check(!EndpointText.TrySplit("192.168.56.1:", out _, out _), "incomplete pasted port is left alone");
var shareable = HostAddresses.Endpoints(45831, [IPAddress.Parse("169.254.1.2"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("192.168.56.1"), IPAddress.Parse("10.0.0.2"), IPAddress.Parse("172.16.0.1"), IPAddress.Loopback, IPAddress.Parse("192.168.56.1")]);
Check(shareable.SequenceEqual(["10.0.0.2:45831", "172.16.0.1:45831", "192.168.56.1:45831", "8.8.8.8:45831", "169.254.1.2:45831"]), "host addresses prefer LAN, drop duplicates and loopback");
Check(HostAddresses.Endpoints(45831, [IPAddress.Loopback]).SequenceEqual(["127.0.0.1:45831"]), "loopback remains when it is the only address");
Check(HostAddresses.Same(["10.0.0.2:45831", "192.168.0.8:45831"], ["10.0.0.2:45831", "192.168.0.8:45831"]) && !HostAddresses.Same(["10.0.0.2:45831"], ["192.168.0.8:45831"]) && !HostAddresses.Same(["10.0.0.2:45831"], ["10.0.0.2:45831", "192.168.0.8:45831"]), "an unchanged address list is recognized");
Check(UiText.AddressesChanged() == "네트워크 주소가 바뀌었습니다. 새 주소를 다시 복사하세요.", "a changed network tells the host to copy the new address");
Check(UiText.ClipboardFileProgress("클립보드 전송", 0, 1, "메모.txt", 50, 200) == "클립보드 전송 · 메모.txt · 25%" && UiText.ClipboardFileProgress("클립보드 수신", 1, 3, "사진.png", 100, 100) == "클립보드 수신 · 2/3 · 사진.png · 100%" && UiText.ClipboardFileProgress("클립보드 확인", 0, 1, "빈.bin", 0, 0) == "클립보드 확인 · 빈.bin", "clipboard progress names the file and percent");
Check(!UiText.ShouldReportProgress(0, 4) && UiText.ShouldReportProgress(-1, 0) && UiText.ShouldReportProgress(0, 5) && UiText.ShouldReportProgress(96, 100) && !UiText.ShouldReportProgress(100, 100), "clipboard progress updates on the first byte, each five percent, and completion");
var perUserInstall = AppInstall.PerUserDirectory(@"C:\Users\me\AppData\Local");
var machineInstall = AppInstall.MachineDirectory(@"C:\Program Files");
Check(AppInstall.ScopeOf(Path.Combine(perUserInstall, "RemoteDesktop.exe"), perUserInstall, machineInstall) == InstallScope.PerUser, "an exe in the user programs folder is a per-user install");
Check(AppInstall.ScopeOf(Path.Combine(machineInstall, "RemoteDesktop.exe"), perUserInstall, machineInstall) == InstallScope.Machine, "an exe in Program Files is a machine install");
Check(AppInstall.ScopeOf(@"D:\Tools\RemoteDesktop.exe", perUserInstall, machineInstall) == InstallScope.Portable, "an exe outside the install folders stays portable");
Check(!AppInstall.ShowUninstall(InstallScope.Portable) && AppInstall.ShowUserInstall(InstallScope.Portable) && AppInstall.ShowMachineInstall(InstallScope.Portable) && AppInstall.ShowAutostart(InstallScope.Portable), "a portable run offers both installs");
Check(!AppInstall.ShowUserInstall(InstallScope.PerUser) && AppInstall.ShowMachineInstall(InstallScope.PerUser) && AppInstall.ShowUninstall(InstallScope.PerUser) && AppInstall.ShowAutostart(InstallScope.PerUser), "a per-user install can move to everyone, change login start, or be removed");
Check(AppInstall.AutostartMatches("\"C:\\Users\\me\\AppData\\Local\\Programs\\Remote Desktop\\RemoteDesktop.exe\"", @"C:\Users\me\AppData\Local\Programs\Remote Desktop\RemoteDesktop.exe") && !AppInstall.AutostartMatches("other.exe", @"C:\Users\me\AppData\Local\Programs\Remote Desktop\RemoteDesktop.exe") && !AppInstall.AutostartMatches(null, "RemoteDesktop.exe"), "login autostart matches only this installed program");
Check(UiText.AutostartEnabled() == "로그인하면 접속 대기를 시작합니다." && UiText.AutostartDisabled() == "로그인 후 자동 실행을 껐습니다." && !UiText.IsProblem(UiText.AutostartEnabled()) && UiText.IsProblem(UiText.AutostartFailed(new InvalidOperationException("denied"))), "turning login start on or off stays quiet unless it fails");
Check(!AppInstall.ShowMachineInstall(InstallScope.Machine) && !AppInstall.ShowUserInstall(InstallScope.Machine) && AppInstall.ShowUninstall(InstallScope.Machine), "a machine install offers removal");
var serviceCommand = (char)34 + Path.Combine(machineInstall, "RemoteDesktop.exe") + (char)34 + " --service";
Check(AppInstall.ServiceCommand(Path.Combine(machineInstall, "RemoteDesktop.exe")) == serviceCommand, "the service starts the installed exe");
Check(ServiceRecoveryPolicy.ResetPeriodSeconds == 86400 && ServiceRecoveryPolicy.Actions.SequenceEqual([new ServiceFailureAction(ServiceFailureActionType.Restart, 5000), new ServiceFailureAction(ServiceFailureActionType.Restart, 15000), new ServiceFailureAction(ServiceFailureActionType.Restart, 60000)]), "an unexpected machine-service failure receives bounded automatic restarts");
var serviceStopChecks = 0;
var serviceStopWaits = 0;
var serviceDeletes = 0;
Check(ServiceStopPolicy.StopAndDeleteWhenStopped(() => ++serviceStopChecks >= 3, () => serviceDeletes++, 41, () => serviceStopWaits++) && serviceStopChecks == 3 && serviceStopWaits == 2 && serviceDeletes == 1, "machine uninstall deletes its service only after it reaches the stopped state");
serviceStopChecks = serviceStopWaits = serviceDeletes = 0;
Check(!ServiceStopPolicy.StopAndDeleteWhenStopped(() => { serviceStopChecks++; return false; }, () => serviceDeletes++, 3, () => serviceStopWaits++) && serviceStopChecks == 3 && serviceStopWaits == 2 && serviceDeletes == 0, "a service that ignores stop is retained and uninstall can report a timeout");
Check(AppInstall.CanRemoveDirectory(perUserInstall, @"C:\Program Files", @"C:\Users\me\AppData\Local") && AppInstall.CanRemoveDirectory(machineInstall, @"C:\Program Files", @"C:\Users\me\AppData\Local") && !AppInstall.CanRemoveDirectory(@"C:\Windows", @"C:\Program Files", @"C:\Users\me\AppData\Local"), "uninstall can remove only an install folder");
var publishedDir = Path.Combine(Path.GetTempPath(), "prd-pub-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(publishedDir);
var publishedExe = Path.Combine(publishedDir, "RemoteDesktop.exe");
File.WriteAllText(publishedExe, "exe");
var devDir = Path.Combine(Path.GetTempPath(), "prd-dev-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(devDir);
File.WriteAllText(Path.Combine(devDir, "RemoteDesktop.exe"), "exe");
File.WriteAllText(Path.Combine(devDir, "RemoteDesktop.dll"), "dll");
var copiedDir = Path.Combine(AppContext.BaseDirectory, "prd-copy-" + Guid.NewGuid().ToString("N"));
var missingExe = Path.Combine(publishedDir, "missing", "RemoteDesktop.exe");
try
{
    Check(AppInstall.CanInstall(publishedExe) && !AppInstall.CanInstall(Path.Combine(devDir, "RemoteDesktop.exe")) && !AppInstall.CanInstall(Path.Combine(publishedDir, "Other.exe")), "only a published RemoteDesktop.exe can be installed");
    AppInstaller.CopyExecutable(publishedExe, copiedDir);
    Check(File.Exists(Path.Combine(copiedDir, "RemoteDesktop.exe")), "install copies the published exe into the chosen folder");
    File.WriteAllText(publishedExe, "updated executable");
    AppInstaller.CopyExecutable(publishedExe, copiedDir);
    Check(File.ReadAllText(Path.Combine(copiedDir, "RemoteDesktop.exe")) == "updated executable", "install atomically replaces an existing executable");
    var failedCopyPreservedInstall = false;
    try { AppInstaller.CopyExecutable(missingExe, copiedDir); }
    catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
    {
        failedCopyPreservedInstall = File.ReadAllText(Path.Combine(copiedDir, "RemoteDesktop.exe")) == "updated executable";
    }
    Check(failedCopyPreservedInstall && !Directory.EnumerateFiles(copiedDir, "*.new").Any(), "a failed staged install preserves the working executable and removes its temporary file");
}
finally
{
    if (Directory.Exists(publishedDir)) Directory.Delete(publishedDir, true);
    if (Directory.Exists(devDir)) Directory.Delete(devDir, true);
    if (Directory.Exists(copiedDir)) Directory.Delete(copiedDir, true);
}
Check(UiText.PlacementTitle(InstallScope.Portable) == "설치하지 않고 실행 중" && UiText.PlacementTitle(InstallScope.PerUser) == "이 사용자에게 설치됨" && UiText.PlacementTitle(InstallScope.Machine) == "이 PC에 설치됨", "the host names portable, per-user, and machine installs");
Check(UiText.PlacementBody(InstallScope.Portable, false).Contains("개발용", StringComparison.Ordinal) && UiText.PlacementBody(InstallScope.Portable, true).Contains("모든 사용자", StringComparison.Ordinal) && UiText.PlacementBody(InstallScope.Machine, true).Contains("보안 화면", StringComparison.Ordinal), "install copy says what portable, per-user, and machine installs do");
Check(UiText.InstallCancelled() == "설치를 취소했습니다." && !UiText.IsProblem(UiText.InstallCancelled()) && !UiText.IsProblem(UiText.RemoveCancelled()) && UiText.IsProblem(UiText.InstallFailed(new InvalidOperationException("x"))) && UiText.IsProblem(UiText.RemoveFailed(new InvalidOperationException("x"))), "canceling install is quiet and a failed install is red");
Check(UiText.SecureDesktopNotice("live") == "보안 화면 · 클릭과 키는 이 화면으로 갑니다." && UiText.SecureDesktopNotice("hidden")!.Contains("모든 사용자", StringComparison.Ordinal) && UiText.SecureDesktopNotice("off") == null && !UiText.IsProblem(UiText.SecureDesktopNotice("hidden")), "a secure desktop is named without looking like a failure");
Check(UiText.ShowSecureDesktop(false, UiText.SecureDesktopNotice("live")) && UiText.ShowSecureDesktop(false, UiText.SecureDesktopNotice("hidden")) && !UiText.ShowSecureDesktop(true, UiText.SecureDesktopNotice("live")) && !UiText.ShowSecureDesktop(false, null) && !UiText.ShowSecureDesktop(false, ""), "the secure desktop notice stays on the picture until the controls open");
Check(SecureDesktop.Decide(false, true) == SecureView.Off && SecureDesktop.Decide(true, false) == SecureView.Hidden && SecureDesktop.Decide(true, true) == SecureView.Live, "the secure desktop is shown only when a fresh frame exists");
Check(SecureDesktop.ShouldEnable(true, true, true) && !SecureDesktop.ShouldEnable(false, true, true) && !SecureDesktop.ShouldEnable(true, false, true) && !SecureDesktop.ShouldEnable(true, true, false), "secure desktop gating requires the setting, a running service, and its endpoint");
var unicodeInputType = typeof(Desktop).Assembly.GetType("RemoteDesktop.UnicodeInputText", throwOnError: true)!;
var unicodeCodeUnitsMethod = unicodeInputType.GetMethod("TryGetCodeUnits", BindingFlags.Static | BindingFlags.NonPublic)!;
object?[] validUnicodeArgs = ["A😀Z", null];
var validUnicodeText = (bool)unicodeCodeUnitsMethod.Invoke(null, validUnicodeArgs)!;
var unicodeCodeUnits = (char[])validUnicodeArgs[1]!;
object?[] unmatchedHighArgs = ["x\uD800", null];
object?[] unmatchedLowArgs = ["\uDC00", null];
object?[] maxUnicodeArgs = [new string('x', 4096), null];
object?[] excessiveUnicodeArgs = [new string('x', 4097), null];
var maxUnicodeAccepted = (bool)unicodeCodeUnitsMethod.Invoke(null, maxUnicodeArgs)!;
Check(validUnicodeText && unicodeCodeUnits.SequenceEqual(new[] { 'A', '\uD83D', '\uDE00', 'Z' })
    && !(bool)unicodeCodeUnitsMethod.Invoke(null, unmatchedHighArgs)! && !(bool)unicodeCodeUnitsMethod.Invoke(null, unmatchedLowArgs)!
    && maxUnicodeAccepted && ((char[])maxUnicodeArgs[1]!).Length == 4096 && !(bool)unicodeCodeUnitsMethod.Invoke(null, excessiveUnicodeArgs)!, "text input preserves supplementary surrogate order, rejects unmatched surrogates, and keeps the 4096-code-unit limit");
SecureDesktop.Configure(false);
var injectorType = typeof(Desktop).Assembly.GetType("RemoteDesktop.InputInjector", throwOnError: true)!;
var applyInput = injectorType.GetMethod("ApplyCore", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
var inputInjector = Activator.CreateInstance(injectorType, nonPublic: true)!;
var observationsBeforeDisabledInput = SecureDesktop.ObservationCount;
var invalidInputRejected = false;
try { _ = applyInput.Invoke(inputInjector, [new InputEvent("unsupported")]); }
catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { invalidInputRejected = true; }
Check(invalidInputRejected && SecureDesktop.ObservationCount == observationsBeforeDisabledInput, "a disabled secure desktop skips observation on the input path");
var secureDropsBefore = SecureDesktop.InputDroppedBySecureDesktop;
SecureDesktop.RecordSecureInputDrop(SecureView.Hidden);
Check(SecureDesktop.InputDroppedBySecureDesktop == secureDropsBefore + 1, "secure desktop input withholding increments its diagnostic counter");
Check(SecureDesktop.Header(SecureView.Live) == "live" && SecureDesktop.Header(SecureView.Hidden) == "hidden" && SecureDesktop.Header(SecureView.Off) == "off", "the secure desktop header stays small");
Check(SecureDesktop.IsSecureName("Winlogon") && SecureDesktop.IsSecureName("Disconnect") && !SecureDesktop.IsSecureName("Default") && !SecureDesktop.IsSecureName(null), "the sign-in and UAC desktops are the secure ones");
var secureNow = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
var securePacked = SecureDesktop.Pack(secureNow, 8, 4, new byte[] { 1, 2, 3, 4 });
Check(SecureDesktop.TryUnpack(securePacked, secureNow.AddSeconds(1), TimeSpan.FromSeconds(2), out var secureJpeg) && secureJpeg.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "a fresh secure frame unpacks");
Check(!SecureDesktop.TryUnpack(securePacked, secureNow.AddSeconds(5), TimeSpan.FromSeconds(2), out _), "a stale secure frame is ignored");
Check(!SecureDesktop.TryUnpack(securePacked.AsSpan(0, 8).ToArray(), secureNow, TimeSpan.FromSeconds(2), out _), "a short secure frame is ignored");
var secureInputKey = Enumerable.Range(0, SecureDesktop.InputKeyLength).Select(i => (byte)i).ToArray();
var secureChallenge = Enumerable.Range(0, SecureDesktop.InputKeyLength).Select(i => (byte)(255 - i)).ToArray();
var viewerSid = WindowsIdentity.GetCurrent().User!;
var securePipeName = SecureDesktop.NewPipeName();
var secureEndpointText = securePipeName + "\n" + Convert.ToBase64String(secureInputKey);
Check(SecureDesktop.TryParseEndpoint(secureEndpointText, out var parsedPipeName, out var parsedSecureKey) && parsedPipeName == securePipeName && parsedSecureKey.SequenceEqual(secureInputKey) && !SecureDesktop.TryParseEndpoint("bad-pipe\n" + Convert.ToBase64String(secureInputKey), out _, out _) && !SecureDesktop.TryParseEndpoint(securePipeName + "\nnot-a-key", out _, out _), "the secure desktop channel accepts only a valid pipe name and private key");
var secureProof = SecureDesktop.InputProof(secureInputKey, secureChallenge);
var otherSecureChallenge = secureChallenge.Select(value => (byte)(value ^ 0xA5)).ToArray();
Check(SecureDesktop.VerifyInputProof(secureInputKey, secureChallenge, secureProof) && !SecureDesktop.VerifyInputProof(secureInputKey, otherSecureChallenge, secureProof) && !SecureDesktop.VerifyInputProof(secureInputKey, secureChallenge, new byte[SecureDesktop.InputKeyLength]) && !SecureDesktop.VerifyInputProof(secureInputKey.AsSpan(1), secureChallenge, secureProof), "the secure desktop channel verifies a fresh keyed challenge");
async Task<(bool Authenticated, int ProcessId)> AuthenticateSecureLoopback(byte[] offeredKey)
{
    var pipeName = SecureDesktop.NewPipeName();
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var serverCheck = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync();
        var processId = SecureDesktop.GetPipeClientProcessId(server);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var authenticated = await SecureDesktop.AuthenticateInputServerAsync(server, secureInputKey, timeout.Token);
        return (authenticated, processId);
    });
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    client.Connect(2000);
    SecureDesktop.AuthenticateInputClient(client, offeredKey);
    return await serverCheck;
}
var validSecurePeer = await AuthenticateSecureLoopback(secureInputKey);
var invalidSecurePeer = await AuthenticateSecureLoopback(new byte[SecureDesktop.InputKeyLength]);
Check(validSecurePeer.Authenticated && validSecurePeer.ProcessId == Environment.ProcessId && !invalidSecurePeer.Authenticated, $"the loopback secure input listener admits only the session process with its key (auth={validSecurePeer.Authenticated}, pid={validSecurePeer.ProcessId}, expected={Environment.ProcessId}, invalid={invalidSecurePeer.Authenticated})");
Check(SecureDesktop.AuthorizeInputClient(50, 50, 3, 3) && !SecureDesktop.AuthorizeInputClient(51, 50, 3, 3) && !SecureDesktop.AuthorizeInputClient(50, 50, 4, 3) && !SecureDesktop.AuthorizeInputClient(50, 50, 0xFFFFFFFF, 3) && !SecureDesktop.AuthorizeInputClient(0, 0, 3, 3), "secure input stops when the client PID or active Windows session changes");
async Task<byte[]?> SecureFramePipeRoundTrip(byte[] key, byte[] packed, DateTime now)
{
    var pipeName = SecureDesktop.NewPipeName();
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (!await SecureDesktop.AuthenticateInputServerAsync(server, key, timeout.Token)) return false;
        var header = new byte[4];
        await server.ReadExactlyAsync(header, timeout.Token);
        var length = BitConverter.ToInt32(header);
        if (length is < 2 or > 16) return false;
        var request = new byte[length];
        await server.ReadExactlyAsync(request, timeout.Token);
        if (!SecureDesktop.IsFrameRequest(request)) return false;
        await SecureDesktop.WriteFrameResponseAsync(server, packed, timeout.Token);
        return true;
    });
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    client.Connect(2000);
    SecureDesktop.AuthenticateInputClient(client, key);
    SecureDesktop.WriteFrameRequest(client);
    var hasFrame = SecureDesktop.ReadFrameResponse(client, now, out var frame);
    return await serverTask && hasFrame ? frame : null;
}
var pipeFrameJpeg = new byte[] { 0xFF, 0xD8, 4, 5, 6, 0xFF, 0xD9 };
var pipeFramePacked = SecureDesktop.Pack(secureNow, 8, 4, pipeFrameJpeg);
Check((await SecureFramePipeRoundTrip(secureInputKey, pipeFramePacked, secureNow.AddMilliseconds(10)))?.SequenceEqual(pipeFrameJpeg) == true, "the authenticated UAC pipe returns fresh screen frames to the trusted UI");
async Task<int> GetOtherPipeClientPid()
{
    var name = SecureDesktop.NewPipeName();
    using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var wait = server.WaitForConnectionAsync();
    var clientProcess = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is missing");
    var start = new ProcessStartInfo(clientProcess) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(clientProcess).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Test assembly path is missing"));
    start.ArgumentList.Add("--secure-pipe-client");
    start.ArgumentList.Add(name);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start pipe client check");
    await wait.WaitAsync(TimeSpan.FromSeconds(5));
    var processId = SecureDesktop.GetPipeClientProcessId(server);
    server.Dispose();
    if (!child.WaitForExit(5000)) { child.Kill(true); throw new TimeoutException("The test pipe client did not exit"); }
    return processId;
}
var otherSecurePeerPid = await GetOtherPipeClientPid();
Check(otherSecurePeerPid > 0 && otherSecurePeerPid != Environment.ProcessId, "Windows identifies a different same-user named-pipe client process");
var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
var privateSids = new HashSet<string>(StringComparer.Ordinal) { new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value };
var secureDirectoryAcl = SecureDesktop.BuildDirectorySecurity(viewerSid);
var secureFileAcl = SecureDesktop.BuildFileSecurity(viewerSid);
var secureDirectoryRules = secureDirectoryAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
var secureFileRules = secureFileAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
var secureTrustedSids = new[] { systemSid, adminsSid };
bool HasFullControl(FileSystemAccessRule[] rules, SecurityIdentifier sid) => rules.Any(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
Check(secureDirectoryAcl.AreAccessRulesProtected && secureFileAcl.AreAccessRulesProtected && secureTrustedSids.All(sid => HasFullControl(secureDirectoryRules, sid) && HasFullControl(secureFileRules, sid)) && secureDirectoryRules.Any(rule => rule.IdentityReference.Equals(viewerSid) && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute && (rule.FileSystemRights & FileSystemRights.Write) == 0) && secureFileRules.Any(rule => rule.IdentityReference.Equals(viewerSid) && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute && (rule.FileSystemRights & FileSystemRights.Write) == 0) && secureDirectoryRules.All(rule => !privateSids.Contains(((SecurityIdentifier)rule.IdentityReference).Value)) && secureFileRules.All(rule => !privateSids.Contains(((SecurityIdentifier)rule.IdentityReference).Value)), "secure desktop files are readable only by SYSTEM, administrators, and the active user");
var securePipeSecurity = SecureDesktop.BuildInputPipeSecurity(viewerSid);
var securePipeRules = securePipeSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
var securePipeSddl = SecureDesktop.BuildInputPipeSddl(viewerSid);
var securePipeDescriptor = new RawSecurityDescriptor(securePipeSddl);
var viewerPipeRule = securePipeRules.Any(rule => rule.IdentityReference.Equals(viewerSid) && (rule.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite && (rule.PipeAccessRights & PipeAccessRights.CreateNewInstance) == 0);
var serverPipeRule = securePipeRules.Any(rule => rule.IdentityReference.Equals(systemSid) && (rule.PipeAccessRights & PipeAccessRights.CreateNewInstance) != 0);
var noBroadPipeRules = securePipeRules.All(rule => !privateSids.Contains(((SecurityIdentifier)rule.IdentityReference).Value));
var mediumPipeLabel = securePipeSddl.Contains("ML;;NW;;;ME", StringComparison.Ordinal) && securePipeDescriptor.SystemAcl?.Count == 1;
Check(viewerPipeRule && serverPipeRule && noBroadPipeRules && mediumPipeLabel, "the UAC pipe admits the active user at medium integrity and reserves instance creation to SYSTEM and administrators");
var secureMove = SecureDesktop.DecodeInput(SecureDesktop.EncodeInput(new InputEvent("move", 0.25, 0.75)));
var secureKey = SecureDesktop.DecodeInput(SecureDesktop.EncodeInput(new InputEvent("key", Code: 13, Down: true)));
Check(secureMove.Kind == "move" && secureMove.X == 0.25 && secureMove.Y == 0.75 && secureKey.Kind == "key" && secureKey.Code == 13 && secureKey.Down, "secure input keeps the pointer and the key");
var attentionAt = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
var attentionPacked = SecureDesktop.EncodeInput(new InputEvent(SecureAttention.Kind));
Check(SecureAttention.CanSend(InstallScope.Machine) && !SecureAttention.CanSend(InstallScope.Portable) && !SecureAttention.CanSend(InstallScope.PerUser), "only a machine install can send Ctrl+Alt+Del");
Check(SecureAttention.Decide(true, false) == SecureAttention.Delivery.Send && SecureAttention.Decide(true, true) == SecureAttention.Delivery.Send && SecureAttention.Decide(false, true) == SecureAttention.Delivery.Forward && SecureAttention.Decide(false, false) == SecureAttention.Delivery.Decline, "the machine service sends Ctrl+Alt+Del and other installs decline it");
Check(SecureAttention.Ready(DateTime.MinValue, attentionAt) && !SecureAttention.Ready(attentionAt, attentionAt) && !SecureAttention.Ready(attentionAt, attentionAt.AddMilliseconds(999)) && SecureAttention.Ready(attentionAt, attentionAt.AddSeconds(1)) && !SecureAttention.Ready(attentionAt, attentionAt.AddSeconds(-1)), "Ctrl+Alt+Del is not sent twice in the same second");
Check(SecureAttention.PolicyValue(null) == 1 && SecureAttention.PolicyValue(0) == 1 && SecureAttention.PolicyValue(2) == 3 && SecureAttention.PolicyValue(1) == null && SecureAttention.PolicyValue(3) == null && SecureAttention.PolicyValue(4) == null, "the service policy is raised only when software secure attention is blocked");
Check(SecureAttention.RestorePolicyValue(1, 0) == 0 && SecureAttention.RestorePolicyValue(3, 2) == 2 && SecureAttention.RestorePolicyValue(1, null) == null, "machine uninstall restores the secure attention policy value it changed");
Check(SecureAttention.RestorePolicyValue(3, 0) == 3 && SecureAttention.RestorePolicyValue(null, 2) == null && SecureAttention.RestorePolicyValue(2, 2) == 2, "machine uninstall preserves policy changes made after installation");
Check(UiText.SecureAttentionSent() == "Ctrl+Alt+Del을 보냈습니다." && UiText.SecureAttentionUnavailable() == "Ctrl+Alt+Del은 모든 사용자 설치에서만 보낼 수 있습니다." && !UiText.IsProblem(UiText.SecureAttentionSent()) && !UiText.IsProblem(UiText.SecureAttentionUnavailable()) && UiText.SessionOnPicture(UiText.SecureAttentionSent()) && UiText.SessionOnPicture(UiText.SecureAttentionUnavailable()), "a declined Ctrl+Alt+Del stays quiet on the picture");
Check(SecureDesktop.DecodeInput(attentionPacked).Kind == SecureAttention.Kind && SecureAttention.ReadNotice("{\"notice\":\"" + UiText.SecureAttentionUnavailable() + "\"}") == UiText.SecureAttentionUnavailable() && SecureAttention.ReadNotice("") == null && SecureAttention.ReadNotice("ok") == null && SecureAttention.ReadNotice("{\"notice\":\"\"}") == null, "secure attention survives the service channel and an empty reply stays quiet");
Check(AppCommand.Has(["--wait-ui"], AppCommand.WaitUi) && AppCommand.Has(["RemoteDesktop.exe", "--service"], AppCommand.Service) && AppCommand.Has(["--remove-firewall"], AppCommand.RemoveFirewall) && !AppCommand.Has(["--install"], AppCommand.InstallMachine), "service and install arguments are recognized");
Check(HostFirewall.TryCommand(["--firewall", "45831"], out var firewallPort) && firewallPort == 45831 && HostFirewall.Command(45831) == "--firewall 45831" && !HostFirewall.TryCommand(["--firewall", "80"], out _) && !HostFirewall.TryCommand(["--firewall"], out _) && AppCommand.Has(["--FIREWALL", "45831"], AppCommand.Firewall), "the firewall command accepts only a listening port");
Check(HostFirewall.TryPlan(45831, @"C:\Program Files\Remote Desktop\RemoteDesktop.exe", out var firewallPlan) && firewallPlan.Port == 45831 && HostFirewall.TryPlan(1024, firewallPlan.Program, out _) && HostFirewall.TryPlan(65535, firewallPlan.Program, out _) && !HostFirewall.TryPlan(1023, firewallPlan.Program, out _) && !HostFirewall.TryPlan(65536, firewallPlan.Program, out _) && !HostFirewall.TryPlan(45831, "RemoteDesktop.exe", out _) && !HostFirewall.TryPlan(45831, @"C:\Program Files\Remote Desktop\RemoteDesktop.dll", out _), "a firewall plan needs the listening port and this program");
var quote = ((char)34).ToString();
Check(HostFirewall.RuleName == "Personal Remote Desktop" && HostFirewall.DeleteArguments() == "advfirewall firewall delete rule name=" + quote + HostFirewall.RuleName + quote && !HostFirewall.DeleteArguments().Contains("program=", StringComparison.Ordinal) && !HostFirewall.DeleteArguments().Contains("localport", StringComparison.Ordinal), "firewall delete names only this rule");
Check(HostFirewall.AddArguments(firewallPlan) == "advfirewall firewall add rule name=" + quote + HostFirewall.RuleName + quote + " dir=in action=allow protocol=TCP localport=" + firewallPlan.Port + " program=" + quote + firewallPlan.Program + quote + " profile=domain,private enable=yes" && !HostFirewall.AddArguments(firewallPlan).Contains("public", StringComparison.Ordinal), "firewall add names this program, the port, and private or domain networks");
var firewallNow = new HostFirewall.Observed(true, firewallPlan.Port, firewallPlan.Program, true, HostFirewall.DomainAndPrivate, HostFirewall.Tcp, HostFirewall.Inbound, HostFirewall.Allow);
Check(HostFirewall.Decide(firewallPlan, firewallNow) == HostFirewall.Choice.Keep && HostFirewall.Decide(firewallPlan, firewallNow with { Program = firewallPlan.Program.ToLowerInvariant() }) == HostFirewall.Choice.Keep, "an existing private and domain rule for this program is kept");
Check(HostFirewall.ShouldRemove(firewallNow, firewallPlan.Program) && !HostFirewall.ShouldRemove(firewallNow with { Program = @"D:\Other\RemoteDesktop.exe" }, firewallPlan.Program) && !HostFirewall.ShouldRemove(HostFirewall.Absent, firewallPlan.Program) && !HostFirewall.ShouldRemove(null, firewallPlan.Program), "uninstall removes only this install's firewall rule");
Check(HostFirewall.Decide(firewallPlan, null) == HostFirewall.Choice.Replace && HostFirewall.Decide(firewallPlan, HostFirewall.Absent) == HostFirewall.Choice.Replace && HostFirewall.Decide(firewallPlan, firewallNow with { Port = 47000 }) == HostFirewall.Choice.Replace && HostFirewall.Decide(firewallPlan, firewallNow with { Program = @"D:\Other\RemoteDesktop.exe" }) == HostFirewall.Choice.Replace && HostFirewall.Decide(firewallPlan, firewallNow with { Profiles = 4 }) == HostFirewall.Choice.Replace && HostFirewall.Decide(firewallPlan, firewallNow with { Enabled = false }) == HostFirewall.Choice.Replace && HostFirewall.Decide(firewallPlan, firewallNow with { Protocol = 17 }) == HostFirewall.Choice.Replace, "the firewall rule is replaced when it is missing or no longer matches");
var firewallNl = ((char)10).ToString();
var firewallShown = "Rule Name:                            Personal Remote Desktop" + firewallNl + "Enabled:                              Yes" + firewallNl + "Direction:                            In" + firewallNl + "Profiles:                             Domain,Private" + firewallNl + "Protocol:                             TCP" + firewallNl + "LocalPort:                            " + firewallPlan.Port + firewallNl + "Program:                              " + firewallPlan.Program + firewallNl + "Action:                               Allow" + firewallNl;
Check(HostFirewall.Decide(firewallPlan, HostFirewall.ParseShow(firewallShown)) == HostFirewall.Choice.Keep && HostFirewall.Decide(firewallPlan, HostFirewall.ParseShow(firewallShown.Replace("Domain,Private", "Public"))) == HostFirewall.Choice.Replace && HostFirewall.ParseShow("No rules match the specified criteria.") == HostFirewall.Absent && HostFirewall.ParseShow("규칙") == null, "a firewall listing is kept only when the shown rule already matches");
Check(UiText.FirewallOpened(45831) == "방화벽에서 포트 45831 수신을 허용했습니다." && UiText.FirewallDeclined() == "방화벽 허용을 취소했습니다. 접속 대기는 계속됩니다." && UiText.FirewallLeft() == "방화벽은 그대로입니다. 같은 네트워크에서 접속이 안 되면 이 포트를 허용해 주세요." && UiText.FirewallRemoveFailed() == "앱의 방화벽 규칙을 확인하거나 제거하지 못했습니다." && !UiText.IsProblem(UiText.FirewallOpened(45831)) && !UiText.IsProblem(UiText.FirewallDeclined()) && !UiText.IsProblem(UiText.FirewallLeft()) && UiText.IsProblem(UiText.FirewallRemoveFailed()), "firewall removal failures are explained as errors");
Check(VideoCodec.Negotiate("h264", true) == "h264" && VideoCodec.Negotiate("h264", false) == "jpeg" && VideoCodec.Negotiate(null, true) == "jpeg" && VideoCodec.Negotiate("jpeg", true) == "jpeg", "h264 is used only when the client asks and the encoder is ready");
Check(VideoCodec.Bitrate(55, 1920, 1080) == 4_000_000 && VideoCodec.Bitrate(30, 1920, 1080) == 1_500_000 && VideoCodec.Bitrate(80, 1920, 1080) == 8_000_000 && VideoCodec.Bitrate(55, 960, 540) == 1_000_000 && VideoCodec.Bitrate(90, 16, 16) == 400_000, "video bitrate follows the picture size and quality");
var held = VideoCodec.Pace(75, 75, 10, 15, 0, 0, false);
Check(held.Quality == 75 && held.Slow == 0 && !held.Changed, "an unchanged picture does not move the link pace");
var late = VideoCodec.Pace(75, 75, 200, 15, 2, 4, true);
Check(late.Changed && late.Quality == 55 && late.Slow == 0 && late.Fast == 0, "three late pictures step the requested quality down");
var waiting = VideoCodec.Pace(75, 75, 200, 15, 1, 0, true);
Check(!waiting.Changed && waiting.Slow == 2 && waiting.Quality == 75, "one late picture only counts toward the step");
var middle = VideoCodec.Pace(75, 75, 50, 15, 2, 0, true);
Check(!middle.Changed && middle.Slow == 0 && middle.Fast == 0, "a picture inside the frame interval clears the pace count");
var early = VideoCodec.Pace(75, 55, 10, 15, 0, 7, true);
Check(early.Changed && early.Quality == 75 && early.Fast == 0, "eight quick pictures restore the chosen quality");
var capped = VideoCodec.Pace(30, 25, 5, 5, 0, 7, true);
Check(capped.Quality == 30 && capped.Changed, "link pace never asks above the chosen quality");
var floor = VideoCodec.Pace(75, 25, 400, 15, 2, 0, true);
Check(!floor.Changed && floor.Quality == 25, "link pace stays at the lowest quality");
var missing = VideoCodec.Pace(75, 55, double.NaN, 15, 2, 3, true);
Check(!missing.Changed && missing.Quality == 55 && missing.Slow == 2 && missing.Fast == 3, "a missing transfer time leaves the pace alone");
Check(UiText.PaceShift(75, 75, 55) == "화면이 밀려 화질을 낮췄습니다. 고화질 대신 균형." && UiText.PaceShift(40, 40, 30) == "화면이 밀려 화질을 더 낮췄습니다." && UiText.PaceShift(75, 55, 75) == "화면에 여유가 생겨 화질을 고화질로 되돌렸습니다." && UiText.PaceShift(75, 40, 55) == "화면에 여유가 생겨 화질을 균형으로 올렸습니다." && UiText.PaceShift(75, 55, 55) == null && !UiText.IsProblem(UiText.PaceShift(75, 75, 55)), "a paced quality change is explained once");
Check(VideoCodec.NeedsKeyframe(0, 15, false) && VideoCodec.NeedsKeyframe(30, 15, false) && !VideoCodec.NeedsKeyframe(10, 15, false) && VideoCodec.NeedsKeyframe(1, 15, true), "a skipped or forced picture starts a new video keyframe");
Check(H264FallbackPolicy.NoteDecoderMiss(0) == 1 && H264FallbackPolicy.NoteDecoderMiss(7) == 8 && H264FallbackPolicy.DecoderShouldUseJpeg(8) && !H264FallbackPolicy.DecoderShouldUseJpeg(7), "a Windows decoder switches to JPEG after eight consecutive H.264 misses");
Check(H264FallbackPolicy.NoteServerJpegFrame(0) == 1 && H264FallbackPolicy.NoteServerJpegFrame(2) == 3 && !H264FallbackPolicy.ServerShouldUseJpeg(2) && H264FallbackPolicy.ServerShouldUseJpeg(3), "a Windows client retires H.264 after three consecutive server JPEG frames");
Check(UiText.VideoFallback() == "H.264를 사용할 수 없어 JPEG로 전환했습니다." && UiText.SessionOnPicture(UiText.VideoFallback()), "a codec fallback is reported outside the toolbar");
using (var failedVideo = new H264Session())
{
    var invalidJpeg = new byte[] { 0xFF, 0xD8, 0x00, 0x00 };
    Check(failedVideo.Encode(invalidJpeg, false, false, 15, 55).Length == 0 && !failedVideo.Retired, "one invalid frame does not retire the H.264 session");
    Check(failedVideo.Encode(invalidJpeg, false, false, 15, 55).Length == 0 && !failedVideo.Retired, "two invalid frames do not retire the H.264 session");
    Check(failedVideo.Encode(invalidJpeg, false, false, 15, 55).Length == 0 && failedVideo.Retired && failedVideo.Encode(invalidJpeg, false, false, 15, 55).Length == 0, "three invalid frames retire H.264 and leave the session on JPEG");
}
var avcc = new byte[] { 0, 0, 0, 2, 9, 16, 0, 0, 0, 1, 5 };
var annex = VideoCodec.ToAnnexB(avcc);
Check(!VideoCodec.IsAnnexB(avcc) && VideoCodec.IsAnnexB(annex) && annex.Length == 4 + 2 + 4 + 1 && annex[4] == 9 && annex[10] == 5, "length-prefixed video turns into annex-b");
Check(UiText.SessionPreset("10.0.0.8:45831", "균형", 15, null, "h264") == "10.0.0.8:45831 · 균형 · H.264" && UiText.SessionPreset("10.0.0.8:45831", "균형", 15, null, "jpeg") == "10.0.0.8:45831 · 균형 · JPEG" && UiText.SessionPreset(null, "균형", 15) == "균형", "the toolbar omits frame rates while retaining quality and codec");
using (var picture = new System.Drawing.Bitmap(64, 48, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
{
    using (var graphics = System.Drawing.Graphics.FromImage(picture))
    {
        graphics.Clear(System.Drawing.Color.FromArgb(255, 220, 20, 20));
        graphics.FillRectangle(System.Drawing.Brushes.Blue, 32, 0, 32, 48);
    }
    using var encoder = new H264Encoder();
    using var decoder = new H264Decoder();
    byte[]? seen = null;
    var seenWidth = 0;
    var seenHeight = 0;
    for (var frame = 0; frame < 4; frame++)
    {
        var encoded = encoder.Encode(picture, frame == 0, 15, 55);
        if (encoded.Length == 0) continue;
        if (!decoder.TryDecode(encoded, out var pixels, out var decodedWidth, out var decodedHeight)) continue;
        seen = pixels;
        seenWidth = decodedWidth;
        seenHeight = decodedHeight;
    }
    Check(seen != null && seenWidth == 64 && seenHeight == 48, "h264 roundtrip keeps the frame size");
    var left = (24 * seenWidth + 8) * 4;
    var right = (24 * seenWidth + 40) * 4;
    Check(seen![left + 2] > seen[left] + 20 && seen[right] > seen[right + 2] + 20, "h264 keeps a red side and a blue side");
}
var retiredRoot = Path.Combine(Path.GetTempPath(), "prd-cache-" + Guid.NewGuid().ToString("N"));
var retiredOutside = Path.Combine(Path.GetTempPath(), "prd-outside-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(retiredRoot);
Directory.CreateDirectory(Path.Combine(retiredRoot, "stale"));
File.WriteAllText(Path.Combine(retiredRoot, "stale", "old.txt"), "old");
var retiredCurrent = Path.Combine(retiredRoot, "current", "0", "now.txt");
Directory.CreateDirectory(Path.GetDirectoryName(retiredCurrent)!);
File.WriteAllText(retiredCurrent, "now");
Directory.CreateDirectory(Path.Combine(retiredRoot, "current-extra"));
File.WriteAllText(Path.Combine(retiredRoot, "current-extra", "x.txt"), "x");
Directory.CreateDirectory(retiredOutside);
File.WriteAllText(Path.Combine(retiredOutside, "keep.txt"), "keep");
try
{
    Check(ClipboardPaths.DeleteRetired(Path.Combine(retiredRoot, "missing"), []) == 0, "a missing clipboard cache has nothing to remove");
    Check(ClipboardPaths.DeleteRetired(retiredRoot, [retiredCurrent, Path.Combine(retiredOutside, "keep.txt")]) == 2 && !Directory.Exists(Path.Combine(retiredRoot, "stale")) && !Directory.Exists(Path.Combine(retiredRoot, "current-extra")) && File.Exists(retiredCurrent) && File.Exists(Path.Combine(retiredOutside, "keep.txt")), "retired clipboard batches are removed and the current paste stays");
    Check(ClipboardPaths.DeleteRetired(retiredRoot, [retiredCurrent]) == 0 && File.Exists(retiredCurrent), "the current clipboard batch is not removed twice");
    Check(ClipboardPaths.RetiredNames(["stale", "current", ""], ["current"]).SequenceEqual(["stale"]) && ClipboardPaths.RetiredNames(["a", "b"], ["A", "b"]).Count == 0 && ClipboardPaths.RetiredNames(["only"], []).SequenceEqual(["only"]) && ClipboardPaths.RetiredNames(null, ["current"]).Count == 0, "a clipboard cache drops batches the current paste does not use");
}
finally { Directory.Delete(retiredRoot, true); Directory.Delete(retiredOutside, true); }
var dropRoot = Path.Combine(Path.GetTempPath(), "prd-drop-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dropRoot);
var dropFile = Path.Combine(dropRoot, "note.txt");
File.WriteAllText(dropFile, "hello");
var dropSecond = Path.Combine(dropRoot, "two.txt");
File.WriteAllText(dropSecond, "x");
var dropDir = Path.Combine(dropRoot, "folder");
Directory.CreateDirectory(dropDir);
try
{
    var accepted = ClipboardPaths.Accept([dropFile, dropSecond]);
    Check(accepted.Length == 2 && accepted[0].Name == "note.txt" && accepted[0].Size == 5 && accepted[1].Name == "two.txt" && accepted[1].Size == 1, "dropped files are accepted in order");
    File.WriteAllText(Path.Combine(dropDir, "b.txt"), "bb");
    Directory.CreateDirectory(Path.Combine(dropDir, "nested"));
    File.WriteAllText(Path.Combine(dropDir, "nested", "a.txt"), "aaa");
    var folderFiles = ClipboardPaths.Accept([dropDir]);
    Check(folderFiles.Length == 2 && folderFiles[0].Name == "folder/b.txt" && folderFiles[0].Size == 2 && folderFiles[1].Name == "folder/nested/a.txt" && folderFiles[1].Size == 3, "a dropped folder keeps its files and subfolders");
    var stage = Path.Combine(dropRoot, "stage");
    Directory.CreateDirectory(stage);
    File.WriteAllText(ClipboardPaths.FileAt(stage, 0, folderFiles[0].Name), "bb");
    File.WriteAllText(ClipboardPaths.FileAt(stage, 1, folderFiles[1].Name), "aaa");
    var published = ClipboardPaths.Publish(stage, folderFiles);
    Check(published.Length == 1 && Directory.Exists(published[0]) && Path.GetFileName(published[0]) == "folder" && File.ReadAllText(Path.Combine(published[0], "nested", "a.txt")) == "aaa" && File.ReadAllText(Path.Combine(published[0], "b.txt")) == "bb", "a received folder is pasted as one folder");
    var again = ClipboardPaths.Accept(published);
    Check(again.Length == 2 && again[0].Name == "folder/b.txt" && again[1].Name == "folder/nested/a.txt", "a pasted folder copies again with the same names");
    var otherFolder = Path.Combine(dropRoot, "other", "folder");
    Directory.CreateDirectory(otherFolder);
    File.WriteAllText(Path.Combine(otherFolder, "c.txt"), "c");
    var both = ClipboardPaths.Accept([dropDir, otherFolder]);
    Check(both.Any(file => file.Name == "folder/b.txt") && both.Any(file => file.Name == "folder (2)/c.txt"), "two folders with the same name stay separate");
    var emptyDir = Path.Combine(dropRoot, "empty");
    Directory.CreateDirectory(emptyDir);
    var emptyFiles = ClipboardPaths.Accept([emptyDir]);
    Check(emptyFiles.Length == 1 && emptyFiles[0].Directory && emptyFiles[0].Name == "empty" && emptyFiles[0].Size == 0, "an empty folder is copied");
    var emptyPublished = ClipboardPaths.Publish(Path.Combine(dropRoot, "empty-stage"), emptyFiles);
    Check(emptyPublished.Length == 1 && Directory.Exists(emptyPublished[0]) && Path.GetFileName(emptyPublished[0]) == "empty" && !Directory.EnumerateFileSystemEntries(emptyPublished[0]).Any(), "an empty folder is pasted as an empty folder");
    var emptyAgain = ClipboardPaths.Accept(emptyPublished);
    Check(emptyAgain.Length == 1 && emptyAgain[0].Directory && emptyAgain[0].Name == "empty", "a pasted empty folder copies again");
    var withHole = Path.Combine(dropRoot, "with-hole");
    Directory.CreateDirectory(Path.Combine(withHole, "keep"));
    File.WriteAllText(Path.Combine(withHole, "keep", "a.txt"), "a");
    Directory.CreateDirectory(Path.Combine(withHole, "keep", "blank"));
    var hole = ClipboardPaths.Accept([withHole]);
    Check(hole.Any(file => file.Name == "with-hole/keep/a.txt" && !file.Directory) && hole.Any(file => file.Name == "with-hole/keep/blank" && file.Directory && file.Size == 0), "an empty folder inside a copied folder is kept");
    var holePublished = ClipboardPaths.Publish(Path.Combine(dropRoot, "hole-stage"), hole);
    Check(holePublished.Length == 1 && Directory.Exists(Path.Combine(holePublished[0], "keep", "blank")), "a received folder keeps an empty subfolder");
    try { ClipboardPaths.Validate([new ClipboardFile("empty", 1, true)]); Check(false, "a folder entry cannot carry bytes"); }
    catch (ArgumentException ex) { Check(ex.Message == "빈 폴더만 폴더로 복사할 수 있습니다.", "a folder entry cannot carry bytes"); }
    try { ClipboardPaths.Validate([new ClipboardFile("folder/../../x.txt", 1)]); Check(false, "a folder path cannot escape"); }
    catch (ArgumentException ex) { Check(ex.Message == "지원하지 않는 파일 이름입니다.", "a folder path cannot escape"); }
    try { ClipboardPaths.FileAt(stage, 0, "../outside.txt"); Check(false, "a stored folder path cannot escape"); }
    catch (ArgumentException) { Check(!File.Exists(Path.Combine(dropRoot, "outside.txt")), "a stored folder path cannot escape"); }
    try { ClipboardPaths.Accept([Path.Combine(dropRoot, "missing.txt")]); Check(false, "a missing drop is rejected"); }
    catch (ArgumentException ex) { Check(ex.Message == "파일이나 폴더만 복사할 수 있습니다. 링크는 지원하지 않습니다.", "a missing drop is rejected"); }
    try { ClipboardPaths.Accept([]); Check(false, "an empty drop is rejected"); }
    catch (ArgumentException ex) { Check(ex.Message == "한 번에 파일 1~100개를 복사할 수 있습니다.", "an empty drop is rejected"); }
    try { ClipboardPaths.Accept(Enumerable.Repeat(dropFile, ClipboardPaths.MaxFiles + 1).ToArray()); Check(false, "too many dropped files are rejected"); }
    catch (ArgumentException ex) { Check(ex.Message == "한 번에 파일 1~100개를 복사할 수 있습니다.", "too many dropped files are rejected"); }
    try { ClipboardPaths.Validate([new ClipboardFile("big.bin", RemoteHost.MaxFileBytes + 1)]); Check(false, "a file over 1GiB is rejected"); }
    catch (ArgumentException ex) { Check(ex.Message == "파일당 최대 1GiB입니다.", "a file over 1GiB is rejected"); }
    try { ClipboardPaths.Validate([new ClipboardFile("a.bin", RemoteHost.MaxFileBytes), new ClipboardFile("b.bin", RemoteHost.MaxFileBytes), new ClipboardFile("c.bin", RemoteHost.MaxFileBytes), new ClipboardFile("d.bin", RemoteHost.MaxFileBytes), new ClipboardFile("e.bin", 1)]); Check(false, "a batch over 4GiB is rejected"); }
    catch (ArgumentException ex) { Check(ex.Message == "한 번에 최대 4GiB까지 복사할 수 있습니다.", "a batch over 4GiB is rejected"); }
    string? link = null;
    try { link = Path.Combine(dropRoot, "link.txt"); File.CreateSymbolicLink(link, dropFile); }
    catch (Exception) { link = null; }
    if (link != null && File.Exists(link))
    {
        try { ClipboardPaths.Accept([link]); Check(false, "a dropped link is rejected"); }
        catch (ArgumentException ex) { Check(ex.Message == "파일이나 폴더만 복사할 수 있습니다. 링크는 지원하지 않습니다.", "a dropped link is rejected"); }
    }
}
finally { Directory.Delete(dropRoot, true); }
var placed = new ClientSettings { WindowLeft = -120, WindowTop = 40, WindowWidth = 1100, WindowHeight = 720, WindowMaximized = true };
Check(placed.TryGetWindowPlacement(-1920, 0, 3840, 1080, out var placement) && placement.Left == -120 && placement.Maximized, "window placement restores on a shifted desktop");
Check(new ClientSettings { WindowLeft = 5000, WindowTop = -2000, WindowWidth = 3000, WindowHeight = 1600 }.TryGetWindowPlacement(0, 0, 1920, 1080, out var movedWindow) && movedWindow.Left == 0 && movedWindow.Top == 0 && movedWindow.Width == 1920 && movedWindow.Height == 1080, "an off-screen saved window is resized and moved back onto a display");
var compactWindow = WindowLayout.FitToWorkArea(912, 512, 1180, 800, 920, 640);
Check(WindowLayout.StackHostPanels(1024) && !WindowLayout.StackHostPanels(1180) && compactWindow.Width == 880 && compactWindow.Height == 480 && compactWindow.MinWidth == 880 && compactWindow.MinHeight == 480, "window layout stacks host cards and fits the work area on high-DPI screens");
var recent = new ClientSettings();
recent.RememberRecent("192.168.0.2:45831", "AA", "all", 75, 30);
recent.RememberRecent("10.0.0.5:45831", "BB", "", 55, 15);
recent.RememberRecent("192.168.0.2:45831", "CC", "2", 30, 5);
Check(recent.Recent.Count == 2 && recent.Recent[0].Address == "192.168.0.2:45831" && recent.Recent[0].Fingerprint == "CC" && recent.Recent[1].Address == "10.0.0.5:45831", "recent hosts move the latest to the front");
for (var i = 0; i < 6; i++) recent.RememberRecent("10.1.0." + i + ":45831", "pin", "", 55, 15);
Check(recent.Recent.Count == 5 && recent.Recent[0].Address == "10.1.0.5:45831", "recent hosts keep five entries");
Check(recent.ForgetRecent("10.1.0.5:45831") && recent.Recent[0].Address == "10.1.0.4:45831", "a recent host can be removed");
Check(new ClientSettings().OpenHostTab == false && new ClientSettings { SelectedTab = "nope" }.OpenHostTab == false, "unknown tab preference stays on the remote page");
Check(UiText.Explain(new IOException("Failed to bind to address https://[::]:45831: address already in use."), "fallback") == "이 포트는 이미 사용 중입니다. 남아 있는 이 프로그램을 종료한 뒤 다시 시작하세요.", "a busy port is explained without the framework text");
Check(UiText.Explain(new SocketException((int)SocketError.AddressAlreadyInUse), "fallback").Contains("이미 사용 중"), "a socket bind failure is explained in Korean");
Check(UiText.Explain(new ArgumentException("포트 범위: 1024–65535"), "fallback") == "포트 범위: 1024–65535", "Korean validation text is kept");
Check(UiText.Explain(new InvalidOperationException("Windows password-key enrollment is required."), "fallback") == "보안 등록에 실패했습니다. 현재 비밀번호를 다시 저장하세요.", "secure enrollment failures stay in Korean");
Check(!UiText.Explain(new InvalidOperationException("Object reference not set to an instance of an object."), "연결을 다시 시도하세요.").Contains("Object reference"), "unknown English failures do not reach the UI");
Check(UiText.Explain(new ArgumentException("Invalid clipboard origin"), "fallback") == "클립보드 전송을 확인하지 못했습니다. 다시 복사하세요.", "clipboard origin failures stay in Korean");
Check(UiText.Explain(new ArgumentException("File hash mismatch"), "fallback") == "클립보드 파일이 맞지 않습니다. 다시 복사하세요.", "clipboard hash failures stay in Korean");
Check(UiText.RemoteFailure(null) == null && UiText.RemoteFailure("  ") == null && UiText.RemoteFailure("Response status code does not indicate success: 409") == null, "a framework failure does not become the clipboard reason");
Check(UiText.RemoteFailure("클립보드가 변경되었거나 전송이 만료되었습니다. 다시 복사하세요.") == "클립보드가 변경되었거나 전송이 만료되었습니다. 다시 복사하세요.", "a Korean clipboard conflict reaches the user");
Check(UiText.Explain(new HttpRequestException(UiText.RemoteFailure("File hash mismatch")!, null, HttpStatusCode.BadRequest), "fallback") == "클립보드 파일이 맞지 않습니다. 다시 복사하세요.", "a server hash mismatch stays in Korean");
Check(UiText.Explain(new InvalidOperationException("DXGI interface is unavailable."), "fallback") == "화면 캡처를 시작하지 못했습니다.", "capture startup failures stay in Korean");
Check(!UiText.OfferMonitorChoice(2) && UiText.OfferMonitorChoice(3), "one monitor does not add a display picker");
Check(UiText.SwitchingDisplay("모니터 2") == "모니터 2 화면으로 바꾸는 중…" && UiText.SwitchingDisplay("  ") == "다른 모니터로 바꾸는 중…" && UiText.SwitchingDisplay(null) == "다른 모니터로 바꾸는 중…", "switching monitors names the screen that is coming next");
var sideBySide = new UiText.MonitorSpot[] { new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080) };
var stacked = new UiText.MonitorSpot[] { new(0, 0, 1920, 1080), new(0, 1080, 1920, 1080) };
var across = new UiText.MonitorSpot[] { new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080), new(3840, 0, 1920, 1080) };
var corners = new UiText.MonitorSpot[] { new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080), new(0, 1080, 1920, 1080), new(1920, 1080, 1920, 1080) };
var shifted = new UiText.MonitorSpot[] { new(-1920, 0, 1920, 1080), new(0, 0, 1920, 1080) };
Check(UiText.DisplayPlace(false, 0, 0, 1920, 1080, sideBySide) == "왼쪽" && UiText.DisplayPlace(false, 1920, 0, 1920, 1080, sideBySide) == "오른쪽" && UiText.DisplayPlace(true, 0, 0, 3840, 1080, sideBySide) == "" && UiText.DisplayPlace(false, 0, 0, 1920, 1080, new UiText.MonitorSpot[] { new(0, 0, 1920, 1080) }) == "", "side by side monitors are named from the left");
Check(UiText.DisplayPlace(false, 0, 0, 1920, 1080, stacked) == "위" && UiText.DisplayPlace(false, 0, 1080, 1920, 1080, stacked) == "아래" && UiText.DisplayPlace(false, 0, 0, 1920, 1080, across) == "왼쪽" && UiText.DisplayPlace(false, 1920, 0, 1920, 1080, across) == "가운데" && UiText.DisplayPlace(false, 3840, 0, 1920, 1080, across) == "오른쪽", "stacked and three-across monitors keep their place");
Check(UiText.DisplayPlace(false, 0, 0, 1920, 1080, corners) == "왼쪽 위" && UiText.DisplayPlace(false, 1920, 0, 1920, 1080, corners) == "오른쪽 위" && UiText.DisplayPlace(false, 0, 1080, 1920, 1080, corners) == "왼쪽 아래" && UiText.DisplayPlace(false, 1920, 1080, 1920, 1080, corners) == "오른쪽 아래" && UiText.DisplayPlace(false, -1920, 0, 1920, 1080, shifted) == "왼쪽" && UiText.DisplayPlace(false, 0, 0, 1920, 1080, shifted) == "오른쪽", "a grid names each corner, including a monitor left of the primary");
Check(UiText.DisplayChoice("전체 모니터", "왼쪽", 3840, 1080, false, true) == "전체 모니터 · 3840×1080" && UiText.DisplayChoice("모니터 2 (기본)", "오른쪽", 1920, 1080, true, false) == "오른쪽 · 기본 · 1920×1080" && UiText.DisplayChoice("모니터 1", "왼쪽", 1920, 1080, false, false) == "왼쪽 · 1920×1080" && UiText.DisplayChoice("모니터 1", " ", 1920, 1080, false, false) == "모니터 1 · 1920×1080", "the monitor picker leads with where the screen is");
Check(UiText.ConnectionFailure(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized), false) == "비밀번호가 올바르지 않습니다.", "a rejected login says the password is wrong");
Check(UiText.ConnectFailureField(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized)) == "password" && UiText.ConnectFailureField(new HttpRequestException("The SSL connection could not be established")) == "fingerprint" && UiText.ConnectFailureField(new HttpRequestException("reset")) == "address" && UiText.ConnectFailureField(new TimeoutException("slow")) == "address" && UiText.ConnectFailureField(new HttpRequestException("busy", null, HttpStatusCode.Conflict)) == "", "a failed connection marks the password, fingerprint, or address");
Check(UiText.HostFailureField(new SocketException((int)SocketError.AddressAlreadyInUse)) == "port" && UiText.HostFailureField(new InvalidOperationException("address already in use")) == "port" && UiText.HostFailureField(new InvalidOperationException("nope")) == "", "a taken port marks the host port");
Check(UiText.ConnectionFailure(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized), true) == "서버에서 연결을 끊었습니다. 다시 접속하세요.", "a dropped session is not described as a bad password");
Check(UiText.ConnectionFailure(new TaskCanceledException(), true) == "서버 응답이 끊어졌습니다. 다시 접속하세요.", "a stalled session says the connection dropped");
Check(UiText.ConnectionFailure(new TaskCanceledException(), false) == "서버 응답이 없습니다. 주소와 포트를 확인하세요.", "a login timeout still points at the address");
Check(UiText.ConnectCancelLabel() == "취소" && UiText.ConnectCancelled() == "접속을 취소했습니다.", "cancelling a connection is a quiet notice, not a failed login");
Check(UiText.HostStartLabel(false) == "접속 대기 시작" && UiText.HostStartLabel(true) == "취소" && UiText.HostStartCancelled() == "접속 대기를 취소했습니다." && !UiText.IsProblem(UiText.HostStartCancelled()), "cancelling host start is quiet and the same button does it");
Check(UiText.TransferCancelled() == "파일 전송을 취소했습니다." && !UiText.IsProblem(UiText.TransferCancelled()) && UiText.SessionOnPicture(UiText.TransferCancelled()), "a cancelled file transfer stays quiet on the picture");
Check(UiText.FolderPasteCancelled() == "붙여넣기를 취소했습니다." && !UiText.IsProblem(UiText.FolderPasteCancelled()) && UiText.SessionOnPicture(UiText.FolderPasteCancelled()), "cancelling a folder paste stays quiet");
Check(UiText.ClipboardCleared() == "클립보드를 지웠습니다." && !UiText.IsProblem(UiText.ClipboardCleared()) && UiText.SessionOnPicture(UiText.ClipboardCleared()), "clearing the clipboard stays quiet on the picture");
Check(ClipboardSync.ClipboardClear.PushEmpty(0, 0, false) && !ClipboardSync.ClipboardClear.PushEmpty(1, 0, false) && !ClipboardSync.ClipboardClear.PushEmpty(0, 3, false) && !ClipboardSync.ClipboardClear.PushEmpty(0, 0, true), "an empty local clipboard is pushed only when this sync did not write it");
Check(ClipboardSync.ClipboardClear.ApplyEmpty(0, 0, false, false, false) && !ClipboardSync.ClipboardClear.ApplyEmpty(1, 0, false, false, false) && !ClipboardSync.ClipboardClear.ApplyEmpty(0, 2, false, false, false) && !ClipboardSync.ClipboardClear.ApplyEmpty(0, 0, true, false, false) && !ClipboardSync.ClipboardClear.ApplyEmpty(0, 0, false, true, false) && !ClipboardSync.ClipboardClear.ApplyEmpty(0, 0, false, false, true), "an empty remote clipboard clears local text and files unless it is our own or already empty");
var picturePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
var pictureSize = ClipboardPaths.PngSize(picturePng);
Check(pictureSize.Width == 1 && pictureSize.Height == 1, "a clipboard picture decodes");
var oversizedPng = (byte[])picturePng.Clone();
oversizedPng[18] = 0x20; oversizedPng[22] = 0x20;
try { ClipboardPaths.ValidateImage(oversizedPng); Check(false, "compressed clipboard PNG dimensions are bounded"); }
catch (ArgumentException) { Check(true, "compressed clipboard PNG dimensions are bounded"); }
try { ClipboardPaths.ValidateImage([1, 2, 3, 4, 5, 6, 7, 8]); Check(false, "a non-png picture is rejected"); }
catch (ArgumentException ex) { Check(ex.Message == "클립보드 이미지는 PNG만 지원합니다.", "a non-png picture is rejected"); }
Check(!ClipboardSync.ClipboardClear.PushEmpty(0, 0, false, picturePng.Length) && !ClipboardSync.ClipboardClear.ApplyEmpty(0, 0, false, false, false, picturePng.Length), "a picture is not treated as an empty clipboard");
Check(!UiText.IsProblem(UiText.ImageClipboardReady()) && !UiText.IsProblem(UiText.ImageClipboardReceived()) && !UiText.IsProblem(UiText.ImageClipboardTooLarge()) && UiText.SessionOnPicture(UiText.ImageClipboardReceived()) && UiText.SessionOnPicture(UiText.ImageClipboardTooLarge()), "a clipboard picture stays quiet on the picture");
var formatted = "<p>안녕 <b>서식</b></p><script>alert(1)</script>";
Check(ClipboardHtml.Plain(formatted) == "안녕 서식" && ClipboardHtml.Plain("<p>첫 줄</p><p>둘째 &amp; 줄</p>") == "첫 줄\n둘째 & 줄", "formatted clipboard text keeps words and drops scripts");
var described = ClipboardHtml.Describe("<b>한글</b>");
Check(ClipboardHtml.Fragment(described) == "<b>한글</b>", "html clipboard bytes round-trip the fragment");
Check(!ClipboardSync.ClipboardClear.PushEmpty(0, 0, false, 0, formatted.Length) && !ClipboardSync.ClipboardClear.ApplyEmpty(0, 0, false, false, false, 0, formatted.Length), "formatted text is not treated as an empty clipboard");
Check(!UiText.IsProblem(UiText.HtmlClipboardReady()) && !UiText.IsProblem(UiText.HtmlClipboardReceived()) && !UiText.IsProblem(UiText.HtmlClipboardTooLarge()) && UiText.SessionOnPicture(UiText.HtmlClipboardReceived()), "formatted clipboard text stays quiet on the picture");
Check(UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("reset"))) && UiText.ShouldReconnect(UiText.ClassifyDrop(new TimeoutException("slow"))) && UiText.ShouldReconnect(UiText.ClassifyDrop(new TaskCanceledException())), "a dropped network or a timeout can reconnect");
Check(!UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized))) && !UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("busy", null, HttpStatusCode.Conflict))) && !UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("The SSL connection could not be established"))), "a rejected login, a busy host, or a certificate mismatch does not reconnect");
Check(UiText.ReconnectDelay(1) == TimeSpan.FromSeconds(2) && UiText.ReconnectDelay(2) == TimeSpan.FromSeconds(4) && UiText.ReconnectDelay(3) == TimeSpan.FromSeconds(8) && UiText.MaxAutomaticReconnects == 3, "reconnect waits grow and then stop");
Check(UiText.Reconnecting(1, 3, (int)UiText.ReconnectDelay(1).TotalSeconds) == "연결이 끊겼습니다. 2초 후 다시 연결합니다. (1/3)", "reconnect tells how long the next try waits");
Check(UiText.ReconnectHold() == "다시 연결하는 중…", "a dropped picture stays up while the next try is waiting");
Check(UiText.ReconnectNotice(UiText.Reconnecting(2, 3, 4)) && UiText.ReconnectNotice(UiText.ReconnectHold()) && !UiText.ReconnectNotice("연결됨") && UiText.SessionNoticeHolds(UiText.Reconnecting(1, 3, 1)) && UiText.SessionOnPicture(UiText.Reconnecting(1, 3, 1)), "a reconnect countdown stays on screen instead of looking connected");
Check(UiText.CanResume(UiText.DropKind.Network) && UiText.CanResume(UiText.DropKind.Occupied) && !UiText.CanResume(UiText.DropKind.Rejected) && !UiText.CanResume(UiText.DropKind.Certificate), "a dropped session keeps trying while its old login is still reserved");
Check(!UiText.ReplaceQuietSession(TimeSpan.FromSeconds(4.9)) && UiText.ReplaceQuietSession(TimeSpan.FromSeconds(5)), "a live session stays reserved and a quiet one can be replaced");
Check(!RemoteClient.SendJpeg(true, true) && RemoteClient.SendJpeg(false, true) && RemoteClient.SendJpeg(true, false), "an unchanged frame is not sent again after the viewer has it");
Check(RemoteClient.FrameInterval(30, false) == TimeSpan.FromSeconds(1.0 / 30), "motion keeps the selected frame interval");
Check(RemoteClient.FrameInterval(30, true) == TimeSpan.FromMilliseconds(100), "an unchanged screen is checked ten times a second");
Check(RemoteClient.FrameInterval(5, true) == TimeSpan.FromSeconds(1.0 / 5), "idle polling does not go faster than a slower selected rate");
Check(UiText.ShortSecret("99D8D8CC4DC57302") == "99D8D8CC4DC57302" && UiText.ShortSecret("99D8D8CC4DC5730254F7947AD64ADD88") == "99D8D8CC4DC57302…", "a long fingerprint is shortened for the host page");
Check(UiText.ClientConnected(null) == "클라이언트 연결됨", "a client without an address is still announced");
Check(UiText.ClientConnected(IPAddress.Parse("::ffff:192.168.0.8")) == "클라이언트 연결됨 · 192.168.0.8", "a mapped client address is shown as IPv4");
Check(UiText.ClientConnected(IPAddress.Parse("fe80::1%12")) == "클라이언트 연결됨 · fe80::1", "a link-local client address drops the zone id");
Check(UiText.Listening(45831) == "접속 대기 중 · 포트 45831" && UiText.Listening(0) == "접속 대기 중", "listening status keeps the port");
Check(UiText.SessionEnded("192.168.0.2", UiText.SessionEndKind.Left, 45831) == "192.168.0.2 연결이 종료되었습니다. 접속 대기 중 · 포트 45831" && UiText.SessionEnded("192.168.0.2", UiText.SessionEndKind.Kicked, 45831) == "192.168.0.2 연결을 끊었습니다. 접속 대기 중 · 포트 45831" && UiText.SessionEnded(" ", UiText.SessionEndKind.TimedOut, 45831) == "상대 PC 응답이 없어 연결을 끊었습니다. 접속 대기 중 · 포트 45831", "the host says whether a client left, was disconnected, or stopped answering");
Check(UiText.HostStopped(null) == "서버 중지됨 · 저장된 비밀번호로 다시 시작할 수 있습니다." && UiText.HostStopped("192.168.0.2") == "192.168.0.2 연결을 끊고 서버를 중지했습니다.", "stopping the server names the client that was dropped");
Check(UiText.HostSessionEnded(UiText.SessionEnded("10.0.0.8", UiText.SessionEndKind.Left, 1)) && UiText.SessionOnPicture(UiText.HostStopped("10.0.0.8")) && UiText.SessionNoticeDuration(UiText.SessionEnded("10.0.0.8", UiText.SessionEndKind.Kicked, 1)) == TimeSpan.FromSeconds(12) && !UiText.HostSessionEnded("서버에서 연결을 끊었습니다. 다시 접속하세요."), "a client leaving this pc stays on the remote picture");
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", null, null) == "Personal Remote Desktop 0.9.59", "an idle window keeps the product title");
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", "10.0.0.8:45831", "192.168.0.2") == "10.0.0.8:45831 — 192.168.0.2 연결됨 — Personal Remote Desktop 0.9.59", "both sessions name the remote pc, inbound client, and product");
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", "10.0.0.8:45831", null) == "10.0.0.8:45831 — Personal Remote Desktop 0.9.59", "an open remote session names the window");
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", null, "192.168.0.2") == "192.168.0.2 연결됨 — Personal Remote Desktop 0.9.59", "an inbound client names the window");
Check(UiText.HostTabLabel(null) == "이 PC" && UiText.HostTabLabel("192.168.0.2") == "이 PC · 192.168.0.2", "the host tab names the connected client");
Check(UiText.FullscreenTitle(null, null) == "Personal Remote Desktop · F11 또는 Esc로 복귀" && UiText.FullscreenTitle("10.0.0.8:45831", "192.168.0.2") == "10.0.0.8:45831 — 192.168.0.2 연결됨 · F11 또는 Esc로 복귀", "fullscreen names the remote pc and an inbound client");
Check(UiText.EscapeLeavesFullscreen(true, false) && !UiText.EscapeLeavesFullscreen(true, true) && !UiText.EscapeLeavesFullscreen(false, false), "escape leaves fullscreen only after the keyboard returns to this pc");
Check(UiText.ClosePrompt(null, null) == null && UiText.ClosePrompt("  ", " ") == null, "an idle window closes without asking");
Check(UiText.ClosePrompt("10.0.0.8:45831", null) == "10.0.0.8:45831 화면을 보고 있습니다. 종료하고 연결을 끊을까요?" && UiText.ClosePrompt(null, "192.168.0.2") == "192.168.0.2 이 PC를 보고 있습니다. 종료하고 연결을 끊을까요?", "exit asks before it drops a live picture or a guest");
Check(UiText.ClosePrompt("10.0.0.8:45831", "192.168.0.2") == "10.0.0.8:45831 화면을 보고 있고, 192.168.0.2 이 PC를 보고 있습니다. 종료하고 둘 다 끊을까요?", "exit names both sessions when each side is connected");
Check(!UiText.AskBeforeRemoteBack(true, true) && UiText.AskBeforeRemoteBack(false, true) && !UiText.AskBeforeRemoteBack(false, false) && !UiText.AskBeforeRemoteBack(true, false), "back closes the controls before it asks to drop a session");
Check(UiText.DisconnectPrompt("10.0.0.8:45831") == "10.0.0.8:45831 화면을 보고 있습니다. 연결을 끊을까요?" && UiText.DisconnectPrompt("  ") == "원격 화면을 보고 있습니다. 연결을 끊을까요?" && UiText.DisconnectPrompt(null) == "원격 화면을 보고 있습니다. 연결을 끊을까요?" && !UiText.IsProblem(UiText.DisconnectPrompt("10.0.0.8:45831")), "leaving a remote picture asks before the session drops");
var fullView = PictureView.Full;
var closer = PictureZoom.At(fullView, 2, 0.25, 0.75);
var zoomedPoint = PictureZoom.ToDisplay(closer, 0.25, 0.75);
var panned = PictureZoom.Pan(closer, 0.2, 0);
var restoredView = PictureZoom.At(closer, 1, 0.5, 0.5);
var cappedView = PictureZoom.At(fullView, 9, 0.5, 0.5);
Check(fullView.Zoom == 1 && fullView.Width == 1 && fullView.X == 0, "a picture starts at its full size");
Check(Math.Abs(closer.Zoom - 2) < 0.001 && Math.Abs(closer.Width - 0.5) < 0.001 && Math.Abs(zoomedPoint.X - 0.25) < 0.001 && Math.Abs(zoomedPoint.Y - 0.75) < 0.001, "zoom keeps the chosen point on the whole display");
Check(panned.X < closer.X && Math.Abs(panned.Width - closer.Width) < 0.001 && PictureZoom.CanPan(closer) && !PictureZoom.CanPan(fullView), "dragging a zoomed picture moves the view with the pointer");
Check(restoredView.Zoom == 1 && restoredView.Width == 1 && restoredView.X == 0 && Math.Abs(cappedView.Zoom - 4) < 0.001, "picture zoom returns to the whole screen and stops at four times");
Check(PictureZoom.Notice(restoredView) == "확대를 원래대로 되돌렸습니다." && PictureZoom.Notice(closer) == "화면을 2.0배로 확대했습니다. 휠 버튼으로 끌면 보이는 영역을 옮깁니다." && !UiText.IsProblem(PictureZoom.Notice(closer)), "zoom tells the viewer how far the picture moved");
Check(UiText.TrayHidden(true) == "접속 대기는 계속됩니다. 아이콘을 누르면 창이 다시 열립니다." && UiText.TrayHidden(false) == "아이콘을 누르면 창이 다시 열립니다." && UiText.TrayTip(true) == "Personal Remote Desktop · 접속 대기 중" && UiText.TrayOpen() == "창 열기" && UiText.TrayExit() == "종료" && !UiText.IsProblem(UiText.TrayHidden(true)), "closing the window leaves a quiet tray hint");
Check(WindowClose.Decide(false, false, true) == WindowClose.Result.Hide && WindowClose.Decide(true, false, true) == WindowClose.Result.Ask && WindowClose.Decide(true, true, true) == WindowClose.Result.Exit && WindowClose.Decide(true, false, false) == WindowClose.Result.Exit, "the close button hides and only exit can end a live session");
var cleanupSequenceType = typeof(Desktop).Assembly.GetType("RemoteDesktop.ShutdownCleanup", throwOnError: true)!;
var runCleanupSequence = cleanupSequenceType.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
var completedCleanupSteps = new List<string>();
var failedCleanupSteps = new List<string>();
var injectedCleanupSteps = new (string Name, Func<Task> Cleanup)[]
{
    ("injected failure", () => Task.FromException(new InvalidOperationException("injected"))),
    ("clipboard", () => { completedCleanupSteps.Add("clipboard"); return Task.CompletedTask; }),
    ("host", async () => { await Task.Yield(); completedCleanupSteps.Add("host"); })
};
Action<string, Exception> recordCleanupFailure = (name, _) => failedCleanupSteps.Add(name);
await (Task)runCleanupSequence.Invoke(null, [injectedCleanupSteps, recordCleanupFailure])!;
Check(failedCleanupSteps.SequenceEqual(new[] { "injected failure" }) && completedCleanupSteps.SequenceEqual(new[] { "clipboard", "host" }), "a shutdown-stage exception does not skip clipboard or host disposal");
Check(UiText.ConnectedFor(TimeSpan.FromSeconds(5)) == "연결된 지 00:05" && UiText.ConnectedFor(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(7)) == "연결된 지 03:07", "connection duration stays in minutes and seconds");
Check(UiText.ConnectedFor(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(9)) == "연결된 지 2:04:09" && UiText.ConnectedFor(TimeSpan.FromSeconds(-3)) == "연결된 지 00:00", "long connections include hours and negative time is clamped");
Check(UiText.NeedsAttention(true, false) && !UiText.NeedsAttention(true, true) && !UiText.NeedsAttention(false, false), "only a newly arrived client flashes an inactive window");
var inputBacklog = new InputBacklog();
Check(!inputBacklog.Take(), "an idle input queue does not release keys");
Check(inputBacklog.Note() && !inputBacklog.Note() && inputBacklog.Take() && !inputBacklog.Take(), "a full input queue releases stuck keys once and keeps the session");
inputBacklog.Note();
inputBacklog.Reset();
Check(!inputBacklog.Take() && inputBacklog.Note(), "a new session does not inherit an old input backlog");
Check(UiText.InputBacklog() == "입력이 밀려 눌린 키와 버튼을 놓습니다.", "an input backlog tells the viewer the keys were released");
Check(UiText.PasswordReveal(false) == "보기" && UiText.PasswordReveal(true) == "숨김" && UiText.PasswordRevealTip(false) == "비밀번호 보기" && UiText.PasswordRevealTip(true) == "비밀번호 숨기기", "a password can be shown before it is saved");
Check(UiText.CapsLockWarning() == "Caps Lock이 켜져 있습니다." && !UiText.ShowCapsLock(false, true) && !UiText.ShowCapsLock(true, false) && UiText.ShowCapsLock(true, true), "caps lock is warned only while typing a password");
Check(UiText.CopiedLabel("복사", false) == "복사" && UiText.CopiedLabel("복사", true) == "복사됨" && UiText.CopiedLabel("지문 복사", true) == "복사됨" && UiText.CopiedLabel("지문 복사", false) == "지문 복사", "a copy button shows which value was copied");
Check(!UiText.SelectEntireValue(true) && UiText.SelectEntireValue(false), "the first click selects an address or password, and the next click edits it");
Check(UiText.NoticeFits("접속 실패: 서버 인증서 지문이 일치하지 않습니다. 지문을 비우면 비밀번호로 다시 확인합니다.", 3, 28) && UiText.NoticeFits("192.168.56.1:45831 복사됨 · 상대 PC의 주소 칸에 붙여 넣으세요.", 3, 24) && UiText.NoticeFits("파일 보내기 실패: 클립보드가 변경되었거나 전송이 만료되었습니다. 다시 복사하세요.", 2, 32), "connection and clipboard notices fit on the card");
Check(!UiText.NoticeFits("한 줄", 0, 20) && !UiText.NoticeFits(new string('가', 90), 2, 28), "a notice does not claim to fit when the card is too small");
Check(UiText.CaptureSystemShortcut(0x12, true, false, false, false, false, false) && UiText.CaptureSystemShortcut(0x5B, true, false, false, false, false, false), "alt and the windows key go to the remote pc");
Check(UiText.CaptureSystemShortcut(0x73, true, true, false, true, false, false) && UiText.CaptureSystemShortcut(0x09, true, true, false, true, false, false), "alt+f4 and alt+tab stay on the remote pc");
Check(UiText.CaptureSystemShortcut(0x45, true, false, false, false, true, false), "win+e does not open the local explorer");
Check(UiText.CaptureSystemShortcut(0x1B, true, false, true, false, false, false) && UiText.CaptureSystemShortcut(0x2C, true, false, false, false, false, false), "ctrl+shift+esc and print screen are sent remotely");
Check(UiText.CompletesReleaseChord(0x10, true, true, false) && UiText.CompletesReleaseChord(0xA0, true, true, false) && UiText.CompletesReleaseChord(0x12, true, false, true) && UiText.CompletesReleaseChord(0xA4, true, false, true) && UiText.CompletesReleaseChord(0x11, false, true, true) && UiText.CompletesReleaseChord(0xA3, false, true, true), "ctrl+alt+shift returns the keyboard to this pc");
Check(!UiText.CompletesReleaseChord(0x10, true, false, true) && !UiText.CompletesReleaseChord(0x41, true, true, true) && !UiText.CompletesReleaseChord(0x12, false, true, true) && !UiText.CompletesReleaseChord(0x11, false, true, false), "two modifiers and ordinary keys still go to the remote pc");
Check(UiText.InputHint(true) == "화면을 클릭하면 키보드가 원격 PC로 갑니다. Ctrl+Alt+Shift로 키보드를 이 PC에 되돌립니다." && UiText.InputHint(false) == "입력 제어가 꺼져 있습니다. 화면만 보는 중입니다." && UiText.InputReleased() == "키보드를 이 PC로 되돌렸습니다.", "the picture explains how to bring the keyboard back");
Check(!UiText.CaptureSystemShortcut(0x41, true, false, false, false, false, false) && UiText.CaptureSystemShortcut(0x41, false, false, false, false, false, true) && !UiText.CaptureSystemShortcut(0x41, false, false, false, false, false, false), "plain typing stays local until a captured modifier is held");
Check(!UiText.ShowHostSetup(true) && UiText.ShowHostSetup(false), "host setup hides while the server is running");
Check(UiText.HostCardTitle(false, false, null) == "접속 받기" && UiText.HostCardTitle(false, true, null) == "접속 대기 중" && UiText.HostCardTitle(true, true, "192.168.0.8") == "192.168.0.8 연결 중" && UiText.HostCardTitle(true, true, " ") == "이 PC를 보는 중", "the host card names waiting and the connected client");
Check(UiText.HostCardTitle(true, true, "192.168.0.8", true) == "192.168.0.8 응답 없음" && UiText.HostCardTitle(true, true, " ", true) == "응답이 없습니다" && UiText.ShareTitle(true, true) == "응답이 멈춘 접속입니다" && UiText.ShareHint(true, true).Contains("현재 접속 끊기"), "a quiet client is not described as still watching");
Check(UiText.FullscreenAway(null) == "F11 또는 Esc로 이 창에 되돌립니다." && UiText.FullscreenAway(" 10.0.0.8:45831 ") == "10.0.0.8:45831 화면입니다. F11 또는 Esc로 이 창에 되돌립니다.", "the original window explains a fullscreen session");
Check(UiText.EdgeToEdge(true, true, false) && !UiText.EdgeToEdge(true, true, true) && !UiText.EdgeToEdge(true, false, false) && !UiText.EdgeToEdge(false, true, false), "the picture fills this window only while the session is shown here");
Check(UiText.ShareTitle(false) == "이 PC에 접속하려면" && UiText.ShareTitle(true) == "지금은 한 대만 연결됩니다" && UiText.ShareHint(false).Contains("수락 없이") && UiText.ShareHint(true).StartsWith("이미 다른 기기가 연결되어 있습니다."), "an occupied host says another device cannot connect");
Check(UiText.ShareTitle(false, false, false) == "접속 대기를 시작하면" && UiText.ShareHint(false, false, false).Contains("접속 대기를 시작") && !UiText.ShareHint(false, false, false).Contains("수락 없이"), "a stopped host does not say another pc can connect yet");
Check(UiText.AddressCopied("192.168.56.1:45831", true) == "192.168.56.1:45831 복사됨 · 상대 PC의 주소 칸에 붙여 넣으세요." && UiText.AddressCopied("192.168.56.1:45831", false) == "192.168.56.1:45831 복사됨 · 접속 대기를 시작한 뒤에 붙여 넣으세요." && UiText.NoticeFits(UiText.AddressCopied("192.168.56.1:45831", false), 3, 24), "copying an address says whether this pc is accepting connections");
var missingAddress = UiText.CheckConnectForm("  ", "45831", "secret", "");
var missingPort = UiText.CheckConnectForm("10.0.0.8", "abc", "secret", "");
var missingPassword = UiText.CheckConnectForm("10.0.0.8", "45831", "", "  ");
var badFingerprint = UiText.CheckConnectForm("10.0.0.8", "45831", "secret", "abcd");
var ready = UiText.CheckConnectForm("10.0.0.8", "45831", "secret", new string('a', 64));
Check(missingAddress.Problem == "서버 주소를 입력하세요." && missingAddress.Field == "address" && missingPort.Problem == "포트 범위: 1–65535" && missingPort.Field == "port" && missingPassword.Problem == "접속 비밀번호를 입력하세요." && missingPassword.Field == "password", "an empty address, port, or password is explained before connecting");
Check(badFingerprint.Problem == "서버의 SHA-256 지문 64자리를 입력하세요." && badFingerprint.Field == "fingerprint" && ready.Problem == null && UiText.CheckConnectForm("10.0.0.8", "45831", "secret", "").Problem == null, "a partial fingerprint is rejected and a blank one can be learned from the password");
var badHostPort = UiText.CheckHostSetup("80", "12345678", false);
var shortHostPassword = UiText.CheckHostSetup("45831", "short", true);
var emptyConfirm = UiText.CheckHostSetup("45831", "", true, true);
var savedHost = UiText.CheckHostSetup("45831", "", false);
var readyHost = UiText.CheckHostSetup("45831", "12345678", true);
Check(badHostPort.Problem == "포트 범위: 1024–65535" && badHostPort.Field == "port" && shortHostPassword.Problem == "비밀번호를 8~1024자 입력하세요." && shortHostPassword.Field == "password", "a host port or new password is checked before waiting starts");
Check(emptyConfirm.Problem == "현재 비밀번호를 입력하세요." && emptyConfirm.Field == "password" && savedHost.Problem == null && readyHost.Problem == null, "a saved host can wait without typing the password again");
var mismatch = UiText.CheckHostSetup("45831", "12345678", true, false, "12345679", true);
var missingMatch = UiText.CheckHostSetup("45831", "12345678", true, false, "", true);
var matched = UiText.CheckHostSetup("45831", "12345678", true, false, "12345678", true);
var uncheckedMatch = UiText.CheckHostSetup("45831", "12345678", true, true, "다른값", false);
Check(mismatch.Problem == "비밀번호 확인이 일치하지 않습니다." && mismatch.Field == "confirm" && missingMatch.Problem == "비밀번호를 한 번 더 입력하세요." && missingMatch.Field == "confirm" && matched.Problem == null && uncheckedMatch.Problem == null, "a new host password is saved only when it is entered the same way twice");
Check(!UiText.RemotePointer(0, false) && !UiText.RemotePointer(20, false) && UiText.RemotePointer(20.1, false) && UiText.RemotePointer(0, true), "the toolbar strip does not move the remote pointer unless a drag is captured");
Check(UiText.RemoteButtonRelease(true) && !UiText.RemoteButtonRelease(false) && !UiText.RemotePointer(8, false), "the toolbar does not release or scroll a remote button unless a drag is captured");
Check(UiText.JpegFullColor(75) && UiText.JpegFullColor(55) && !UiText.JpegFullColor(40) && !UiText.JpegFullColor(25), "normal picture quality keeps full color");
Check(UiText.CrispPicture(1) && UiText.CrispPicture(2.04) && !UiText.CrispPicture(0.67) && !UiText.CrispPicture(1.4), "a picture near its own size stays crisp");
var overText = CursorPlacement.Place(40, 70, 2, 4, 16, 16, 200, 100, 200, 200);
var outside = CursorPlacement.Place(40, 10, 2, 4, 16, 16, 200, 100, 200, 200);
Check(overText.Shown && Math.Abs(overText.Left - 38) < 0.01 && Math.Abs(overText.Top - 66) < 0.01 && overText.Width == 16 && overText.Height == 16, "the remote cursor keeps its hotspot on the picture");
Check(!outside.Shown, "the remote cursor hides in the empty margin");
Check(CursorPlacement.InView(0.25, 0.75, 0, 0, 1, 1, out var fullX, out var fullY) && fullX == 0.25 && fullY == 0.75, "a full view keeps the pointer where it is");
Check(CursorPlacement.InView(0.25, 0.75, 0.2, 0.5, 0.5, 0.5, out var zoomX, out var zoomY) && Math.Abs(zoomX - 0.1) < 0.0001 && Math.Abs(zoomY - 0.5) < 0.0001, "a zoomed view remaps the pointer into the picture");
Check(!CursorPlacement.InView(0.1, 0.75, 0.2, 0.5, 0.5, 0.5, out _, out _) && !CursorPlacement.InView(double.NaN, 0.5, 0, 0, 1, 1, out _, out _), "a pointer outside the zoomed view is not drawn");
Check(!CursorPlacement.InView(0.5, 0.5, 0, 0, double.PositiveInfinity, 1, out _, out _), "an invalid viewport does not place the pointer");
var centered = CursorPlacement.PlaceNormalized(0.5, 0.5, 2, 4, 16, 16, 200, 100, 200, 200);
Check(centered.Shown && Math.Abs(centered.Left - 98) < 0.01 && Math.Abs(centered.Top - 96) < 0.01 && centered.Width == 16 && centered.Height == 16, "the local cursor keeps its hotspot on a letterboxed picture");
Check(!CursorPlacement.PlaceNormalized(1.2, 0.5, 0, 0, 16, 16, 200, 100, 200, 200).Shown && !CursorPlacement.PlaceNormalized(double.NaN, 0.5, 0, 0, 16, 16, 200, 100, 200, 200).Shown, "a pointer off the picture is not drawn");
var shot = CursorGlyph.Capture();
Check(!shot.Visible || (shot.Png != null && shot.Png.Length > 8 && shot.Png[0] == 0x89 && shot.Png[1] == 0x50 && shot.Hash.Length == 64 && shot.Width > 0 && shot.Height > 0), "a visible remote cursor is a png");
using (var picture = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
{
    using (var graphics = System.Drawing.Graphics.FromImage(picture)) graphics.Clear(System.Drawing.Color.FromArgb(255, 220, 16, 24));
    var sharp = ScreenJpeg.Encode(picture, 75);
    var small = ScreenJpeg.Encode(picture, 25);
    Check(FrameLimits.TryReadJpegDimensions(sharp, out var jpegWidth, out var jpegHeight) && jpegWidth == 32 && jpegHeight == 32
        && !FrameLimits.TryReadJpegDimensions([0xFF, 0xD8, 0xFF, 0xDA, 0, 2], out _, out _), "JPEG frame dimensions are checked before decoding");
    Check(sharp.Length > 4 && sharp[0] == 0xFF && sharp[1] == 0xD8 && ScreenJpeg.Chroma(sharp) == 444, "normal quality keeps red and blue samples");
    Check(small[0] == 0xFF && small[1] == 0xD8 && ScreenJpeg.Chroma(small) == 420, "the lowest quality saves bandwidth");
    using var decoded = new System.Drawing.Bitmap(new MemoryStream(sharp));
    var pixel = decoded.GetPixel(16, 16);
    Check(pixel.R > 160 && pixel.R > pixel.G + 60 && pixel.R > pixel.B + 60, "a red screen stays red after compression");
}
Check(UiText.HostPresence(true, true) == "LiveBrush" && UiText.HostPresence(false, true) == "AccentBrush" && UiText.HostPresence(false, false) == "MutedBrush", "the host dot distinguishes a connected client from waiting");
Check(!UiText.HostShowsQuiet(TimeSpan.FromSeconds(4.9)) && UiText.HostShowsQuiet(TimeSpan.FromSeconds(5)) && UiText.HostPresence(true, true, true) == "DangerBrush", "the host marks a client quiet when a new login may replace it");
Check(UiText.ElapsedClock(TimeSpan.FromSeconds(5)) == "00:05" && UiText.ElapsedClock(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(9)) == "2:04:09", "elapsed time stays compact");
Check(UiText.FrameHealth(15, 1, 40, false) == "15 fps · 40 ms" && UiText.FrameHealth(0, 1, 40, true) == "화면 유지 · 40 ms" && UiText.FrameHealth(0, 1, -1, false) == "수신 대기 · —", "a still picture stays distinct from a missing frame");
Check(UiText.StaleFrame() == "화면 응답 없음" && UiText.FrameStale(TimeSpan.FromSeconds(3)) && !UiText.FrameStale(TimeSpan.FromSeconds(2.9)), "a silent stream is reported after three seconds");
Check(UiText.PicturePaused() == "화면 일시중지", "a hidden picture pauses without looking like a dropped session");
Check(UiText.ViewerWatching(false, false, false, true) && !UiText.ViewerWatching(false, false, true, true) && !UiText.ViewerWatching(false, false, false, false) && UiText.ViewerWatching(true, false, true, false) && !UiText.ViewerWatching(true, true, false, true), "frames continue only while the remote picture is on screen");
Check(UiText.SessionPreset(null, "균형", 15) == "균형" && UiText.SessionPreset("10.0.0.8:45831", "균형", 15) == "10.0.0.8:45831 · 균형", "the toolbar names the remote pc without a frame rate");
Check(UiText.SessionPreset("10.0.0.8:45831", "고화질", 15, "저대역폭") == "10.0.0.8:45831 · 고화질 → 저대역폭" && UiText.SessionPreset(null, "고화질", 15, "고화질") == "고화질", "the toolbar shows when the stream quality was lowered");
Check(UiText.QualityShift("고화질", null, "고화질") == null && UiText.QualityShift("고화질", null, "저대역폭") == "끊김을 줄이려고 화질을 낮췄습니다. 고화질 대신 저대역폭." && UiText.QualityShift("고화질", "저대역폭", "저대역폭") == null && UiText.QualityShift("고화질", "저대역폭", "고화질") == "여유가 생겨 화질을 고화질로 되돌렸습니다.", "quality changes are announced once");
Check(UiText.QualityRestored("균형") == "여유가 생겨 화질을 균형으로 되돌렸습니다." && UiText.QualityRestored("저대역폭") == "여유가 생겨 화질을 저대역폭으로 되돌렸습니다.", "restored quality uses the right Korean particle");
Check(UiText.SessionHandleTip() == "끌어서 이동 · 화살표를 눌러 세션 조작 열기", "the compact handle explains how to move and open the toolbar");
Check(UiText.IsProblem("클립보드 보내기 실패: 다시 시도하세요.") && UiText.IsProblem("클립보드 동기화 오류: 다시 시도하세요.") && UiText.IsProblem("붙여넣기 중단: 다시 시도하세요."), "session failures stay visible");
Check(UiText.IsProblem("접속 대기 실패: 이 포트는 이미 사용 중입니다.") && UiText.IsProblem("모니터 목록을 가져오지 못했습니다.") && UiText.IsProblem("끊김 줄이기 설정을 저장하지 못했습니다.") && UiText.SessionOnPicture("모니터 목록을 가져오지 못했습니다."), "a failed host or a failed monitor list stays visible");
Check(UiText.SessionBaseline(true) == "연결됨" && UiText.SessionBaseline(false) == "화면만 보는 중", "a live session returns to connected, and view-only keeps its own label");
Check(!UiText.SessionNoticeHolds("연결됨") && !UiText.SessionNoticeHolds("화면 품질: 고화질") && !UiText.SessionNoticeHolds("클립보드 파일 수신 완료"), "ordinary session notes settle back to the connection state");
Check(UiText.SessionNoticeHolds("클립보드 보내기 실패: 다시 시도하세요.") && UiText.SessionNoticeHolds(UiText.ClipboardFileProgress("클립보드 수신", 0, 1, "메모.txt", 50, 200)), "failures and an unfinished file copy stay on screen");
Check(UiText.SessionNoticeHolds(UiText.StaleFrame()) && UiText.SessionOnPicture(UiText.StaleFrame()), "a stalled picture stays visible outside the toolbar until frames resume");
Check(UiText.FileDropHint(true) == "놓으면 상대 PC에서 붙여넣을 수 있습니다" && UiText.FileDropHint(false) == "입력 제어를 켜야 파일을 보낼 수 있습니다", "a file drag explains where the files will go");
Check(UiText.FileDropStarted(1) == "파일을 상대 PC로 보내는 중" && UiText.FileDropStarted(3) == "3개 파일을 상대 PC로 보내는 중", "a dropped batch says how many files are leaving");
Check(UiText.SessionOnPicture("클립보드 파일 준비 완료 · 상대 탐색기에서 붙여넣으세요.") && UiText.SessionOnPicture(UiText.ClipboardFileProgress("클립보드 전송", 0, 1, "메모.txt", 50, 200)) && UiText.SessionOnPicture(UiText.FileDropStarted(2)) && UiText.SessionOnPicture(UiText.FileDropHint(false)) && UiText.SessionOnPicture("파일 보내기 실패: 다시 시도하세요."), "a file transfer stays on the picture while the toolbar is hidden");
Check(!UiText.SessionOnPicture("연결됨") && !UiText.SessionOnPicture("화면 품질: 고화질") && !UiText.SessionOnPicture("화면만 보는 중") && !UiText.SessionOnPicture(""), "ordinary session notes do not cover the remote picture");
Check(UiText.InboundArrived("192.168.0.2") == "192.168.0.2 이 PC에 연결됨" && UiText.InboundArrived("  ") == "이 PC에 연결됨", "a new client is named when it reaches this pc");
Check(UiText.HostPageVisible(true, false) && !UiText.HostPageVisible(false, false) && !UiText.HostPageVisible(true, true) && !UiText.HostPageVisible(false, true), "the host page is on screen only while its tab is selected outside fullscreen");
Check(UiText.AnnounceInbound(true, false) && !UiText.AnnounceInbound(true, true) && !UiText.AnnounceInbound(false, false) && !UiText.AnnounceInbound(false, true), "a new client is announced only while the host page is hidden");
Check(UiText.InboundArrival(UiText.InboundArrived("192.168.0.2")) && UiText.SessionOnPicture(UiText.InboundArrived("192.168.0.2")), "an inbound arrival stays on the picture after the toolbar hides");
Check(UiText.SessionNoticeDuration(UiText.InboundArrived("192.168.0.2")) == TimeSpan.FromSeconds(12) && UiText.SessionNoticeDuration("화면 품질: 고화질") == TimeSpan.FromSeconds(4.5), "an inbound arrival stays longer than an ordinary session note");
Check(UiText.SessionOnPicture(UiText.InputReleased()) && UiText.SessionOnPicture(UiText.InputBacklog()), "input release notices stay visible with the toolbar closed");
Check(!UiText.IsProblem("연결됨") && !UiText.IsProblem("화면 품질: 균형") && !UiText.IsProblem(""), "ordinary session status is not treated as a failure");
recent.SelectedTab = "host";
var recentPath = Path.Combine(Path.GetTempPath(), "prd-recent-" + Guid.NewGuid().ToString("N") + ".json");
recent.Save(recentPath);
var loadedRecent = ClientSettings.Load(recentPath);
Check(loadedRecent.Recent.Count == 4 && loadedRecent.Recent[0].Address == "10.1.0.4:45831" && loadedRecent.Recent[0].FrameRate == 15 && loadedRecent.OpenHostTab && !File.ReadAllText(recentPath).Contains("password", StringComparison.OrdinalIgnoreCase), "recent hosts and the last tab survive reload without a password");
File.Delete(recentPath);

await Password();
await Session();
await SessionRevocationRace();
await ClipboardFiles();
if (args.Contains("--capture")) await Capture();
Console.WriteLine(passed + " integration checks passed.");

async Task Password()
{
    await using var site = await OpenHost();
    Check(Desktop.SmoothCapture, "direct host enables stutter reduction from its saved setting");
    Check(site.Certificate.HasPrivateKey, "server TLS certificate has a current-user private key");
    var certificateTimeMethod = typeof(RemoteClient).GetMethod("IsWithinValidity", BindingFlags.Static | BindingFlags.NonPublic)!;
    var validityNow = new DateTimeOffset(2026, 3, 8, 19, 0, 0, TimeSpan.FromHours(9));
    var validityStartUtc = validityNow.UtcDateTime.AddMinutes(-1);
    var validityEndUtc = validityNow.UtcDateTime.AddMinutes(1);
    var validUtcWindow = (bool)certificateTimeMethod.Invoke(null, [validityStartUtc, validityEndUtc, validityNow])!;
    var validLocalWindow = (bool)certificateTimeMethod.Invoke(null, [validityStartUtc.ToLocalTime(), validityEndUtc.ToLocalTime(), validityNow])!;
    var expiredBeforeWindow = (bool)certificateTimeMethod.Invoke(null, [validityStartUtc, validityEndUtc, validityNow.AddMinutes(-2)])!;
    var expiredAfterWindow = (bool)certificateTimeMethod.Invoke(null, [validityStartUtc, validityEndUtc, validityNow.AddMinutes(2)])!;
    Check(validUtcWindow && validLocalWindow && !expiredBeforeWindow && !expiredAfterWindow, "certificate validity compares UTC and local DateTime representations as the same instants");
    Check(site.Settings.Verify(site.Password) && !site.Settings.Verify("wrong"), "salted password verification");
    var hostPath = Path.Combine(site.Root, "host.json");
    var persistedHost = new HostSettings { Port = 45999, SecureDesktopEnabled = false };
    persistedHost.SetPassword(site.Password, hostPath);
    var restartedHost = HostSettings.Load(hostPath);
    Check(restartedHost.HasPassword && restartedHost.Verify(site.Password), "server password works after restart without re-entry");
    Check(restartedHost.Port == 45999, "server port survives restart");
    Check(!restartedHost.SecureDesktopEnabled, "secure desktop kill switch survives restart");
    Check(restartedHost.UseSmoothHost, "stutter reduction stays enabled when the setting is absent");
    File.WriteAllText(hostPath, "{\"Port\":45999}");
    Check(HostSettings.Load(hostPath).UseSmoothHost && HostSettings.Load(hostPath).SecureDesktopEnabled, "older host settings keep stutter reduction and secure desktop enabled");
    var smoothOff = HostSettings.Load(hostPath);
    smoothOff.SmoothHost = false;
    smoothOff.Save(hostPath);
    Check(!HostSettings.Load(hostPath).UseSmoothHost, "stutter reduction can be turned off");
    Check(!File.ReadAllText(hostPath).Contains(site.Password), "server never persists plaintext password");
    var dataDirectory = Path.Combine(site.Root, "server-data");
    Directory.CreateDirectory(dataDirectory);
    new HostSettings { Port = 46001 }.SetPassword(site.Password, dataDirectory);
    Check(Directory.Exists(dataDirectory) && File.Exists(Path.Combine(dataDirectory, "settings.json")), "password save writes settings.json inside the data directory");
    Check(HostSettings.Load(dataDirectory).Verify(site.Password) && HostSettings.Load(dataDirectory).Port == 46001, "password load accepts the data directory");
    var savedPath = Path.Combine(site.Root, "client.json");
    new ClientSettings { Address = "192.168.1.20:45831", Fingerprint = site.Pin, Display = "all", FrameQuality = 75, FrameRate = 30, ClipboardAutoSync = false }.Save(savedPath);
    var saved = ClientSettings.Load(savedPath);
    Check(saved.Address == "192.168.1.20:45831" && saved.Fingerprint == site.Pin && saved.Display == "all" && saved.FrameQuality == 75 && saved.FrameRate == 30 && !saved.ClipboardAutoSync, "last connection and session preferences survive reload");
    Check(!File.ReadAllText(savedPath).Contains("password", StringComparison.OrdinalIgnoreCase), "connection preferences contain no password");
    var remembered = new ClientSettings { Address = saved.Address, WindowLeft = 80, WindowTop = 90, WindowWidth = 1024, WindowHeight = 700, WindowMaximized = true };
    remembered.Save(savedPath);
    var reloadedPlacement = ClientSettings.Load(savedPath);
    Check(reloadedPlacement.TryGetWindowPlacement(0, 0, 1920, 1080, out var reloadedWindow) && reloadedWindow.Left == 80 && reloadedWindow.Top == 90 && reloadedWindow.Maximized, "window placement survives reload");
    File.WriteAllText(savedPath, "invalid json");
    Check(ClientSettings.Load(savedPath).Address == "127.0.0.1:45831", "corrupt preferences recover to defaults");
}

async Task Session()
{
    await using var site = await OpenHost();
    var virtualBounds = new System.Drawing.Rectangle(-1920, -1080, 3840, 2160);
    Check(Desktop.MapPoint(virtualBounds, virtualBounds, 0, 0) == (0, 0) && Desktop.MapPoint(virtualBounds, virtualBounds, 1, 1) == (65535, 65535), "whole-desktop corners with negative origin");
    var secondary = new System.Drawing.Rectangle(-1920, -1080, 1920, 1080);
    Check(Desktop.MapPoint(secondary, virtualBounds, 0, 0) == (0, 0) && Desktop.MapPoint(secondary, virtualBounds, 1, 1).X < 32768, "secondary monitor maps into correct half of desktop");
    Check(Desktop.MapPoint(virtualBounds, virtualBounds, -10, 20) == (0, 65535), "out-of-view dragging clamps to desktop bounds");
    var cursor = CursorPlacement.DrawPoint(100, 80, new System.Drawing.Rectangle(10, 20, 200, 100), 3, 4);
    Check(cursor.X == 87 && cursor.Y == 56, "captured cursor keeps its hotspot");
    try { Desktop.MapPoint(virtualBounds, virtualBounds, double.NaN, 0); Check(false, "invalid coordinates"); } catch (ArgumentException) { Check(true, "nonfinite coordinates rejected"); }
    using (var response = await site.Http.GetAsync("frame")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "screen requires authentication");
    using (var response = await site.Http.GetAsync("displays")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "monitor list requires authentication");
    var opaqueNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    using (var response = await site.Http.PostAsync("pake/start", Json("{\"loginRequest\":\"not-enrolled\",\"nonce\":\"" + opaqueNonce + "\"}"))) Check(response.StatusCode == HttpStatusCode.Conflict, "unregistered host rejects key exchange before the body matters");
    using (var response = await site.Http.PostAsync("pake/finish", Json("{\"exchangeId\":\"missing\",\"finishLoginRequest\":\"missing\",\"nonce\":\"" + opaqueNonce + "\"}"))) Check(response.StatusCode == HttpStatusCode.Unauthorized, "unknown key exchange cannot finish");
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = "wrong" })) Check(response.StatusCode == HttpStatusCode.Unauthorized, "wrong password rejected");
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password })) Check((int)response.StatusCode == 429, "a failed source is temporarily throttled");
    await Task.Delay(2100);
    var hostNotices = new List<string>();
    site.Host.Status += hostNotices.Add;
    string sessionToken;
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password }))
    {
        Check(response.IsSuccessStatusCode, "password login without host approval");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(result.GetProperty("protocol").GetInt32() == 1 && result.GetProperty("maxFileBytes").GetInt64() == 1073741824, "login returns protocol version and file limit");
        sessionToken = result.GetProperty("token").GetString()!;
        site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
    }
    Check(hostNotices.Any(text => text is "클라이언트 연결됨 · 127.0.0.1" or "클라이언트 연결됨 · ::1"), "host shows who connected");
    Check(site.Host.SessionPeer is "127.0.0.1" or "::1", "host remembers who connected");
    var connectedAt = site.Host.SessionStartedUtc;
    Check(connectedAt != null && connectedAt.Value <= DateTime.UtcNow.AddSeconds(2) && DateTime.UtcNow - connectedAt.Value < TimeSpan.FromSeconds(15), "host remembers when the client connected");
    using (var response = await site.Http.GetAsync("ping"))
    {
        var ping = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(response.IsSuccessStatusCode && ping.GetProperty("protocol").GetInt32() == 1, "ping returns protocol version");
    }
    site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", sessionToken);
    using (var response = await site.Http.GetAsync("ping")) Check(response.IsSuccessStatusCode, "bearer scheme is parsed case-insensitively");
    site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "short");
    using (var response = await site.Http.GetAsync("ping")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "a bearer token with a different length is rejected");
    site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
    var bearerMethod = typeof(RemoteHost).GetMethod("BearerTokenMatches", BindingFlags.Static | BindingFlags.NonPublic)!;
    var bearerMatches = bearerMethod.CreateDelegate<Func<string?, string?, bool>>();
    var wrongSameLengthToken = (sessionToken[0] == '0' ? "1" : "0") + sessionToken[1..];
    Check(bearerMatches("Bearer " + sessionToken, sessionToken) && bearerMatches("bearer " + sessionToken, sessionToken)
        && !bearerMatches("Bearer " + wrongSameLengthToken, sessionToken) && !bearerMatches("Basic " + sessionToken, sessionToken), "bearer authorization compares equal-length token bytes and accepts only bearer schemes");
    for (var warmup = 0; warmup < 5000; warmup++)
    {
        _ = bearerMatches("Bearer " + sessionToken, sessionToken);
        _ = bearerMatches("Bearer " + wrongSameLengthToken, sessionToken);
    }
    var validSamples = new double[3];
    var invalidSamples = new double[3];
    const int bearerComparisonsPerSample = 40000;
    for (var sample = 0; sample < validSamples.Length; sample++)
    {
        var validMatches = 0;
        var watch = Stopwatch.StartNew();
        for (var comparison = 0; comparison < bearerComparisonsPerSample; comparison++)
            if (bearerMatches("Bearer " + sessionToken, sessionToken)) validMatches++;
        watch.Stop();
        validSamples[sample] = watch.Elapsed.TotalMilliseconds;
        var invalidMatches = 0;
        watch.Restart();
        for (var comparison = 0; comparison < bearerComparisonsPerSample; comparison++)
            if (bearerMatches("Bearer " + wrongSameLengthToken, sessionToken)) invalidMatches++;
        watch.Stop();
        invalidSamples[sample] = watch.Elapsed.TotalMilliseconds;
        Check(validMatches == bearerComparisonsPerSample && invalidMatches == 0, "bearer timing sample returns the expected match result");
    }
    Array.Sort(validSamples);
    Array.Sort(invalidSamples);
    var timingRatio = Math.Max(validSamples[1], invalidSamples[1]) / Math.Max(0.001, Math.Min(validSamples[1], invalidSamples[1]));
    Console.WriteLine($"Bearer comparison median sample: valid={validSamples[1]:0.00}ms invalid={invalidSamples[1]:0.00}ms ratio={timingRatio:0.00}x");
    Check(timingRatio < 3.0, "same-length bearer match and mismatch timing stays within the coarse comparison bound");
    using (var response = await site.Http.GetAsync("displays"))
    {
        var displays = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        var primary = displays.Single(item => item.GetProperty("primary").GetBoolean());
        var desktop = displays.Single(item => item.GetProperty("id").GetString() == "all");
        Check(response.IsSuccessStatusCode && primary.GetProperty("name").GetString() is { Length: > 0 } && primary.GetProperty("width").GetInt32() > 0 && primary.GetProperty("height").GetInt32() > 0 && primary.GetProperty("left").ValueKind == JsonValueKind.Number && primary.GetProperty("top").ValueKind == JsonValueKind.Number && desktop.GetProperty("name").GetString() is { Length: > 0 }, "display list uses protocol fields");
    }
    using (var response = await site.Http.GetAsync("files")) Check(response.StatusCode == HttpStatusCode.NotFound, "legacy shared-folder API removed");
    using (var response = await site.Http.GetAsync("health")) Check(response.StatusCode == HttpStatusCode.NotFound, "unused health probe removed");
    using (var response = await site.Http.PostAsync("input", Json("{\"kind\":\"key\",\"code\":9999,\"down\":true}"))) Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid keyboard event rejected");
    using (var response = await site.Http.PostAsync("input", Json("{\"kind\":\"button\",\"code\":9,\"down\":true}"))) Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid mouse button rejected");
    using (var response = await site.Http.GetAsync("frame?display=missing-monitor")) Check(response.StatusCode == HttpStatusCode.BadRequest, "missing monitor rejected before capture");
    using (var response = await site.Http.PostAsync("input", Json("{\"kind\":\"move\",\"display\":\"missing-monitor\",\"x\":0.5,\"y\":0.5}"))) Check(response.StatusCode == HttpStatusCode.BadRequest, "missing monitor rejected before pointer injection");
    using (var response = await site.Http.PostAsync("input", Json("{\"kind\":\"text\",\"text\":\"" + new string('x', 4097) + "\"}"))) Check(response.StatusCode == HttpStatusCode.BadRequest, "input text over 4096 characters rejected");
    var authChecksBeforeInput = site.Host.AuthorizationCheckCount;
    using (var response = await site.Http.PostAsync("input", Json("{\"kind\":\"release\"}"))) Check(response.StatusCode == HttpStatusCode.OK, "release with nothing held is accepted");
    Check(site.Host.AuthorizationCheckCount - authChecksBeforeInput == 1, "the input route reuses the middleware authentication result");
    var inputMetrics = site.Host.InputMetrics;
    Check(inputMetrics.Requests > 0 && inputMetrics.AuthGateAverageMs < 100 && inputMetrics.NoticeGateAverageMs < 100, "input auth and notice gate timing are measured separately from apply");
    using (var body = new StringContent(new string('x', 40000)))
    using (var response = await site.Http.PostAsync("input", body)) Check(response.StatusCode == HttpStatusCode.BadRequest, "oversized control message rejected");
    await Task.Delay(2100);
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password })) Check(response.StatusCode == HttpStatusCode.Conflict, "only one active session");
    await Task.Delay(5200);
    string? replacedToken;
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password }))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "a quiet session can be replaced by a new login");
        replacedToken = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
    }
    using (var response = await site.Http.GetAsync("ping")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "the replaced session token stops working");
    site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replacedToken);
    using (var response = await site.Http.GetAsync("ping")) Check(response.IsSuccessStatusCode, "the replacement login can use the host");
    site.Host.Disconnect();
    var left = hostNotices[^1];
    Check(left == UiText.SessionEnded("127.0.0.1", UiText.SessionEndKind.Left, site.Port) || left == UiText.SessionEnded("::1", UiText.SessionEndKind.Left, site.Port), "a departed client returns the host to listening and says who left");
    Check(site.Host.SessionPeer == null, "host forgets the client after it leaves");
    Check(site.Host.SessionStartedUtc == null, "host forgets the connection time after the client leaves");
    using (var response = await site.Http.GetAsync("ping")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "host disconnect revokes token");
    using var wrongPinClient = new RemoteClient("localhost:" + site.Port, new string('0', 64));
    try { await wrongPinClient.Login(site.Password); Check(false, "wrong certificate pin"); } catch (HttpRequestException) { Check(true, "wrong certificate pin rejected"); }
    await Task.Delay(2100);
    using var client = new RemoteClient("localhost:" + site.Port, site.Pin);
    await client.Login(site.Password);
    Console.WriteLine("Waiting for abandoned-session expiry...");
    await Task.Delay(17000);
    try { await client.Ping(CancellationToken.None); Check(false, "session timeout"); }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { Check(true, "abandoned session expires"); }
}

async Task SessionRevocationRace()
{
    await using var site = await OpenHost();
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password }))
    {
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    var captureGate = (SemaphoreSlim)typeof(RemoteHost).GetField("captureGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(site.Host)!;
    await captureGate.WaitAsync();
    var released = false;
    try
    {
        var previousSeen = site.Host.SessionLastSeenUtc!.Value;
        await Task.Delay(5);
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var frameRequest = site.Http.GetAsync("frame", HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
        var authorizedBy = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (site.Host.SessionLastSeenUtc <= previousSeen && DateTime.UtcNow < authorizedBy) await Task.Delay(5);
        Check(site.Host.SessionLastSeenUtc > previousSeen, "queued frame request is authorized before revocation");
        var disconnect = Task.Run(() => site.Host.Disconnect());
        var revokedBy = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (site.Host.SessionPeer != null && DateTime.UtcNow < revokedBy) await Task.Delay(5);
        Check(site.Host.SessionPeer == null, "frame test session is revoked while request waits");
        Check(await Task.WhenAny(disconnect, Task.Delay(300)) == disconnect, "disconnect does not wait for the capture gate");
        captureGate.Release();
        released = true;
        try
        {
            using var response = await frameRequest;
            var body = await response.Content.ReadAsByteArrayAsync();
            Check(response.StatusCode != HttpStatusCode.OK || body.Length == 0, "queued frame is canceled on session revocation");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Check(true, "queued frame is canceled on session revocation");
        }
        await disconnect;
        using (var login = await site.Http.PostAsJsonAsync("login", new { password = site.Password }))
        {
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
            site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using (var logout = await site.Http.PostAsync("logout", new StringContent("")))
            Check(logout.StatusCode == HttpStatusCode.OK && site.Host.SessionPeer == null, "explicit logout returns successfully while revoking its session");
    }
    finally
    {
        if (!released) captureGate.Release();
    }
}

async Task ClipboardFiles()
{
    await using var site = await OpenHost();
    using (var response = await site.Http.GetAsync("clipboard")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "file clipboard requires authentication");
    using (var response = await site.Http.PutAsJsonAsync("clipboard/text", new ClipboardTextUpdate("test", "", "unauthorized"))) Check(response.StatusCode == HttpStatusCode.Unauthorized, "text clipboard requires authentication");
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password }))
    {
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.GetProperty("token").GetString());
    }
    var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
    var clipJson = (await site.Http.GetFromJsonAsync<JsonElement>("clipboard"))!;
    Check(clipJson.GetProperty("id").GetString() is { Length: > 0 } && clipJson.GetProperty("origin").ValueKind == JsonValueKind.String && clipJson.GetProperty("files").ValueKind == JsonValueKind.Array && clipJson.GetProperty("text").ValueKind == JsonValueKind.String, "clipboard manifest uses protocol fields");
    using (var response = await site.Http.PutAsync("clipboard/text", Json("{\"origin\":\"protocol-check\",\"expectedRevision\":\"stale-revision\",\"text\":\"must-not-apply\"}"))) Check(response.StatusCode == HttpStatusCode.Conflict, "stale text revision is rejected before clipboard changes");
    var clipBefore = (await site.Http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!;
    Check(clipBefore.Id == clipJson.GetProperty("id").GetString() && clipBefore.Text == clipJson.GetProperty("text").GetString(), "rejected text update leaves clipboard unchanged");
    var stagedFiles = new[] { new ClipboardFile("한글 복사.bin", payload.Length) };
    async Task<string> BeginClipboard(ClipboardFile[] files, string revision)
    {
        var filesJson = string.Join(',', files.Select(file => "{\"name\":" + JsonSerializer.Serialize(file.Name) + ",\"size\":" + file.Size + "}"));
        using var response = await site.Http.PostAsync("clipboard/uploads", Json("{\"origin\":\"test-client\",\"expectedRevision\":" + JsonSerializer.Serialize(revision) + ",\"files\":[" + filesJson + "]}")); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
    async Task<HttpResponseMessage> SendClipboardBytes(string id, byte[] bytes, string hash)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "clipboard/uploads/" + id + "/0") { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("X-Content-SHA256", hash); return await site.Http.SendAsync(request);
    }
    async Task<HttpResponseMessage> SendClipboardRange(string id, byte[] bytes, int offset, int count)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "clipboard/uploads/" + id + "/0") { Content = new ByteArrayContent(bytes, offset, count) };
        request.Headers.Add("X-Content-SHA256", Convert.ToHexString(SHA256.HashData(bytes)));
        request.Headers.Add("X-Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await site.Http.SendAsync(request);
    }
    var idleBatch = await BeginClipboard([new ClipboardFile("idle.bin", 4)], clipBefore.Id);
    Check(await site.Host.ExpireIdleUploads(TimeSpan.FromMinutes(2)) == 0, "a fresh clipboard upload is not expired");
    using (var alive = await site.Http.GetAsync("clipboard/uploads/" + idleBatch + "/0")) Check(alive.IsSuccessStatusCode, "a fresh clipboard upload can still report progress");
    Check(await site.Host.ExpireIdleUploads(TimeSpan.Zero) == 1 && !Directory.Exists(Path.Combine(site.ClipboardCache, idleBatch)), "an idle clipboard upload expires");
    using (var dead = await site.Http.GetAsync("clipboard/uploads/" + idleBatch + "/0")) Check(dead.StatusCode == HttpStatusCode.Conflict && (await dead.Content.ReadAsStringAsync()).Contains("만료"), "an expired clipboard upload asks for a new copy");
    var clipBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    using (var response = await SendClipboardBytes(clipBatch, payload, new string('0', 64))) Check(response.StatusCode == HttpStatusCode.BadRequest, "clipboard rejects corrupt file hash");
    using (var response = await site.Http.PostAsync("clipboard/uploads/" + clipBatch + "/commit", null)) Check(response.StatusCode == HttpStatusCode.Conflict, "partial clipboard batch cannot publish");
    Check((await site.Clipboard.Read(CancellationToken.None)).Paths.Length == 0, "failed clipboard upload leaves clipboard untouched");
    using (var response = await SendClipboardBytes(clipBatch, payload, Convert.ToHexString(SHA256.HashData(payload)))) Check(response.IsSuccessStatusCode, "clipboard upload verifies bytes");
    JsonElement committedClip;
    using (var response = await site.Http.PostAsync("clipboard/uploads/" + clipBatch + "/commit", null))
    {
        Check(response.IsSuccessStatusCode, "complete clipboard batch publishes atomically");
        committedClip = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }
    var committedFile = committedClip.GetProperty("files").EnumerateArray().Single();
    Check(committedClip.GetProperty("id").GetString() is { Length: > 0 } && committedClip.GetProperty("origin").ValueKind == JsonValueKind.String && committedFile.GetProperty("name").GetString() == "한글 복사.bin" && committedFile.GetProperty("size").GetInt64() == payload.Length && committedClip.GetProperty("text").GetString() == "", "committed clipboard manifest uses protocol file fields");
    var committedPaths = (await site.Clipboard.Read(CancellationToken.None)).Paths;
    Check(committedPaths.Length == 1 && File.ReadAllBytes(committedPaths[0]).SequenceEqual(payload), "clipboard points to received file, not a remote path");
    Check(Path.GetFileName(committedPaths[0]) == "한글 복사.bin", "clipboard preserves Unicode filename");
    clipBefore = (await site.Http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!;
    var cancelledBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    using (var response = await site.Http.DeleteAsync("clipboard/uploads/" + cancelledBatch)) Check(response.IsSuccessStatusCode && !Directory.Exists(Path.Combine(site.ClipboardCache, cancelledBatch)), "cancel cleans staged clipboard files");
    var resumeBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    var half = payload.Length / 2;
    using (var response = await SendClipboardRange(resumeBatch, payload, 0, half)) Check(response.StatusCode == HttpStatusCode.Conflict && response.Headers.Contains("X-Received-Bytes"), "a short clipboard upload stays resumable");
    var probed = (await site.Http.GetFromJsonAsync<JsonElement>("clipboard/uploads/" + resumeBatch + "/0"))!;
    Check(probed.GetProperty("received").GetInt64() == half, "clipboard upload reports received bytes");
    using (var response = await SendClipboardRange(resumeBatch, payload, half, payload.Length - half)) Check(response.IsSuccessStatusCode, "clipboard upload resumes after the received bytes");
    string resumeId;
    using (var response = await site.Http.PostAsync("clipboard/uploads/" + resumeBatch + "/commit", null))
    {
        Check(response.IsSuccessStatusCode, "a resumed clipboard upload can publish");
        resumeId = (await response.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetString()!;
    }
    Check(File.ReadAllBytes((await site.Clipboard.Read(CancellationToken.None)).Paths.Single()).SequenceEqual(payload), "a resumed clipboard upload keeps every byte");
    using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "clipboard/" + Uri.EscapeDataString(resumeId) + "/0"))
    {
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, null);
        using var rangeResponse = await site.Http.SendAsync(rangeRequest);
        var suffix = await rangeResponse.Content.ReadAsByteArrayAsync();
        Check(rangeResponse.StatusCode == HttpStatusCode.PartialContent && suffix.AsSpan().SequenceEqual(payload.AsSpan(100)) && rangeResponse.Headers.GetValues("X-Content-SHA256").Single().Equals(Convert.ToHexString(SHA256.HashData(payload)), StringComparison.OrdinalIgnoreCase), "a resumed clipboard download returns the remaining bytes and the full hash");
    }
    clipBefore = (await site.Http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!;
    using (var response = await site.Http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test", clipBefore.Id, [new("../escape.bin", 1)]))) Check(response.StatusCode == HttpStatusCode.BadRequest, "clipboard filename traversal rejected");
    using (var response = await site.Http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test", clipBefore.Id, [new("oversize.bin", RemoteHost.MaxFileBytes + 1)]))) Check(response.StatusCode == HttpStatusCode.BadRequest && (await response.Content.ReadAsStringAsync()).Contains("1GiB"), "clipboard file size limit enforced before upload");
    var staleBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    using (var response = await SendClipboardBytes(staleBatch, payload, Convert.ToHexString(SHA256.HashData(payload)))) response.EnsureSuccessStatusCode();
    site.Clipboard.Set([]);
    using (var response = await site.Http.PostAsync("clipboard/uploads/" + staleBatch + "/commit", null)) Check(response.StatusCode == HttpStatusCode.Conflict && (await response.Content.ReadAsStringAsync()).Contains("다시 복사하세요"), "new host copy is not overwritten by an older transfer");
    using (var response = await site.Http.DeleteAsync("clipboard/uploads/" + staleBatch)) response.EnsureSuccessStatusCode();
    var emptyRevision = (await site.Http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!;
    using (var response = await site.Http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test-client", emptyRevision.Id, [new ClipboardFile("empty", 0, true)])))
    {
        Check(response.IsSuccessStatusCode, "an empty folder upload can start");
        var emptyId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var committed = await site.Http.PostAsync("clipboard/uploads/" + emptyId + "/commit", null);
        Check(committed.IsSuccessStatusCode, "an empty folder publishes without a file body");
    }
    var emptyPaths = (await site.Clipboard.Read(CancellationToken.None)).Paths;
    Check(emptyPaths.Length == 1 && Directory.Exists(emptyPaths[0]) && !Directory.EnumerateFileSystemEntries(emptyPaths[0]).Any(), "the host clipboard receives an empty folder");
    var pictureRevision = (await site.Http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!;
    using (var badPicture = new HttpRequestMessage(HttpMethod.Put, "clipboard/image") { Content = new ByteArrayContent(picturePng) })
    {
        badPicture.Headers.Add("X-Content-SHA256", new string('0', 64));
        badPicture.Headers.Add("X-Clipboard-Origin", "test-client");
        badPicture.Headers.Add("X-Clipboard-Revision", pictureRevision.Id);
        using var badResponse = await site.Http.SendAsync(badPicture);
        Check(badResponse.StatusCode == HttpStatusCode.BadRequest, "clipboard picture rejects a bad hash");
    }
    Check((await site.Http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!.Id == pictureRevision.Id, "a bad picture hash leaves the clipboard unchanged");
    site.Host.Disconnect();
    await Task.Delay(2100);
    using var client = new RemoteClient("localhost:" + site.Port, site.Pin);
    await client.Login(site.Password);
    var source = Path.Combine(site.Root, "client.bin"); await File.WriteAllBytesAsync(source, payload);
    var localClipboard = new TestFileClipboard();
    var localClipboardCache = Path.Combine(site.Root, "clipboard-client");
    var sync = new ClipboardSync(client, localClipboard, _ => { }, localClipboardCache);
    await sync.Initialize(CancellationToken.None);
    localClipboard.Set([source]);
    Check(await sync.Push(CancellationToken.None), "local file copy pushes to remote clipboard");
    var pushed = await site.Clipboard.Read(CancellationToken.None);
    Check(pushed.Paths.Length == 1 && File.ReadAllBytes(pushed.Paths[0]).SequenceEqual(payload), "local-to-remote clipboard roundtrip");
    var localSequence = (await localClipboard.Read(CancellationToken.None)).Sequence;
    await sync.Pull(CancellationToken.None);
    Check((await localClipboard.Read(CancellationToken.None)).Sequence == localSequence, "clipboard origin suppresses echo loop");
    var copyA = Path.Combine(site.Root, "copy-a"); var copyB = Path.Combine(site.Root, "copy-b"); Directory.CreateDirectory(copyA); Directory.CreateDirectory(copyB);
    var sameA = Path.Combine(copyA, "같은 이름.txt"); var sameB = Path.Combine(copyB, "같은 이름.txt");
    File.WriteAllText(sameA, "첫 번째"); File.WriteAllText(sameB, "두 번째");
    site.Clipboard.Set([sameA, sameB]);
    var remoteManifest = await client.Clipboard(CancellationToken.None);
    Check(remoteManifest.Files.Length == 2 && !JsonSerializer.Serialize(remoteManifest).Contains(site.Root), "clipboard manifest hides source paths");
    await sync.Pull(CancellationToken.None);
    var receivedClipboard = await localClipboard.Read(CancellationToken.None);
    Check(receivedClipboard.Paths.Length == 2 && File.ReadAllText(receivedClipboard.Paths[0]) == "첫 번째" && File.ReadAllText(receivedClipboard.Paths[1]) == "두 번째", "remote-to-local clipboard preserves duplicate basenames");
    Check(!await sync.Push(CancellationToken.None), "received clipboard is not re-uploaded");
    var executableSource = Path.Combine(site.Root, "remote-tool.exe");
    var executableBytes = payload.ToArray(); executableBytes[0] = (byte)'M'; executableBytes[1] = (byte)'Z';
    await File.WriteAllBytesAsync(executableSource, executableBytes);
    localClipboard.Set([executableSource]);
    Check(await sync.Push(CancellationToken.None), "executable extension can enter clipboard transfer");
    var receivedExecutable = await site.Clipboard.Read(CancellationToken.None);
    Check(Path.GetFileName(receivedExecutable.Paths.Single()) == "remote-tool.exe" && File.ReadAllBytes(receivedExecutable.Paths.Single()).SequenceEqual(executableBytes), "executable name and bytes reach remote clipboard unchanged");
    site.Clipboard.Set([source]);
    localClipboard.BeforeWrite = () => localClipboard.Set([sameA]);
    await sync.Pull(CancellationToken.None);
    Check((await localClipboard.Read(CancellationToken.None)).Paths.SequenceEqual([sameA]), "local copy during download wins over stale remote clipboard");
    try { await client.DownloadClipboard(remoteManifest.Id, 0, Path.Combine(site.Root, "stale.bin"), CancellationToken.None); Check(false, "stale clipboard"); }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { Check(e.Message.Contains("다시 복사하세요"), "old clipboard snapshot cannot read new files"); }
    localClipboard.Set([], "클라이언트에서 복사한 한글 텍스트\r\n두 번째 줄");
    Check(await sync.PushText(CancellationToken.None), "local text copy pushes to remote clipboard");
    var hostText = await site.Clipboard.Read(CancellationToken.None);
    Check(hostText.Paths.Length == 0 && hostText.Text == "클라이언트에서 복사한 한글 텍스트\r\n두 번째 줄", "local-to-remote Unicode text clipboard roundtrip");
    var textSequence = hostText.Sequence;
    await sync.Pull(CancellationToken.None);
    Check((await site.Clipboard.Read(CancellationToken.None)).Sequence == textSequence, "text clipboard origin suppresses echo loop");
    site.Clipboard.Set([], "서버에서 복사한 text ✓");
    await sync.Pull(CancellationToken.None);
    var receivedText = await localClipboard.Read(CancellationToken.None);
    Check(receivedText.Paths.Length == 0 && receivedText.Text == "서버에서 복사한 text ✓", "remote-to-local text clipboard roundtrip");
    Check(!await sync.PushText(CancellationToken.None), "received text is not re-uploaded");
    site.Clipboard.Set([], "원격 텍스트");
    localClipboard.BeforeWrite = () => localClipboard.Set([], "새 로컬 텍스트");
    await sync.Pull(CancellationToken.None);
    Check((await localClipboard.Read(CancellationToken.None)).Text == "새 로컬 텍스트", "local text copy wins over stale remote text");
    var textManifest = await client.Clipboard(CancellationToken.None);
    try { await client.SendClipboardText("오래된 텍스트", "test", remoteManifest.Id, CancellationToken.None); Check(false, "stale text revision"); }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { Check(true, "stale text revision cannot overwrite newer clipboard"); }
    try { await client.SendClipboardText(new string('x', ClipboardPaths.MaxTextCharacters + 1), "test", textManifest.Id, CancellationToken.None); Check(false, "oversized text"); }
    catch (ArgumentException) { Check(true, "text clipboard size limit enforced before upload"); }
    var longText = new string('가', 40_000);
    await client.SendClipboardText(longText, "test", (await client.Clipboard(CancellationToken.None)).Id, CancellationToken.None);
    Check((await site.Clipboard.Read(CancellationToken.None)).Text == longText, "text beyond control-message limit reaches clipboard");
    var html = "<p>안녕 <b>서식</b></p>";
    await client.SendClipboardText("안녕 서식", "test", (await client.Clipboard(CancellationToken.None)).Id, CancellationToken.None, html);
    var formattedHost = await site.Clipboard.Read(CancellationToken.None);
    Check(formattedHost.Text == "안녕 서식" && formattedHost.Html == html, "formatted text reaches the host clipboard");
    var formattedManifest = await client.Clipboard(CancellationToken.None);
    Check(formattedManifest.Html == html.Length && formattedManifest.Text == "안녕 서식" && formattedManifest.Image == 0 && formattedManifest.Files.Length == 0, "the clipboard manifest reports formatted text");
    Check(await client.DownloadClipboardHtml(formattedManifest.Id, CancellationToken.None) == html, "formatted text can be downloaded");
    localClipboard.Set([], "로컬 서식", "", null, "<i>로컬 서식</i>");
    Check(await sync.PushText(CancellationToken.None), "local formatted text pushes to the remote clipboard");
    var pushedFormat = await site.Clipboard.Read(CancellationToken.None);
    Check(pushedFormat.Text == "로컬 서식" && pushedFormat.Html == "<i>로컬 서식</i>", "local formatted text keeps its html");
    await sync.Pull(CancellationToken.None);
    Check((await site.Clipboard.Read(CancellationToken.None)).Sequence == pushedFormat.Sequence, "formatted text origin suppresses echo");
    site.Clipboard.Set([], "원격 서식", "", null, "<b>원격 서식</b>");
    await sync.Pull(CancellationToken.None);
    var receivedFormat = await localClipboard.Read(CancellationToken.None);
    Check(receivedFormat.Text == "원격 서식" && receivedFormat.Html == "<b>원격 서식</b>", "remote formatted text reaches the local clipboard");
    Check(!await sync.PushText(CancellationToken.None), "received formatted text is not re-uploaded");
    try { await client.SendClipboardText("a", "test", formattedManifest.Id, CancellationToken.None, new string('h', ClipboardPaths.MaxHtmlCharacters + 1)); Check(false, "oversized html"); }
    catch (ArgumentException) { Check(true, "html clipboard size limit enforced before upload"); }
    await client.SendClipboardText("", "test", (await client.Clipboard(CancellationToken.None)).Id, CancellationToken.None, "<p>태그 없는 글</p>");
    var derived = await site.Clipboard.Read(CancellationToken.None);
    Check(derived.Text == "태그 없는 글" && derived.Html == "<p>태그 없는 글</p>", "html without text keeps a plain copy");
    localClipboard.Set([]);
    Check(await sync.PushText(CancellationToken.None), "an empty local clipboard clears the remote clipboard");
    var clearedHost = await site.Clipboard.Read(CancellationToken.None);
    Check(clearedHost.Paths.Length == 0 && clearedHost.Text.Length == 0 && clearedHost.Html.Length == 0 && (clearedHost.Image?.Length ?? 0) == 0, "the remote clipboard is empty after a local clear");
    site.Clipboard.Set([], "다시 채운 텍스트");
    await sync.Pull(CancellationToken.None);
    site.Clipboard.Set([]);
    await sync.Pull(CancellationToken.None);
    var clearedLocal = await localClipboard.Read(CancellationToken.None);
    Check(clearedLocal.Paths.Length == 0 && clearedLocal.Text.Length == 0 && clearedLocal.Html.Length == 0 && (clearedLocal.Image?.Length ?? 0) == 0, "an empty remote clipboard clears the local clipboard");
    localClipboard.Set([], "", "", picturePng);
    Check(await sync.PushImage(CancellationToken.None), "local picture copy pushes to remote clipboard");
    var hostPicture = await site.Clipboard.Read(CancellationToken.None);
    Check(hostPicture.Paths.Length == 0 && hostPicture.Text.Length == 0 && hostPicture.Image != null && hostPicture.Image.SequenceEqual(picturePng), "local-to-remote picture clipboard roundtrip");
    var pictureSequence = hostPicture.Sequence;
    await sync.Pull(CancellationToken.None);
    Check((await site.Clipboard.Read(CancellationToken.None)).Sequence == pictureSequence, "picture clipboard origin suppresses echo");
    site.Clipboard.Set([], "", "", picturePng);
    await sync.Pull(CancellationToken.None);
    var receivedPicture = await localClipboard.Read(CancellationToken.None);
    Check(receivedPicture.Image != null && receivedPicture.Image.SequenceEqual(picturePng) && receivedPicture.Text.Length == 0 && receivedPicture.Paths.Length == 0, "remote-to-local picture clipboard roundtrip");
    Check(!await sync.PushImage(CancellationToken.None), "received picture is not re-uploaded");
    var pictureManifest = await client.Clipboard(CancellationToken.None);
    Check(pictureManifest.Image == picturePng.Length && pictureManifest.Files.Length == 0 && pictureManifest.Text.Length == 0, "picture clipboard manifest reports only the picture");
    try { await client.SendClipboardImage(picturePng, "test", remoteManifest.Id, CancellationToken.None); Check(false, "stale picture"); }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { Check(true, "stale picture revision cannot overwrite newer clipboard"); }
}

async Task Capture()
{
    await using var site = await OpenHost();
    using var client = new RemoteClient("localhost:" + site.Port, site.Pin);
    await client.Login(site.Password);
    var jpeg = await client.Frame(CancellationToken.None);
    Check(jpeg.Length > 100 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "desktop capture returns JPEG");
    var lowQuality = await client.Frame(CancellationToken.None, quality: 30);
    var highQuality = await client.Frame(CancellationToken.None, quality: 75);
    using (var lowDecoded = System.Drawing.Image.FromStream(new MemoryStream(lowQuality)))
    using (var highDecoded = System.Drawing.Image.FromStream(new MemoryStream(highQuality)))
        Check(lowDecoded.Width == highDecoded.Width && lowDecoded.Height == highDecoded.Height && lowQuality[0] == 0xFF && highQuality[0] == 0xFF, "quality presets return valid JPEG frames");
    var displays = await client.Displays() ?? [];
    Check(displays.Any(d => d.Id == "all") && displays.Any(d => d.Primary), "monitor API includes virtual desktop and primary display");
    foreach (var display in displays)
    {
        var frame = await client.Frame(CancellationToken.None, display.Id);
        using var decoded = System.Drawing.Image.FromStream(new MemoryStream(frame));
        Check(decoded.Width == display.Width && decoded.Height == display.Height, "capture dimensions: " + display.Name + " (" + display.Width + "x" + display.Height + ")");
    }
    var virtualDisplay = displays.Single(display => display.Id == "all");
    var expectedViewport = Desktop.Viewport(new System.Drawing.Rectangle(virtualDisplay.Left, virtualDisplay.Top, virtualDisplay.Width, virtualDisplay.Height), .25, .25, .5, .5);
    var cropped = await client.Frame(CancellationToken.None, "all", 30, .25, .25, .5, .5);
    using var croppedDecoded = System.Drawing.Image.FromStream(new MemoryStream(cropped));
    Check(croppedDecoded.Width == expectedViewport.Width && croppedDecoded.Height == expectedViewport.Height && croppedDecoded.Width < virtualDisplay.Width && croppedDecoded.Height < virtualDisplay.Height, "viewport capture reduces requested frame area");
    Desktop.SmoothCapture = true;
    var smoothDesktop = new Desktop();
    var smoothFrame = smoothDesktop.CaptureDetailed(null, 55, 0, 0, 1, 1, 30, true);
    Check(smoothFrame.Bytes.Length > 100 && smoothFrame.Bytes[0] == 0xFF && smoothFrame.Bytes[1] == 0xD8, "smooth capture returns JPEG");
    var repeatedFrame = smoothDesktop.CaptureDetailed(null, 55, 0, 0, 1, 1, 30, true);
    Check(repeatedFrame.Bytes[0] == 0xFF && repeatedFrame.Bytes[1] == 0xD8 && repeatedFrame.LocalCursor, "smooth capture serves a prepared frame");
    var h264Frame = await client.FrameDetailed(CancellationToken.None, null, 55, 15, true, false, null, true);
    Check(h264Frame.Codec == "h264" && h264Frame.Bytes.Length > 8 && VideoCodec.IsAnnexB(h264Frame.Bytes), "a windows frame request receives h264");
    Check(h264Frame.WaitMilliseconds >= 0 && h264Frame.WaitMilliseconds < 10000, "a frame reports how long the host waited");
    using var h264 = new H264Decoder();
    var opened = h264.TryDecode(h264Frame.Bytes, out var h264Pixels, out var h264Width, out var h264Height);
    if (!opened)
    {
        var follow = await client.FrameDetailed(CancellationToken.None, null, 55, 15, true, true, null, false);
        if (follow.Bytes.Length > 0) opened = h264.TryDecode(follow.Bytes, out h264Pixels, out h264Width, out h264Height);
    }
    Check(opened && h264Width > 1 && h264Height > 1 && h264Pixels.Length == h264Width * h264Height * 4, "the h264 desktop frame decodes");
    var jpegFallback = await client.FrameDetailed(CancellationToken.None, null, 55, 15, true, false, null, false, 0, 0, 1, 1, false);
    Check(jpegFallback.Codec == "jpeg" && jpegFallback.Bytes.Length > 2 && jpegFallback.Bytes[0] == 0xFF && jpegFallback.Bytes[1] == 0xD8, "a client can explicitly leave H.264 and receive JPEG from the same host");
    var plain = await client.Frame(CancellationToken.None);
    Check(plain.Length > 100 && plain[0] == 0xFF && plain[1] == 0xD8, "a client that omits the codec still receives jpeg");
}

async Task<RunningHost> OpenHost()
{
    var root = Path.Combine(Path.GetTempPath(), "rdc-check-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    var settings = new HostSettings();
    var salt = RandomNumberGenerator.GetBytes(32);
    const string password = "integration-password-123";
    settings.Salt = Convert.ToBase64String(salt);
    settings.PasswordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, HostSettings.Iterations, HashAlgorithmName.SHA256, 32));
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=integration", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    var pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c != null && Convert.ToHexString(SHA256.HashData(c.RawData)) == pin, AllowAutoRedirect = false, ClientCertificateOptions = ClientCertificateOption.Manual, UseProxy = false }) { BaseAddress = new Uri("https://localhost:" + port + "/v1/") };
    var clipboard = new TestFileClipboard();
    var cache = Path.Combine(root, "clipboard-host");
    var host = new RemoteHost(settings, clipboard, cache);
    try { await host.StartAsync(port, certificate); }
    catch
    {
        http.Dispose();
        await host.DisposeAsync();
        certificate.Dispose();
        Directory.Delete(root, true);
        throw;
    }
    return new RunningHost(root, password, settings, certificate, pin, port, http, clipboard, cache, host);
}

sealed class RunningHost(string root, string password, HostSettings settings, X509Certificate2 certificate, string pin, int port, HttpClient http, TestFileClipboard clipboard, string clipboardCache, RemoteHost host) : IAsyncDisposable
{
    public string Root { get; } = root;
    public string Password { get; } = password;
    public HostSettings Settings { get; } = settings;
    public X509Certificate2 Certificate { get; } = certificate;
    public string Pin { get; } = pin;
    public int Port { get; } = port;
    public HttpClient Http { get; } = http;
    public TestFileClipboard Clipboard { get; } = clipboard;
    public string ClipboardCache { get; } = clipboardCache;
    public RemoteHost Host { get; } = host;
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Host.DisposeAsync();
        Certificate.Dispose();
        // This directory is created above solely for this run.
        Directory.Delete(Root, true);
    }
}

sealed class TestFileClipboard : IFileClipboard
{
    private ClipboardSelection value = new(1, "", []);
    public Action? BeforeWrite;
    public void Set(string[] paths, string text = "", string origin = "", byte[]? image = null, string html = "") { lock (this) value = new(value.Sequence + 1, origin, paths, text, image, html); }
    public Task<ClipboardSelection> Read(CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (this) return Task.FromResult(value); }
    public Task<bool> Write(string[] paths, string text, string origin, uint expectedSequence, CancellationToken ct, byte[]? image = null, string? html = null)
    {
        ct.ThrowIfCancellationRequested(); lock (this)
        {
            var callback = BeforeWrite; BeforeWrite = null; callback?.Invoke();
            if (value.Sequence != expectedSequence) return Task.FromResult(false);
            value = new(value.Sequence + 1, origin, paths, text, image, html ?? ""); return Task.FromResult(true);
        }
    }
}
