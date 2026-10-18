using System.IO;
using System.Net.Http;

namespace RemoteDesktop;

public static class RemoteResponseBounds
{
    public static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var length = content.Headers.ContentLength;
        if (length is < 0 || length > maxBytes) throw new IOException("서버 응답이 허용 크기를 초과했습니다.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await ReadBoundedAsync(stream, maxBytes, cancellationToken);
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length > maxBytes - read) throw new IOException("서버 응답이 허용 크기를 초과했습니다.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
