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
