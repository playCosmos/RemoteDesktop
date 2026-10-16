using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteDesktop;

System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

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
}
finally { Directory.Delete(retiredRoot, true); Directory.Delete(retiredOutside, true); }
var placed = new ClientSettings { WindowLeft = -120, WindowTop = 40, WindowWidth = 1100, WindowHeight = 720, WindowMaximized = true };
Check(placed.TryGetWindowPlacement(-1920, 0, 3840, 1080, out var placement) && placement.Left == -120 && placement.Maximized, "window placement restores on a shifted desktop");
Check(!new ClientSettings { WindowLeft = 5000, WindowTop = 0, WindowWidth = 1000, WindowHeight = 700 }.TryGetWindowPlacement(0, 0, 1920, 1080, out _), "off-screen window placement is ignored");
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
Check(UiText.Explain(new InvalidOperationException("DXGI interface is unavailable."), "fallback") == "화면 캡처를 시작하지 못했습니다.", "capture startup failures stay in Korean");
Check(!UiText.OfferMonitorChoice(2) && UiText.OfferMonitorChoice(3), "one monitor does not add a display picker");
Check(UiText.ConnectionFailure(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized), false) == "비밀번호가 올바르지 않습니다.", "a rejected login says the password is wrong");
Check(UiText.ConnectionFailure(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized), true) == "서버에서 연결을 끊었습니다. 다시 접속하세요.", "a dropped session is not described as a bad password");
Check(UiText.ConnectionFailure(new TaskCanceledException(), true) == "서버 응답이 끊어졌습니다. 다시 접속하세요.", "a stalled session says the connection dropped");
Check(UiText.ConnectionFailure(new TaskCanceledException(), false) == "서버 응답이 없습니다. 주소와 포트를 확인하세요.", "a login timeout still points at the address");
Check(UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("reset"))) && UiText.ShouldReconnect(UiText.ClassifyDrop(new TimeoutException("slow"))) && UiText.ShouldReconnect(UiText.ClassifyDrop(new TaskCanceledException())), "a dropped network or a timeout can reconnect");
Check(!UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized))) && !UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("busy", null, HttpStatusCode.Conflict))) && !UiText.ShouldReconnect(UiText.ClassifyDrop(new HttpRequestException("The SSL connection could not be established"))), "a rejected login, a busy host, or a certificate mismatch does not reconnect");
Check(UiText.ReconnectDelay(1) == TimeSpan.FromSeconds(2) && UiText.ReconnectDelay(2) == TimeSpan.FromSeconds(4) && UiText.ReconnectDelay(3) == TimeSpan.FromSeconds(8) && UiText.MaxAutomaticReconnects == 3, "reconnect waits grow and then stop");
Check(UiText.Reconnecting(1, 3, (int)UiText.ReconnectDelay(1).TotalSeconds) == "연결이 끊겼습니다. 2초 후 다시 연결합니다. (1/3)", "reconnect tells how long the next try waits");
Check(UiText.ReconnectHold() == "다시 연결하는 중…", "a dropped picture stays up while the next try is waiting");
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
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", null, null) == "Personal Remote Desktop 0.9.59", "an idle window keeps the product title");
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", "10.0.0.8:45831", "192.168.0.2") == "10.0.0.8:45831 — 192.168.0.2 연결됨", "both sessions name the remote pc and the inbound client");
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", "10.0.0.8:45831", null) == "10.0.0.8:45831 — Personal Remote Desktop 0.9.59", "an open remote session names the window");
Check(UiText.WindowTitle("Personal Remote Desktop 0.9.59", null, "192.168.0.2") == "192.168.0.2 연결됨 — Personal Remote Desktop 0.9.59", "an inbound client names the window");
Check(UiText.HostTabLabel(null) == "이 PC" && UiText.HostTabLabel("192.168.0.2") == "이 PC · 192.168.0.2", "the host tab names the connected client");
Check(UiText.FullscreenTitle(null, null) == "Personal Remote Desktop · F11로 복귀" && UiText.FullscreenTitle("10.0.0.8:45831", "192.168.0.2") == "10.0.0.8:45831 — 192.168.0.2 연결됨 · F11로 복귀", "fullscreen names the remote pc and an inbound client");
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
Check(UiText.CaptureSystemShortcut(0x12, true, false, false, false, false, false) && UiText.CaptureSystemShortcut(0x5B, true, false, false, false, false, false), "alt and the windows key go to the remote pc");
Check(UiText.CaptureSystemShortcut(0x73, true, true, false, true, false, false) && UiText.CaptureSystemShortcut(0x09, true, true, false, true, false, false), "alt+f4 and alt+tab stay on the remote pc");
Check(UiText.CaptureSystemShortcut(0x45, true, false, false, false, true, false), "win+e does not open the local explorer");
Check(UiText.CaptureSystemShortcut(0x1B, true, false, true, false, false, false) && UiText.CaptureSystemShortcut(0x2C, true, false, false, false, false, false), "ctrl+shift+esc and print screen are sent remotely");
Check(!UiText.CaptureSystemShortcut(0x41, true, false, false, false, false, false) && UiText.CaptureSystemShortcut(0x41, false, false, false, false, false, true) && !UiText.CaptureSystemShortcut(0x41, false, false, false, false, false, false), "plain typing stays local until a captured modifier is held");
Check(!UiText.ShowHostSetup(true) && UiText.ShowHostSetup(false), "host setup hides while the server is running");
Check(UiText.HostCardTitle(false, false, null) == "접속 받기" && UiText.HostCardTitle(false, true, null) == "접속 대기 중" && UiText.HostCardTitle(true, true, "192.168.0.8") == "192.168.0.8 연결 중" && UiText.HostCardTitle(true, true, " ") == "이 PC를 보는 중", "the host card names waiting and the connected client");
Check(UiText.HostCardTitle(true, true, "192.168.0.8", true) == "192.168.0.8 응답 없음" && UiText.HostCardTitle(true, true, " ", true) == "응답이 없습니다" && UiText.ShareTitle(true, true) == "응답이 멈춘 접속입니다" && UiText.ShareHint(true, true).Contains("현재 접속 끊기"), "a quiet client is not described as still watching");
Check(UiText.FullscreenAway(null) == "F11로 이 창에 되돌립니다." && UiText.FullscreenAway(" 10.0.0.8:45831 ") == "10.0.0.8:45831 화면입니다. F11로 이 창에 되돌립니다.", "the original window explains a fullscreen session");
Check(UiText.EdgeToEdge(true, true, false) && !UiText.EdgeToEdge(true, true, true) && !UiText.EdgeToEdge(true, false, false) && !UiText.EdgeToEdge(false, true, false), "the picture fills this window only while the session is shown here");
Check(UiText.ShareTitle(false) == "이 PC에 접속하려면" && UiText.ShareTitle(true) == "지금은 한 대만 연결됩니다" && UiText.ShareHint(false).Contains("수락 없이") && UiText.ShareHint(true).StartsWith("이미 다른 기기가 연결되어 있습니다."), "an occupied host says another device cannot connect");
Check(!UiText.RemotePointer(0, false) && !UiText.RemotePointer(20, false) && UiText.RemotePointer(20.1, false) && UiText.RemotePointer(0, true), "the toolbar strip does not move the remote pointer unless a drag is captured");
Check(UiText.HostPresence(true, true) == "LiveBrush" && UiText.HostPresence(false, true) == "AccentBrush" && UiText.HostPresence(false, false) == "MutedBrush", "the host dot distinguishes a connected client from waiting");
Check(!UiText.HostShowsQuiet(TimeSpan.FromSeconds(4.9)) && UiText.HostShowsQuiet(TimeSpan.FromSeconds(5)) && UiText.HostPresence(true, true, true) == "DangerBrush", "the host marks a client quiet when a new login may replace it");
Check(UiText.ElapsedClock(TimeSpan.FromSeconds(5)) == "00:05" && UiText.ElapsedClock(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(9)) == "2:04:09", "elapsed time stays compact");
Check(UiText.SessionHealth(null, "15 fps · 40 ms") == "15 fps · 40 ms" && UiText.SessionHealth("03:12", "15 fps · 40 ms") == "03:12 · 15 fps · 40 ms", "session health keeps the clock beside the frame rate");
Check(UiText.FrameHealth(15, 1, 40, false) == "15 fps · 40 ms" && UiText.FrameHealth(0, 1, 40, true) == "화면 유지 · 40 ms" && UiText.FrameHealth(0, 1, -1, false) == "수신 대기 · —", "a still picture stays distinct from a missing frame");
Check(UiText.StaleFrame() == "화면 응답 없음" && UiText.FrameStale(TimeSpan.FromSeconds(3)) && !UiText.FrameStale(TimeSpan.FromSeconds(2.9)), "a silent stream is reported after three seconds");
Check(UiText.PicturePaused() == "화면 일시중지" && UiText.HealthTone(UiText.PicturePaused()) == "MutedBrush", "a hidden picture pauses without looking like a dropped session");
Check(UiText.ViewerWatching(false, false, false, true) && !UiText.ViewerWatching(false, false, true, true) && !UiText.ViewerWatching(false, false, false, false) && UiText.ViewerWatching(true, false, true, false) && !UiText.ViewerWatching(true, true, false, true), "frames continue only while the remote picture is on screen");
Check(UiText.HealthTone("15 fps · 40 ms") == "InkBrush" && UiText.HealthTone("화면 유지 · 40 ms") == "MutedBrush" && UiText.HealthTone("화면 응답 없음") == "DangerBrush" && UiText.HealthTone("연결 종료") == "DangerBrush", "health text color follows whether frames are arriving");
Check(UiText.SessionPreset(null, "균형", 15) == "균형 · 15 FPS" && UiText.SessionPreset("10.0.0.8:45831", "균형", 15) == "10.0.0.8:45831 · 균형 · 15 FPS", "the toolbar names the remote pc");
Check(UiText.SessionPreset("10.0.0.8:45831", "고화질", 15, "저대역폭") == "10.0.0.8:45831 · 고화질 → 저대역폭 · 15 FPS" && UiText.SessionPreset(null, "고화질", 15, "고화질") == "고화질 · 15 FPS", "the toolbar shows when the stream quality was lowered");
Check(UiText.QualityShift("고화질", null, "고화질") == null && UiText.QualityShift("고화질", null, "저대역폭") == "끊김을 줄이려고 화질을 낮췄습니다. 고화질 대신 저대역폭." && UiText.QualityShift("고화질", "저대역폭", "저대역폭") == null && UiText.QualityShift("고화질", "저대역폭", "고화질") == "여유가 생겨 화질을 고화질로 되돌렸습니다.", "quality changes are announced once");
Check(UiText.QualityRestored("균형") == "여유가 생겨 화질을 균형으로 되돌렸습니다." && UiText.QualityRestored("저대역폭") == "여유가 생겨 화질을 저대역폭으로 되돌렸습니다.", "restored quality uses the right Korean particle");
Check(UiText.RevealTip(null, null) == "세션 조작 열기" && UiText.RevealTip("연결된 지 03:12", null) == "연결된 지 03:12 · 세션 조작 열기" && UiText.RevealTip("연결된 지 03:12", "192.168.0.2") == "연결된 지 03:12 · 이 PC에 192.168.0.2 연결됨", "the hidden toolbar button explains the session");
Check(UiText.IsProblem("클립보드 보내기 실패: 다시 시도하세요.") && UiText.IsProblem("클립보드 동기화 오류: 다시 시도하세요.") && UiText.IsProblem("붙여넣기 중단: 다시 시도하세요."), "session failures stay visible");
Check(UiText.SessionBaseline(true) == "연결됨" && UiText.SessionBaseline(false) == "화면만 보는 중", "a live session returns to connected, and view-only keeps its own label");
Check(!UiText.SessionNoticeHolds("연결됨") && !UiText.SessionNoticeHolds("화면 품질: 고화질") && !UiText.SessionNoticeHolds("클립보드 파일 수신 완료"), "ordinary session notes settle back to the connection state");
Check(UiText.SessionNoticeHolds("클립보드 보내기 실패: 다시 시도하세요.") && UiText.SessionNoticeHolds(UiText.ClipboardFileProgress("클립보드 수신", 0, 1, "메모.txt", 50, 200)), "failures and an unfinished file copy stay on screen");
Check(UiText.SessionReveal("03:12 · 15 fps · 40 ms", true) == "03:12 · 15 fps · 40 ms" && UiText.SessionReveal("03:12 · 15 fps · 40 ms", false) == "03:12 · 15 fps · 40 ms · 보기만", "the collapsed session control still says when input is off");
Check(!UiText.IsProblem("연결됨") && !UiText.IsProblem("화면 품질: 균형") && !UiText.IsProblem(""), "ordinary session status is not treated as a failure");
recent.SelectedTab = "host";
var recentPath = Path.Combine(Path.GetTempPath(), "prd-recent-" + Guid.NewGuid().ToString("N") + ".json");
recent.Save(recentPath);
var loadedRecent = ClientSettings.Load(recentPath);
Check(loadedRecent.Recent.Count == 4 && loadedRecent.Recent[0].Address == "10.1.0.4:45831" && loadedRecent.Recent[0].FrameRate == 15 && loadedRecent.OpenHostTab && !File.ReadAllText(recentPath).Contains("password", StringComparison.OrdinalIgnoreCase), "recent hosts and the last tab survive reload without a password");
File.Delete(recentPath);

await Password();
await Session();
await ClipboardFiles();
if (args.Contains("--capture")) await Capture();
Console.WriteLine(passed + " integration checks passed.");

async Task Password()
{
    await using var site = await OpenHost();
    Check(Desktop.SmoothCapture, "direct host enables stutter reduction from its saved setting");
    Check(site.Certificate.HasPrivateKey, "server TLS certificate has a current-user private key");
    Check(site.Settings.Verify(site.Password) && !site.Settings.Verify("wrong"), "salted password verification");
    var hostPath = Path.Combine(site.Root, "host.json");
    var persistedHost = new HostSettings { Port = 45999 };
    persistedHost.SetPassword(site.Password, hostPath);
    var restartedHost = HostSettings.Load(hostPath);
    Check(restartedHost.HasPassword && restartedHost.Verify(site.Password), "server password works after restart without re-entry");
    Check(restartedHost.Port == 45999, "server port survives restart");
    Check(restartedHost.UseSmoothHost, "stutter reduction stays enabled when the setting is absent");
    File.WriteAllText(hostPath, "{\"Port\":45999}");
    Check(HostSettings.Load(hostPath).UseSmoothHost, "older host settings keep stutter reduction enabled");
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
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password })) Check((int)response.StatusCode == 429, "login rate limit");
    await Task.Delay(2100);
    var hostNotices = new List<string>();
    site.Host.Status += hostNotices.Add;
    using (var response = await site.Http.PostAsJsonAsync("login", new { password = site.Password }))
    {
        Check(response.IsSuccessStatusCode, "password login without host approval");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(result.GetProperty("protocol").GetInt32() == 1 && result.GetProperty("maxFileBytes").GetInt64() == 1073741824, "login returns protocol version and file limit");
        site.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.GetProperty("token").GetString());
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
    using (var response = await site.Http.PostAsync("input", Json("{\"kind\":\"release\"}"))) Check(response.StatusCode == HttpStatusCode.OK, "release with nothing held is accepted");
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
    Check(hostNotices[^1] == "접속 대기 중 · 포트 " + site.Port, "a departed client returns the host to listening");
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
    using (var response = await site.Http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test", clipBefore.Id, [new("../escape.bin", 1)]))) Check(response.StatusCode == HttpStatusCode.BadRequest, "clipboard filename traversal rejected");
    using (var response = await site.Http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test", clipBefore.Id, [new("oversize.bin", RemoteHost.MaxFileBytes + 1)]))) Check(response.StatusCode == HttpStatusCode.BadRequest, "clipboard file size limit enforced before upload");
    var staleBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    using (var response = await SendClipboardBytes(staleBatch, payload, Convert.ToHexString(SHA256.HashData(payload)))) response.EnsureSuccessStatusCode();
    site.Clipboard.Set([]);
    using (var response = await site.Http.PostAsync("clipboard/uploads/" + staleBatch + "/commit", null)) Check(response.StatusCode == HttpStatusCode.Conflict, "new host copy is not overwritten by an older transfer");
    using (var response = await site.Http.DeleteAsync("clipboard/uploads/" + staleBatch)) response.EnsureSuccessStatusCode();
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
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { Check(true, "old clipboard snapshot cannot read new files"); }
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
    var cropped = await client.Frame(CancellationToken.None, "all", 30, .25, .25, .5, .5);
    using var croppedDecoded = System.Drawing.Image.FromStream(new MemoryStream(cropped));
    Check(croppedDecoded.Width == 1920 && croppedDecoded.Height == 540, "viewport capture reduces requested frame area");
    Desktop.SmoothCapture = true;
    var smoothDesktop = new Desktop();
    var smoothFrame = smoothDesktop.CaptureDetailed(null, 55, 0, 0, 1, 1, 30, true);
    Check(smoothFrame.Bytes.Length > 100 && smoothFrame.Bytes[0] == 0xFF && smoothFrame.Bytes[1] == 0xD8, "smooth capture returns JPEG");
    var repeatedFrame = smoothDesktop.CaptureDetailed(null, 55, 0, 0, 1, 1, 30, true);
    Check(repeatedFrame.Bytes[0] == 0xFF && repeatedFrame.Bytes[1] == 0xD8 && repeatedFrame.LocalCursor, "smooth capture serves a prepared frame");
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
    public void Set(string[] paths, string text = "", string origin = "") { lock (this) value = new(value.Sequence + 1, origin, paths, text); }
    public Task<ClipboardSelection> Read(CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (this) return Task.FromResult(value); }
    public Task<bool> Write(string[] paths, string text, string origin, uint expectedSequence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (this)
        {
            var callback = BeforeWrite; BeforeWrite = null; callback?.Invoke();
            if (value.Sequence != expectedSequence) return Task.FromResult(false);
            value = new(value.Sequence + 1, origin, paths, text); return Task.FromResult(true);
        }
    }
}
