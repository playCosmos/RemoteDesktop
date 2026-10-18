using System.Buffers;
using System.Text;

namespace RemoteDesktop;

internal static class UnicodeInputText
{
    internal const int MaxCodeUnits = 4096;

    internal static bool TryGetCodeUnits(string? text, out char[] codeUnits)
    {
        codeUnits = [];
        if (text is null || text.Length > MaxCodeUnits) return false;
        var buffer = new char[text.Length];
        var encoded = new char[2];
        var offset = 0;
        var output = 0;
        while (offset < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed);
            if (status != OperationStatus.Done) return false;
            var written = rune.EncodeToUtf16(encoded);
            encoded.AsSpan(0, written).CopyTo(buffer.AsSpan(output));
            offset += consumed;
            output += written;
        }
        codeUnits = buffer;
        return true;
    }
}
