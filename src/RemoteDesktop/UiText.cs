using System.Net.Sockets;
using System.Net;
using System.Net.Http;

namespace RemoteDesktop;

public static class UiText
{
    public static string Explain(Exception exception, string fallback)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return "이 포트는 이미 사용 중입니다. 남아 있는 이 프로그램을 종료한 뒤 다시 시작하세요.";
            var message = current.Message ?? "";
            if (message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || message.Contains("EADDRINUSE", StringComparison.Ordinal))
                return "이 포트는 이미 사용 중입니다. 남아 있는 이 프로그램을 종료한 뒤 다시 시작하세요.";
            if (message.Contains("OPAQUE", StringComparison.OrdinalIgnoreCase) || message.Contains("password-key", StringComparison.OrdinalIgnoreCase))
                return "보안 등록에 실패했습니다. 현재 비밀번호를 다시 저장하세요.";
            if (message.Contains("Invalid clipboard origin", StringComparison.OrdinalIgnoreCase))
                return "클립보드 전송을 확인하지 못했습니다. 다시 복사하세요.";
            if (message.Contains("Invalid clipboard file", StringComparison.OrdinalIgnoreCase) || message.Contains("File hash mismatch", StringComparison.OrdinalIgnoreCase) || message.Contains("File size mismatch", StringComparison.OrdinalIgnoreCase))
                return "클립보드 파일이 맞지 않습니다. 다시 복사하세요.";
            if (message.Contains("DXGI", StringComparison.OrdinalIgnoreCase))
                return "화면 캡처를 시작하지 못했습니다.";
            if (HasHangul(message)) return message;
        }
        return fallback;
    }

    public static string ShortSecret(string? value)
    {
        value = value?.Trim() ?? "";
        return value.Length <= 16 ? value : value[..16] + "…";
    }

    public static string ClientConnected(IPAddress? address)
    {
        var peer = PeerLabel(address);
        return peer.Length == 0 ? "클라이언트 연결됨" : "클라이언트 연결됨 · " + peer;
    }

    public static string Listening(int port) => port is > 0 and <= 65535 ? "접속 대기 중 · 포트 " + port : "접속 대기 중";

    public enum SessionEndKind { Left, Kicked, TimedOut }

    public static string SessionEnded(string? peer, SessionEndKind kind, int port)
    {
        var name = Blank(peer) ?? "상대 PC";
        var why = kind switch
        {
            SessionEndKind.Kicked => name + " 연결을 끊었습니다.",
            SessionEndKind.TimedOut => name + " 응답이 없어 연결을 끊었습니다.",
            _ => name + " 연결이 종료되었습니다."
        };
        return why + " " + Listening(port);
    }

    public static string HostStopped(string? peer)
    {
        var name = Blank(peer);
        return name == null ? "서버 중지됨 · 저장된 비밀번호로 다시 시작할 수 있습니다." : name + " 연결을 끊고 서버를 중지했습니다.";
    }

    public static bool HostSessionEnded(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains(" 연결이 종료되었습니다.", StringComparison.Ordinal)
            || text.Contains(" 연결을 끊었습니다. 접속 대기", StringComparison.Ordinal)
            || text.Contains(" 응답이 없어 연결을 끊었습니다.", StringComparison.Ordinal)
            || text.Contains(" 연결을 끊고 서버를 중지했습니다.", StringComparison.Ordinal);
    }

    public static string PeerLabel(IPAddress? address)
    {
        if (address is null) return "";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) address = new IPAddress(address.GetAddressBytes());
        return address.ToString();
    }

    public static string WindowTitle(string product, string? outbound, string? inbound)
    {
        var viewing = Blank(outbound);
        var hosting = Blank(inbound);
        if (viewing != null && hosting != null) return viewing + " — " + hosting + " 연결됨";
        if (viewing != null) return viewing + " — " + product;
        if (hosting != null) return hosting + " 연결됨 — " + product;
        return product;
    }

    public static string FullscreenTitle(string? outbound, string? inbound)
    {
        var viewing = Blank(outbound);
        var hosting = Blank(inbound);
        if (viewing != null && hosting != null) return viewing + " — " + hosting + " 연결됨 · F11 또는 Esc로 복귀";
        if (viewing != null) return viewing + " — F11 또는 Esc로 복귀";
        return "Personal Remote Desktop · F11 또는 Esc로 복귀";
    }

    public static bool EscapeLeavesFullscreen(bool fullscreen, bool inputCaptured) => fullscreen && !inputCaptured;

    public static string? ClosePrompt(string? viewing, string? hosting)
    {
        var remote = Blank(viewing);
        var guest = Blank(hosting);
        if (remote == null && guest == null) return null;
        if (remote != null && guest != null) return remote + " 화면을 보고 있고, " + guest + " 이 PC를 보고 있습니다. 창을 닫고 둘 다 끊을까요?";
        if (guest != null) return guest + " 이 PC를 보고 있습니다. 창을 닫고 연결을 끊을까요?";
        return remote + " 화면을 보고 있습니다. 창을 닫고 연결을 끊을까요?";
    }

    public static string HostTabLabel(string? inbound)
    {
        var hosting = Blank(inbound);
        return hosting == null ? "이 PC" : "이 PC · " + hosting;
    }

    public static string ConnectedFor(TimeSpan elapsed) => "연결된 지 " + ElapsedClock(elapsed);

    public static string ElapsedClock(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? (int)elapsed.TotalHours + ":" + elapsed.Minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00")
            : elapsed.Minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00");
    }

    public static string SessionHealth(string? clock, string? detail)
    {
        detail = string.IsNullOrWhiteSpace(detail) ? "측정 중" : detail.Trim();
        return string.IsNullOrWhiteSpace(clock) ? detail : clock.Trim() + " · " + detail;
    }

    public static string FrameHealth(int changedFrames, double seconds, long roundTripMilliseconds, bool pictureHeld)
    {
        var latency = roundTripMilliseconds < 0 ? "—" : roundTripMilliseconds + " ms";
        if (changedFrames <= 0) return (pictureHeld ? "화면 유지" : "수신 대기") + " · " + latency;
        var fps = changedFrames / Math.Max(0.01, seconds);
        return fps.ToString("F0") + " fps · " + latency;
    }

    public static string StaleFrame() => "화면 응답 없음";

    public static bool FrameStale(TimeSpan sinceFrame) => sinceFrame >= TimeSpan.FromSeconds(3);

    public static string PicturePaused() => "화면 일시중지";

    public static bool ViewerWatching(bool fullscreen, bool fullscreenMinimized, bool windowMinimized, bool clientTabSelected)
        => fullscreen ? !fullscreenMinimized : !windowMinimized && clientTabSelected;

    public static string HealthTone(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "MutedBrush";
        if (detail.Contains("응답 없음", StringComparison.Ordinal) || detail.Contains("종료", StringComparison.Ordinal)) return "DangerBrush";
        if (detail.Contains("fps", StringComparison.Ordinal)) return "InkBrush";
        return "MutedBrush";
    }

    public static string SessionPreset(string? address, string quality, int frameRate)
    {
        var preset = quality + " · " + frameRate + " FPS";
        return string.IsNullOrWhiteSpace(address) ? preset : address.Trim() + " · " + preset;
    }

    public static string SessionPreset(string? address, string quality, int frameRate, string? delivered)
        => SessionPreset(address, DeliveredQuality(quality, delivered), frameRate);

    public static string DeliveredQuality(string requested, string? delivered)
    {
        requested = requested.Trim();
        if (string.IsNullOrWhiteSpace(delivered) || delivered.Trim() == requested) return requested;
        return requested + " → " + delivered.Trim();
    }

    public static int QualityRank(string label) => label == "고화질" ? 2 : label == "균형" ? 1 : 0;

    public static string QualityReduced(string requested, string delivered) => "끊김을 줄이려고 화질을 낮췄습니다. " + requested + " 대신 " + delivered + ".";

    public static string QualityRestored(string quality) => "여유가 생겨 화질을 " + Euro(quality) + " 되돌렸습니다.";

    public static string? QualityShift(string requested, string? previousDelivered, string delivered)
    {
        if (string.Equals(previousDelivered, delivered, StringComparison.Ordinal)) return null;
        if (delivered == requested) return string.IsNullOrEmpty(previousDelivered) || previousDelivered == requested ? null : QualityRestored(requested);
        return QualityRank(delivered) < QualityRank(requested) ? QualityReduced(requested, delivered) : null;
    }

    private static string Euro(string word)
    {
        if (word.Length == 0) return "로";
        var last = word[^1];
        if (last is < '가' or > '힣') return word + "로";
        var batchim = (last - '가') % 28;
        return word + (batchim is 0 or 8 ? "로" : "으로");
    }

    public static string RevealTip(string? elapsedLabel, string? inbound)
    {
        var duration = Blank(elapsedLabel);
        var peer = Blank(inbound);
        if (duration != null && peer != null) return duration + " · 이 PC에 " + peer + " 연결됨";
        if (duration != null) return duration + " · 세션 조작 열기";
        if (peer != null) return "이 PC에 " + peer + " 연결됨 · 세션 조작 열기";
        return "세션 조작 열기";
    }

    public static bool NeedsAttention(bool newlyConnected, bool windowActive) => newlyConnected && !windowActive;

    public static string InboundArrived(string? peer)
    {
        var name = Blank(peer);
        return name == null ? "이 PC에 연결됨" : name + " 이 PC에 연결됨";
    }

    public static bool HostPageVisible(bool hostTabSelected, bool fullscreen) => hostTabSelected && !fullscreen;

    public static bool AnnounceInbound(bool newlyConnected, bool hostPageVisible) => newlyConnected && !hostPageVisible;

    public static bool CompletesReleaseChord(int key, bool ctrlDown, bool altDown, bool shiftDown)
    {
        if (key is 0x11 or 0xA2 or 0xA3) return altDown && shiftDown;
        if (key is 0x12 or 0xA4 or 0xA5) return ctrlDown && shiftDown;
        if (key is 0x10 or 0xA0 or 0xA1) return ctrlDown && altDown;
        return false;
    }

    public static string InputReleaseTip() => "Ctrl+Alt+Shift로 키보드를 이 PC에 되돌립니다.";

    public static string InputHint(bool inputEnabled) => inputEnabled
        ? "화면을 클릭하면 키보드가 원격 PC로 갑니다. " + InputReleaseTip()
        : "입력 제어가 꺼져 있습니다. 화면만 보는 중입니다.";

    public static string InputReleased() => "키보드를 이 PC로 되돌렸습니다.";

    public static string InputBacklog() => "입력이 밀려 눌린 키와 버튼을 놓습니다.";

    public static bool CaptureSystemShortcut(int key, bool down, bool altFlag, bool ctrlDown, bool altHeld, bool winHeld, bool suppressed)
    {
        if (!down) return suppressed;
        if (key is 0x12 or 0x5B or 0x5C) return true;
        if (altHeld || winHeld || altFlag) return true;
        if (key == 0x2C) return true;
        return ctrlDown && key == 0x1B;
    }

    public static bool ShowHostSetup(bool serverRunning) => !serverRunning;

    public static string HostCardTitle(bool clientConnected, bool serverLive, string? peer)
        => HostCardTitle(clientConnected, serverLive, peer, quiet: false);

    public static string HostCardTitle(bool clientConnected, bool serverLive, string? peer, bool quiet)
    {
        if (clientConnected)
        {
            var name = Blank(peer);
            if (quiet) return name == null ? "응답이 없습니다" : name + " 응답 없음";
            return name == null ? "이 PC를 보는 중" : name + " 연결 중";
        }
        return serverLive ? "접속 대기 중" : "접속 받기";
    }

    public static string FullscreenAway(string? address)
    {
        var target = Blank(address);
        return target == null ? "F11 또는 Esc로 이 창에 되돌립니다." : target + " 화면입니다. F11 또는 Esc로 이 창에 되돌립니다.";
    }

    public static bool EdgeToEdge(bool connected, bool clientTabSelected, bool fullscreen) => connected && clientTabSelected && !fullscreen;

    public static string ShareTitle(bool occupied, bool quiet = false)
        => ShareTitle(occupied, quiet, accepting: true);

    public static string ShareTitle(bool occupied, bool quiet, bool accepting)
        => quiet ? "응답이 멈춘 접속입니다" : occupied ? "지금은 한 대만 연결됩니다" : accepting ? "이 PC에 접속하려면" : "접속 대기를 시작하면";

    public static string ShareHint(bool occupied, bool quiet = false)
        => ShareHint(occupied, quiet, accepting: true);

    public static string ShareHint(bool occupied, bool quiet, bool accepting) => quiet
        ? "상대가 응답하지 않습니다. 다른 PC가 이 접속을 이어받을 수 있고, 바로 비우려면 현재 접속 끊기를 누르세요."
        : occupied
        ? "이미 다른 기기가 연결되어 있습니다. 현재 접속을 끊어야 다른 PC가 들어올 수 있습니다."
        : accepting
        ? "상대 PC의 주소 칸에 붙여 넣으면 포트도 같이 들어갑니다. 비밀번호만 맞으면 수락 없이 연결됩니다."
        : "아직 접속을 받고 있지 않습니다. 접속 대기를 시작한 뒤에 주소를 복사하세요.";

    public static string AddressCopied(string endpoint, bool accepting)
        => accepting
            ? endpoint + " 복사됨 · 상대 PC의 주소 칸에 붙여 넣으세요."
            : endpoint + " 복사됨 · 접속 대기를 시작한 뒤에 붙여 넣으세요.";

    public readonly record struct ConnectCheck(string? Problem, string Field);

    public const string InvalidField = "invalid";

    public static ConnectCheck CheckConnectForm(string? address, string? port, string? password, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(address)) return new("서버 주소를 입력하세요.", "address");
        if (!int.TryParse(port?.Trim(), out var parsed) || parsed is < 1 or > 65535) return new("포트 범위: 1–65535", "port");
        if (string.IsNullOrEmpty(password)) return new("접속 비밀번호를 입력하세요.", "password");
        var pin = fingerprint?.Trim() ?? "";
        if (pin.Length > 0 && (pin.Length != 64 || !pin.All(Uri.IsHexDigit))) return new("서버의 SHA-256 지문 64자리를 입력하세요.", "fingerprint");
        return new(null, "");
    }

    public static string ConnectFailureField(Exception exception) => ClassifyDrop(exception) switch
    {
        DropKind.Rejected => "password",
        DropKind.Certificate => "fingerprint",
        DropKind.Network or DropKind.Timeout => "address",
        _ => ""
    };

    public static string HostFailureField(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse) return "port";
            var message = current.Message ?? "";
            if (message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || message.Contains("EADDRINUSE", StringComparison.Ordinal)) return "port";
        }
        return "";
    }

    public readonly record struct HostSetupCheck(string? Problem, string Field);

    public static HostSetupCheck CheckHostSetup(string? port, string? password, bool passwordRequired, bool confirmingExisting = false, string? confirmation = null, bool matchRequired = false)
    {
        if (!int.TryParse(port?.Trim(), out var parsed) || parsed is < 1024 or > 65535) return new("포트 범위: 1024–65535", "port");
        if (string.IsNullOrEmpty(password))
            return passwordRequired ? new(confirmingExisting ? "현재 비밀번호를 입력하세요." : "비밀번호를 8~1024자 입력하세요.", "password") : new(null, "");
        if (password.Length > 1024 || (!confirmingExisting && password.Length < 8)) return new("비밀번호를 8~1024자 입력하세요.", "password");
        if (matchRequired && !string.Equals(password, confirmation, StringComparison.Ordinal))
            return new(string.IsNullOrEmpty(confirmation) ? "비밀번호를 한 번 더 입력하세요." : "비밀번호 확인이 일치하지 않습니다.", "confirm");
        return new(null, "");
    }

    public static string AddressesChanged() => "네트워크 주소가 바뀌었습니다. 새 주소를 다시 복사하세요.";

    public static string ClipboardFileProgress(string action, int index, int count, string name, long sent, long size)
    {
        var item = count <= 1 ? name : (index + 1) + "/" + count + " · " + name;
        if (size <= 0) return action + " · " + item;
        var percent = (int)Math.Clamp(Math.Round(sent * 100d / size), 0, 100);
        return action + " · " + item + " · " + percent + "%";
    }

    public static bool ShouldReportProgress(int previousPercent, int nextPercent)
        => previousPercent < 0 || nextPercent - previousPercent >= 5 || (nextPercent == 100 && previousPercent < 100);

    public static bool RemotePointer(double stageY, bool captured) => captured || stageY > 20;

    public static bool RemoteButtonRelease(bool captured) => captured;

    public static bool JpegFullColor(int quality) => quality >= 55;

    public static bool CrispPicture(double deviceScale)
    {
        if (!double.IsFinite(deviceScale) || deviceScale <= 0) return false;
        var whole = Math.Round(deviceScale);
        return whole is >= 1 and <= 3 && Math.Abs(deviceScale - whole) <= 0.08;
    }

    public static string HostPresence(bool clientConnected, bool serverLive, bool quiet = false)
        => clientConnected ? (quiet ? "DangerBrush" : "LiveBrush") : serverLive ? "AccentBrush" : "MutedBrush";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsProblem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("실패", StringComparison.Ordinal)
            || text.Contains("오류", StringComparison.Ordinal)
            || text.Contains("중단", StringComparison.Ordinal)
            || text.Contains("초과", StringComparison.Ordinal)
            || text.Contains("못했습니다", StringComparison.Ordinal);
    }

    public static string SessionBaseline(bool inputEnabled) => inputEnabled ? "연결됨" : "화면만 보는 중";

    public static bool SessionNoticeHolds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsProblem(text) || ReconnectNotice(text)) return true;
        return text.Contains('%') && !text.Contains("완료", StringComparison.Ordinal);
    }

    public static bool InboundArrival(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.Contains("이 PC에 연결됨", StringComparison.Ordinal);

    public static TimeSpan SessionNoticeDuration(string? text)
        => InboundArrival(text) || HostSessionEnded(text) ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(4.5);

    public static string FileDropHint(bool inputEnabled)
        => inputEnabled ? "놓으면 상대 PC에서 붙여넣을 수 있습니다" : "입력 제어를 켜야 파일을 보낼 수 있습니다";

    public static string FileDropStarted(int count)
        => count <= 1 ? "파일을 상대 PC로 보내는 중" : count + "개 파일을 상대 PC로 보내는 중";

    public static bool SessionOnPicture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsProblem(text) || InboundArrival(text) || HostSessionEnded(text) || ReconnectNotice(text) || text == FileDropHint(false) || text.Contains("보내는 중", StringComparison.Ordinal) || text.Contains("붙여넣", StringComparison.Ordinal)) return true;
        return text.Contains('%') && text.Contains("클립보드", StringComparison.Ordinal);
    }

    public static string SessionReveal(string health, bool inputEnabled)
        => inputEnabled || string.IsNullOrWhiteSpace(health) ? health : health.Trim() + " · 보기만";

    public static bool OfferMonitorChoice(int listedDisplays) => listedDisplays > 2;

    public static string SwitchingDisplay(string? name)
    {
        var label = Blank(name);
        return label == null ? "다른 모니터로 바꾸는 중…" : label + " 화면으로 바꾸는 중…";
    }

    public readonly record struct MonitorSpot(int Left, int Top, int Width, int Height);

    public static string DisplayPlace(bool entireDesktop, int left, int top, int width, int height, IReadOnlyList<MonitorSpot> monitors)
    {
        if (entireDesktop || monitors == null || monitors.Count <= 1 || width <= 0 || height <= 0) return "";
        var centers = monitors.Where(m => m.Width > 0 && m.Height > 0).Select(m => (X: m.Left + m.Width / 2.0, Y: m.Top + m.Height / 2.0)).ToArray();
        if (centers.Length <= 1) return "";
        var minX = centers.Min(c => c.X);
        var maxX = centers.Max(c => c.X);
        var minY = centers.Min(c => c.Y);
        var maxY = centers.Max(c => c.Y);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        var cx = left + width / 2.0;
        var cy = top + height / 2.0;
        string? x = null;
        string? y = null;
        if (spanX >= 80 && spanX >= spanY)
        {
            var t = (cx - minX) / spanX;
            x = t < 0.34 ? "왼쪽" : t > 0.66 ? "오른쪽" : "가운데";
        }
        if (spanY >= 80 && spanY >= spanX * 0.35)
        {
            var t = (cy - minY) / spanY;
            y = t < 0.34 ? "위" : t > 0.66 ? "아래" : "중간";
        }
        if (x != null && y != null)
        {
            if (x == "가운데" && y == "중간") return "가운데";
            return x + " " + y;
        }
        return x ?? y ?? "";
    }

    public static string DisplayChoice(string? name, string? place, int width, int height, bool primary, bool entireDesktop)
    {
        var size = width > 0 && height > 0 ? width + "×" + height : null;
        if (entireDesktop)
        {
            var all = Blank(name) ?? "전체 모니터";
            return size == null ? all : all + " · " + size;
        }
        var side = Blank(place);
        if (side == null)
        {
            var fallback = Blank(name);
            if (fallback == null) return size ?? "";
            return size == null ? fallback : fallback + " · " + size;
        }
        var head = primary ? side + " · 기본" : side;
        return size == null ? head : head + " · " + size;
    }

    public static string ConnectionFailure(Exception exception, bool duringSession)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException http)
            {
                if (http.StatusCode == HttpStatusCode.Unauthorized)
                    return duringSession ? "서버에서 연결을 끊었습니다. 다시 접속하세요." : "비밀번호가 올바르지 않습니다.";
                if (http.StatusCode == HttpStatusCode.Conflict) return "이미 다른 기기가 연결되어 있습니다.";
                if (http.StatusCode == HttpStatusCode.TooManyRequests) return "접속 시도가 제한되었습니다. 잠시 후 다시 시도하세요.";
                var detail = http.InnerException?.Message ?? http.Message;
                if (detail.Contains("SSL", StringComparison.OrdinalIgnoreCase) || detail.Contains("certificate", StringComparison.OrdinalIgnoreCase) || detail.Contains("인증서", StringComparison.Ordinal))
                    return "서버 인증서 지문이 일치하지 않습니다. 지문을 비우면 비밀번호로 다시 확인합니다.";
                if (http.StatusCode is null)
                    return duringSession ? "서버와 연결이 끊어졌습니다. 주소, 포트, 네트워크를 확인하세요." : "서버에 연결할 수 없습니다. 주소, 포트, 네트워크를 확인하세요.";
            }
            if (current is TaskCanceledException or TimeoutException)
                return duringSession ? "서버 응답이 끊어졌습니다. 다시 접속하세요." : "서버 응답이 없습니다. 주소와 포트를 확인하세요.";
        }
        return Explain(exception, duringSession ? "서버와 연결이 끊어졌습니다. 다시 접속하세요." : "서버에 연결할 수 없습니다. 주소, 포트, 네트워크를 확인하세요.");
    }

    public enum DropKind { Network, Timeout, Rejected, Certificate, Occupied, Limited, Other }

    public const int MaxAutomaticReconnects = 3;

    public static DropKind ClassifyDrop(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException http)
            {
                if (CertificateMismatch(http)) return DropKind.Certificate;
                if (http.StatusCode == HttpStatusCode.Unauthorized) return DropKind.Rejected;
                if (http.StatusCode == HttpStatusCode.Conflict) return DropKind.Occupied;
                if (http.StatusCode == HttpStatusCode.TooManyRequests) return DropKind.Limited;
                if (http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) return DropKind.Timeout;
                if (http.StatusCode is null) return DropKind.Network;
                return DropKind.Other;
            }
            if (current is TimeoutException or TaskCanceledException) return DropKind.Timeout;
        }
        return DropKind.Other;
    }

    public static bool ShouldReconnect(DropKind kind) => kind is DropKind.Network or DropKind.Timeout;

    public static bool CanResume(DropKind kind) => ShouldReconnect(kind) || kind == DropKind.Occupied;

    public static bool ReplaceQuietSession(TimeSpan quiet) => quiet >= TimeSpan.FromSeconds(5);

    public static bool HostShowsQuiet(TimeSpan sinceSeen) => ReplaceQuietSession(sinceSeen);

    public static TimeSpan ReconnectDelay(int attempt) => TimeSpan.FromSeconds(attempt <= 1 ? 2 : attempt == 2 ? 4 : 8);

    public static string Reconnecting(int attempt, int maximum, int seconds)
        => "연결이 끊겼습니다. " + seconds + "초 후 다시 연결합니다. (" + attempt + "/" + maximum + ")";

    public static string ReconnectHold() => "다시 연결하는 중…";

    public static bool ReconnectNotice(string? text)
        => text == ReconnectHold() || (text != null && text.Contains("초 후 다시 연결합니다.", StringComparison.Ordinal));

    public static string PasswordReveal(bool shown) => shown ? "숨김" : "보기";

    public static string PasswordRevealTip(bool shown) => shown ? "비밀번호 숨기기" : "비밀번호 보기";

    public static string CopiedLabel(string idle, bool copied) => copied ? "복사됨" : idle;

    public static string CapsLockWarning() => "Caps Lock이 켜져 있습니다.";

    public static bool ShowCapsLock(bool passwordFocused, bool capsLock) => passwordFocused && capsLock;

    public static bool SelectEntireValue(bool alreadyFocused) => !alreadyFocused;

    public static bool NoticeFits(string? text, int maxLines, int charsPerLine)
    {
        if (maxLines < 1 || charsPerLine < 8 || string.IsNullOrEmpty(text)) return false;
        var lines = 1;
        var column = 0;
        foreach (var character in text)
        {
            if (character == (char)10 || column == charsPerLine)
            {
                lines++;
                column = character == (char)10 ? 0 : 1;
            }
            else column++;
        }
        return lines <= maxLines;
    }

    public static string? RemoteFailure(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var text = body.Trim();
        if (text.Length > 240) text = text[..240];
        if (text.IndexOfAny(new[] { '\n', '\r', '<' }) >= 0) return null;
        if (HasHangul(text)) return text;
        if (text.Contains("Invalid clipboard", StringComparison.OrdinalIgnoreCase) || text.Contains("File hash mismatch", StringComparison.OrdinalIgnoreCase) || text.Contains("File size mismatch", StringComparison.OrdinalIgnoreCase))
            return text;
        return null;
    }

    private static bool CertificateMismatch(HttpRequestException http)
    {
        var detail = http.InnerException?.Message ?? http.Message;
        return detail.Contains("SSL", StringComparison.OrdinalIgnoreCase) || detail.Contains("certificate", StringComparison.OrdinalIgnoreCase) || detail.Contains("인증서", StringComparison.Ordinal);
    }

    private static bool HasHangul(string text)
    {
        foreach (var character in text)
            if (character is >= '가' and <= '힣') return true;
        return false;
    }
}
