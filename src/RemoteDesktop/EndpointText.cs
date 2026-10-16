namespace RemoteDesktop;

public static class EndpointText
{
    public static bool TrySplit(string? text, out string host, out int port)
    {
        host = "";
        port = 0;
        text = text?.Trim() ?? "";
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        var hostPart = text[..colon].Trim();
        if (hostPart.StartsWith('[') && hostPart.EndsWith(']')) hostPart = hostPart[1..^1];
        if (hostPart.Length == 0 || hostPart.Any(char.IsWhiteSpace) || hostPart.Contains(':') && !text.StartsWith('[')) return false;
        if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        host = hostPart;
        return true;
    }
}
