using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteDesktop;

public sealed class RemoteClient : IDisposable
{
    private readonly HttpClient http;
    public RemoteClient(string address, string fingerprint)
    {
        var pin = fingerprint.Replace(":", "").Replace(" ", "").Trim();
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new ArgumentException("서버의 SHA-256 지문 64자리를 입력하세요.");
        var uri = new Uri("https://" + address.Trim().TrimEnd('/') + "/v1/");
        if (uri.AbsolutePath != "/v1/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("주소에는 호스트와 포트만 입력하세요.");
        http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null && DateTime.Now >= certificate.NotBefore && DateTime.Now <= certificate.NotAfter &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(pin))
        }) { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(30) };
    }
    public async Task Login(string password, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var result = await http.PostAsJsonAsync("login", new { password }, timeout.Token);
        result.EnsureSuccessStatusCode();
        var json = await result.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        http.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("token").GetString());
    }
    public async Task<string?> Input(InputEvent input, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("input", input, ct);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type == null || type.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0) return null;
        return SecureAttention.ReadNotice(await response.Content.ReadAsStringAsync(ct));
    }
    public async Task<RemoteDisplay[]?> Displays(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await http.GetFromJsonAsync<RemoteDisplay[]>("displays", timeout.Token);
    }
    public async Task<byte[]> Frame(CancellationToken ct, string? display = null, int quality = 55)
        => await Frame(ct, display, quality, 0, 0, 1, 1);
    public async Task<byte[]> Frame(CancellationToken ct, string? display, int quality, double viewX, double viewY, double viewWidth, double viewHeight)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var view = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{viewX:0.#####},{viewY:0.#####},{viewWidth:0.#####},{viewHeight:0.#####}");
        var query = (display == null ? "?quality=" + quality : "?display=" + Uri.EscapeDataString(display) + "&quality=" + quality) + "&view=" + view;
        return await http.GetByteArrayAsync("frame" + query, timeout.Token);
    }
    public async Task<RemoteFrame> FrameDetailed(CancellationToken ct, string? display, int quality, int fps, bool localCursor, bool viewerHasFrame = false, string? cursorShape = null, bool keyframe = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var query = (display == null ? "?quality=" + quality : "?display=" + Uri.EscapeDataString(display) + "&quality=" + quality) + "&view=0,0,1,1&fps=" + fps + (localCursor ? "&cursor=local" : "") + (viewerHasFrame ? "&cached=1" : "") + (localCursor && !string.IsNullOrEmpty(cursorShape) ? "&shape=" + Uri.EscapeDataString(cursorShape) : "") + "&codec=h264" + (keyframe ? "&key=1" : "");
        using var response = await http.GetAsync("frame" + query, timeout.Token);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        return new RemoteFrame(bytes, Header(response, "X-Capture-Ms"), Header(response, "X-Encode-Ms"), HeaderFlag(response, "X-Frame-Reused"), (int)Header(response, "X-Jpeg-Quality"), Header(response, "X-Cursor-X"), Header(response, "X-Cursor-Y"), HeaderFlag(response, "X-Cursor-Local"), HeaderFlag(response, "X-Cursor-Visible"), (int)Header(response, "X-Cursor-Hot-X"), (int)Header(response, "X-Cursor-Hot-Y"), HeaderText(response, "X-Cursor-Hash"), HeaderBytes(response, "X-Cursor-Png"), HeaderText(response, "X-Secure-Desktop"), HeaderText(response, "X-Codec"));
    }

    public static bool SendJpeg(bool viewerHasFrame, bool reused) => !viewerHasFrame || !reused;

    public static TimeSpan FrameInterval(int fps, bool unchanged)
    {
        var active = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps, 1, 30));
        if (!unchanged) return active;
        var idle = TimeSpan.FromMilliseconds(100);
        return active > idle ? active : idle;
    }
    private static double Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) && double.TryParse(values.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static bool HeaderFlag(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() == "1";
    private static string HeaderText(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";
    private static byte[]? HeaderBytes(HttpResponseMessage response, string name)
    {
        var text = HeaderText(response, name);
        if (text.Length == 0) return null;
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }
    public async Task Ping(CancellationToken ct) { using var response = await http.GetAsync("ping", ct); response.EnsureSuccessStatusCode(); }
    public async Task Logout() { using var timeout = new CancellationTokenSource(2000); using var response = await http.PostAsync("logout", null, timeout.Token); }
    public async Task<ClipboardManifest> Clipboard(CancellationToken ct) => await http.GetFromJsonAsync<ClipboardManifest>("clipboard", ct) ?? throw new IOException("클립보드 응답 오류");
    public async Task<ClipboardManifest> SendClipboardText(string text, string origin, string expectedRevision, CancellationToken ct)
    {
        ClipboardPaths.ValidateText(text);
        return await SendClipboardText(text, origin, expectedRevision, ct, "");
    }
    public async Task<ClipboardManifest> SendClipboardText(string text, string origin, string expectedRevision, CancellationToken ct, string? html)
    {
        var formatted = html ?? "";
        ClipboardPaths.ValidateText(text);
        ClipboardPaths.ValidateHtml(formatted);
        using var response = await http.PutAsJsonAsync("clipboard/text", new ClipboardTextUpdate(origin, expectedRevision, text, formatted), ct);
        await EnsureClipboard(response);
        return await response.Content.ReadFromJsonAsync<ClipboardManifest>(ct) ?? throw new IOException("클립보드 응답 오류");
    }
    public async Task<ClipboardManifest> SendClipboardImage(byte[] png, string origin, string expectedRevision, CancellationToken ct)
    {
        ClipboardPaths.ValidateImage(png);
        using var content = new ByteArrayContent(png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var request = new HttpRequestMessage(HttpMethod.Put, "clipboard/image") { Content = content };
        request.Headers.Add("X-Content-SHA256", Convert.ToHexString(SHA256.HashData(png)));
        request.Headers.Add("X-Clipboard-Origin", origin);
        request.Headers.Add("X-Clipboard-Revision", expectedRevision);
        using var response = await http.SendAsync(request, ct);
        await EnsureClipboard(response);
        return await response.Content.ReadFromJsonAsync<ClipboardManifest>(ct) ?? throw new IOException("클립보드 응답 오류");
    }
    public async Task<byte[]> DownloadClipboardImage(string id, CancellationToken ct)
    {
        using var response = await http.GetAsync("clipboard/" + Uri.EscapeDataString(id) + "/image", ct);
        await EnsureClipboard(response);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var expected = response.Headers.GetValues("X-Content-SHA256").Single();
        if (!string.Equals(expected, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase)) throw new IOException("파일 해시 불일치");
        ClipboardPaths.ValidateImage(bytes);
        return bytes;
    }
    public async Task<string> DownloadClipboardHtml(string id, CancellationToken ct)
    {
        using var response = await http.GetAsync("clipboard/" + Uri.EscapeDataString(id) + "/html", ct);
        await EnsureClipboard(response);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var expected = response.Headers.GetValues("X-Content-SHA256").Single();
        if (!string.Equals(expected, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase)) throw new IOException("파일 해시 불일치");
        var html = System.Text.Encoding.UTF8.GetString(bytes);
        ClipboardPaths.ValidateHtml(html);
        return html;
    }
    public async Task<ClipboardManifest> SendClipboard(string[] paths, string origin, string expectedRevision, CancellationToken ct, Action<string>? progress = null)
    {
        var leaves = ClipboardPaths.Collect(paths);
        var files = leaves.Select(leaf => new ClipboardFile(leaf.Name, leaf.Size, leaf.Directory)).ToArray();
        using var begin = await http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload(origin, expectedRevision, files), ct);
        await EnsureClipboard(begin);
        var json = await begin.Content.ReadFromJsonAsync<JsonElement>(ct); var id = json.GetProperty("id").GetString()!;
        var committed = false;
        try
        {
            for (var index = 0; index < leaves.Length; index++)
            {
                if (leaves[index].Directory) continue;
                var name = leaves[index].Name;
                var size = leaves[index].Size;
                await using var stream = new FileStream(leaves[index].Source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long hashed = 0;
                var reported = new ProgressMark();
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    incremental.AppendData(buffer, 0, read);
                    hashed += read;
                    ReportClipboard(progress, "클립보드 확인", index, leaves.Length, name, hashed, size, reported);
                }
                var hash = Convert.ToHexString(incremental.GetHashAndReset());
                long offset = 0;
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (attempt > 1)
                    {
                        var probed = await ClipboardReceived(id, index, ct);
                        if (probed < offset || probed > size || (probed == offset && attempt > 2)) throw new IOException("클립보드 전송을 이어받지 못했습니다.");
                        offset = probed;
                        if (offset == size) break;
                    }
                    stream.Position = offset;
                    reported.Percent = -1;
                    var action = offset > 0 ? "클립보드 이어받기" : "클립보드 전송";
                    var content = new StreamContent(new CountingStream(stream, sent => ReportClipboard(progress, action, index, leaves.Length, name, offset + sent, size, reported)));
                    content.Headers.ContentLength = size - offset;
                    using var request = new HttpRequestMessage(HttpMethod.Put, $"clipboard/uploads/{id}/{index}") { Content = content };
                    request.Headers.Add("X-Content-SHA256", hash);
                    request.Headers.Add("X-Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    HttpResponseMessage response;
                    try { response = await http.SendAsync(request, ct); }
                    catch (HttpRequestException) when (attempt < 4) { continue; }
                    using (response)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Received-Bytes", out var values) && long.TryParse(System.Linq.Enumerable.Single(values), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var received) && received > offset && received <= size)
                        { offset = received; continue; }
                        await EnsureClipboard(response);
                        offset = size;
                        break;
                    }
                }
                if (offset != size) throw new IOException("클립보드 전송을 이어받지 못했습니다.");
            }
            using var commit = await http.PostAsync($"clipboard/uploads/{id}/commit", null, ct);
            await EnsureClipboard(commit);
            var result = await commit.Content.ReadFromJsonAsync<ClipboardManifest>(ct) ?? throw new IOException("클립보드 응답 오류"); committed = true; return result;
        }
        finally
        {
            if (!committed) { try { using var timeout = new CancellationTokenSource(3000); using var result = await http.DeleteAsync($"clipboard/uploads/{id}", timeout.Token); } catch { } }
        }
    }
    public Task DownloadClipboard(string id, int index, string target, CancellationToken ct, Action<long, long>? progress = null) => DownloadPath($"clipboard/{Uri.EscapeDataString(id)}/{index}", target, ct, progress);
    private async Task DownloadPath(string path, string target, CancellationToken ct, Action<long, long>? progress)
    {
        var temporary = target + "." + Guid.NewGuid() + ".partial";
        try
        {
            long lastLength = -1;
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var have = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
                if (attempt > 2 && have <= lastLength) throw new IOException("클립보드 전송을 이어받지 못했습니다.");
                lastLength = have;
                try
                {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                    lastLength = -1;
                    continue;
                }
                await EnsureClipboard(response);
                var partial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                var start = partial ? response.Content.Headers.ContentRange?.From ?? have : 0;
                var expectedSize = partial ? response.Content.Headers.ContentRange?.Length ?? start + (response.Content.Headers.ContentLength ?? 0) : response.Content.Headers.ContentLength ?? 0;
                var reported = -1;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                if (partial && File.Exists(temporary))
                {
                    await using var prefix = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    var prefixBuffer = new byte[65536]; int prefixRead;
                    while ((prefixRead = await prefix.ReadAsync(prefixBuffer, ct)) > 0) hash.AppendData(prefixBuffer, 0, prefixRead);
                }
                await using (var output = new FileStream(temporary, partial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[65536]; long total = partial ? start : 0; int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > RemoteHost.MaxFileBytes) throw new IOException("최대 파일 크기 초과");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        if (progress != null && UiText.ShouldReportProgress(reported, Percent(total, expectedSize)))
                        {
                            reported = Percent(total, expectedSize);
                            progress(total, expectedSize);
                        }
                    }
                }
                var expected = response.Headers.GetValues("X-Content-SHA256").Single();
                if (!string.Equals(expected, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                    throw new IOException("파일 해시 불일치");
                }
                File.Move(temporary, target, false);
                return;
                }
                catch (HttpRequestException ex) when (attempt < 4 && !ct.IsCancellationRequested && ex.StatusCode is null or System.Net.HttpStatusCode.RequestTimeout) { continue; }
                catch (IOException ex) when (attempt < 4 && !ct.IsCancellationRequested && ex.Message != "파일 해시 불일치" && ex.Message != "최대 파일 크기 초과") { continue; }
            }
            throw new IOException("클립보드 전송을 이어받지 못했습니다.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => http.Dispose();
    private async Task<long> ClipboardReceived(string id, int index, CancellationToken ct)
    {
        using var response = await http.GetAsync($"clipboard/uploads/{id}/{index}", ct);
        await EnsureClipboard(response);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("received").GetInt64();
    }

    private static async Task EnsureClipboard(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = "";
        try { body = await response.Content.ReadAsStringAsync(); }
        catch (Exception) { }
        var detail = UiText.RemoteFailure(body);
        if (detail != null) throw new HttpRequestException(detail, null, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    private static void ReportClipboard(Action<string>? progress, string action, int index, int count, string name, long sent, long size, ProgressMark reported)
    {
        if (progress == null) return;
        var percent = Percent(sent, size);
        if (!UiText.ShouldReportProgress(reported.Percent, percent)) return;
        reported.Percent = percent;
        progress(UiText.ClipboardFileProgress(action, index, count, name, sent, size));
    }

    private static int Percent(long sent, long size) => size <= 0 ? 100 : (int)Math.Clamp(sent * 100 / size, 0, 100);

    private sealed class ProgressMark { public int Percent = -1; }

    private sealed class CountingStream(Stream inner, Action<long> progress) : Stream
    {
        private long sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            if (n > 0) { sent += n; progress(sent); }
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            if (n > 0) { sent += n; progress(sent); }
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
public readonly record struct RemoteFrame(byte[] Bytes, double CaptureMilliseconds, double EncodeMilliseconds, bool Reused, int Quality, double CursorX, double CursorY, bool LocalCursor, bool CursorVisible, int CursorHotX, int CursorHotY, string CursorHash, byte[]? CursorPng, string Secure = "", string Codec = "");
