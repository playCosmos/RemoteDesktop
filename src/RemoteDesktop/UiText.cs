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
        if (viewing != null && hosting != null) return viewing + " — " + hosting + " 연결됨 · F11로 복귀";
        if (viewing != null) return viewing + " — F11로 복귀";
        return "Personal Remote Desktop · F11로 복귀";
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

    public static string InputBacklog() => "입력이 밀려 눌린 키와 버튼을 놓습니다.";

    public static bool ShowHostSetup(bool serverRunning) => !serverRunning;

    public static string HostCardTitle(bool clientConnected, bool serverLive, string? peer)
    {
        if (clientConnected)
        {
            var name = Blank(peer);
            return name == null ? "이 PC를 보는 중" : name + " 연결 중";
        }
        return serverLive ? "접속 대기 중" : "접속 받기";
    }

    public static string FullscreenAway(string? address)
    {
        var target = Blank(address);
        return target == null ? "F11로 이 창에 되돌립니다." : target + " 화면입니다. F11로 이 창에 되돌립니다.";
    }

    public static bool EdgeToEdge(bool connected, bool clientTabSelected, bool fullscreen) => connected && clientTabSelected && !fullscreen;

    public static string ShareTitle(bool occupied) => occupied ? "지금은 한 대만 연결됩니다" : "이 PC에 접속하려면";

    public static string ShareHint(bool occupied) => occupied
        ? "이미 다른 기기가 연결되어 있습니다. 현재 접속을 끊어야 다른 PC가 들어올 수 있습니다."
        : "상대 PC의 주소 칸에 붙여 넣으면 포트도 같이 들어갑니다. 비밀번호만 맞으면 수락 없이 연결됩니다.";

    public static bool RemotePointer(double stageY, bool captured) => captured || stageY > 20;

    public static string HostPresence(bool clientConnected, bool serverLive) => clientConnected ? "LiveBrush" : serverLive ? "AccentBrush" : "MutedBrush";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsProblem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("실패", StringComparison.Ordinal)
            || text.Contains("오류", StringComparison.Ordinal)
            || text.Contains("중단", StringComparison.Ordinal)
            || text.Contains("초과", StringComparison.Ordinal);
    }

    public static bool OfferMonitorChoice(int listedDisplays) => listedDisplays > 2;

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

    private static bool HasHangul(string text)
    {
        foreach (var character in text)
            if (character is >= '가' and <= '힣') return true;
        return false;
    }
}
