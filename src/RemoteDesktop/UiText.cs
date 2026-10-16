using System.Net.Sockets;

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
            if (HasHangul(message)) return message;
        }
        return fallback;
    }

    public static string ShortSecret(string? value)
    {
        value = value?.Trim() ?? "";
        return value.Length <= 16 ? value : value[..16] + "…";
    }

    private static bool HasHangul(string text)
    {
        foreach (var character in text)
            if (character is >= '가' and <= '힣') return true;
        return false;
    }
}
