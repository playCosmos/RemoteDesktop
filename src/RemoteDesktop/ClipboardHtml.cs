using System.Net;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteDesktop;

public static class ClipboardHtml
{
    public static byte[] Describe(string fragment)
    {
        fragment ??= "";
        var prefix = "<html>\r\n<body>\r\n<!--StartFragment-->";
        var suffix = "<!--EndFragment-->\r\n</body>\r\n</html>";
        var body = prefix + fragment + suffix;
        var startHtml = 105;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(body);
        var header = "Version:0.9\r\n"
            + "StartHTML:" + startHtml.ToString("D10") + "\r\n"
            + "EndHTML:" + endHtml.ToString("D10") + "\r\n"
            + "StartFragment:" + startFragment.ToString("D10") + "\r\n"
            + "EndFragment:" + endFragment.ToString("D10") + "\r\n";
        if (Encoding.UTF8.GetByteCount(header) != startHtml) throw new InvalidOperationException("HTML clipboard header size changed.");
        return Encoding.UTF8.GetBytes(header + body);
    }

    public static string Fragment(byte[] utf8)
    {
        if (utf8 == null || utf8.Length == 0) return "";
        var header = Encoding.ASCII.GetString(utf8, 0, Math.Min(utf8.Length, 512));
        if (header.StartsWith("Version:", StringComparison.Ordinal)
            && TryOffset(header, "StartFragment", out var start) && TryOffset(header, "EndFragment", out var end)
            && start >= 0 && end >= start && end <= utf8.Length)
            return Encoding.UTF8.GetString(utf8, start, end - start);
        if (header.StartsWith("Version:", StringComparison.Ordinal)
            && TryOffset(header, "StartHTML", out var htmlStart) && TryOffset(header, "EndHTML", out var htmlEnd)
            && htmlStart >= 0 && htmlEnd > htmlStart && htmlEnd <= utf8.Length)
            return Encoding.UTF8.GetString(utf8, htmlStart, htmlEnd - htmlStart);
        var text = Encoding.UTF8.GetString(utf8);
        return text.Contains('<', StringComparison.Ordinal) ? text : "";
    }

    public static byte[] Bytes(object? value)
    {
        switch (value)
        {
            case byte[] bytes: return bytes;
            case MemoryStream stream: return stream.ToArray();
            case Stream stream:
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
            case string text: return Encoding.UTF8.GetBytes(text);
            default: return [];
        }
    }

    public static string Plain(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.Length > ClipboardPaths.MaxHtmlCharacters) return "";
        var text = new StringBuilder(html.Length);
        var index = 0;
        while (index < html.Length)
        {
            if (html[index] != '<') { text.Append(html[index]); index++; continue; }
            var end = html.IndexOf('>', index + 1);
            if (end < 0) break;
            var name = TagName(html.Substring(index + 1, end - index - 1));
            if (name is "br" or "/p" or "/div" or "/li" or "/tr" or "/h1" or "/h2" or "/h3" or "/h4" or "/h5" or "/h6") text.Append('\n');
            if (name is "script" or "style")
            {
                var closer = "</" + name;
                var next = html.IndexOf(closer, end + 1, StringComparison.OrdinalIgnoreCase);
                if (next < 0) break;
                var after = html.IndexOf('>', next + closer.Length);
                index = after < 0 ? html.Length : after + 1;
                continue;
            }
            index = end + 1;
        }
        var decoded = WebUtility.HtmlDecode(text.ToString()).Replace('\u00A0', ' ');
        decoded = Regex.Replace(decoded, "[ \\t\\f\\v]+", " ");
        decoded = Regex.Replace(decoded, " *\\r?\\n *", "\n");
        decoded = Regex.Replace(decoded, "\\n{3,}", "\n\n");
        return decoded.Trim();
    }

    private static string TagName(string raw)
    {
        var body = raw.Trim();
        if (body.EndsWith('/')) body = body[..^1].TrimEnd();
        var space = body.IndexOfAny([' ', '\t', '\r', '\n']);
        if (space >= 0) body = body[..space];
        return body.ToLowerInvariant();
    }

    private static bool TryOffset(string header, string name, out int offset)
    {
        offset = -1;
        var marker = name + ":";
        var at = header.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        at += marker.Length;
        var end = at;
        while (end < header.Length && char.IsDigit(header[end])) end++;
        return end > at && int.TryParse(header.AsSpan(at, end - at), out offset);
    }
}
