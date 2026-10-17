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
    public async Task Login(string password)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var result = await http.PostAsJsonAsync("login", new { password }, timeout.Token);
        result.EnsureSuccessStatusCode();
        var json = await result.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        http.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("token").GetString());
    }
    public async Task Input(InputEvent input, CancellationToken ct = default)
    { using var response = await http.PostAsJsonAsync("input", input, ct); response.EnsureSuccessStatusCode(); }
    public Task<RemoteDisplay[]?> Displays() => http.GetFromJsonAsync<RemoteDisplay[]>("displays");
    public async Task<byte[]> Frame(CancellationToken ct, string? display = null, int quality = 55)
        => await Frame(ct, display, quality, 0, 0, 1, 1);
    public async Task<byte[]> Frame(CancellationToken ct, string? display, int quality, double viewX, double viewY, double viewWidth, double viewHeight)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var view = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{viewX:0.#####},{viewY:0.#####},{viewWidth:0.#####},{viewHeight:0.#####}");
        var query = (display == null ? "?quality=" + quality : "?display=" + Uri.EscapeDataString(display) + "&quality=" + quality) + "&view=" + view;
        return await http.GetByteArrayAsync("frame" + query, timeout.Token);
    }
    public async Task<RemoteFrame> FrameDetailed(CancellationToken ct, string? display, int quality, int fps, bool localCursor, bool viewerHasFrame = false, string? cursorShape = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var query = (display == null ? "?quality=" + quality : "?display=" + Uri.EscapeDataString(display) + "&quality=" + quality) + "&view=0,0,1,1&fps=" + fps + (localCursor ? "&cursor=local" : "") + (viewerHasFrame ? "&cached=1" : "") + (localCursor && !string.IsNullOrEmpty(cursorShape) ? "&shape=" + Uri.EscapeDataString(cursorShape) : "");
        using var response = await http.GetAsync("frame" + query, timeout.Token);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        return new RemoteFrame(bytes, Header(response, "X-Capture-Ms"), Header(response, "X-Encode-Ms"), HeaderFlag(response, "X-Frame-Reused"), (int)Header(response, "X-Jpeg-Quality"), Header(response, "X-Cursor-X"), Header(response, "X-Cursor-Y"), HeaderFlag(response, "X-Cursor-Local"), HeaderFlag(response, "X-Cursor-Visible"), (int)Header(response, "X-Cursor-Hot-X"), (int)Header(response, "X-Cursor-Hot-Y"), HeaderText(response, "X-Cursor-Hash"), HeaderBytes(response, "X-Cursor-Png"));
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
        using var response = await http.PutAsJsonAsync("clipboard/text", new ClipboardTextUpdate(origin, expectedRevision, text), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClipboardManifest>(ct) ?? throw new IOException("클립보드 응답 오류");
    }
    public async Task<ClipboardManifest> SendClipboard(string[] paths, string origin, string expectedRevision, CancellationToken ct, Action<string>? progress = null)
    {
        var files = ClipboardPaths.Accept(paths);
        using var begin = await http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload(origin, expectedRevision, files), ct); begin.EnsureSuccessStatusCode();
        var json = await begin.Content.ReadFromJsonAsync<JsonElement>(ct); var id = json.GetProperty("id").GetString()!;
        var committed = false;
        try
        {
            for (var index = 0; index < paths.Length; index++)
            {
                var name = files[index].Name;
                var size = files[index].Size;
                await using var stream = new FileStream(paths[index], FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long hashed = 0;
                var reported = new ProgressMark();
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    incremental.AppendData(buffer, 0, read);
                    hashed += read;
                    ReportClipboard(progress, "클립보드 확인", index, paths.Length, name, hashed, size, reported);
                }
                var hash = Convert.ToHexString(incremental.GetHashAndReset());
                stream.Position = 0;
                reported.Percent = -1;
                var content = new StreamContent(new CountingStream(stream, sent => ReportClipboard(progress, "클립보드 전송", index, paths.Length, name, sent, size, reported)));
                content.Headers.ContentLength = stream.Length;
                using var request = new HttpRequestMessage(HttpMethod.Put, $"clipboard/uploads/{id}/{index}") { Content = content };
                request.Headers.Add("X-Content-SHA256", hash);
                using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            }
            using var commit = await http.PostAsync($"clipboard/uploads/{id}/commit", null, ct); commit.EnsureSuccessStatusCode();
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
            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var expectedSize = response.Content.Headers.ContentLength ?? 0;
            var reported = -1;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = File.Create(temporary))
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[65536]; long total = 0; int read;
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
            if (!string.Equals(expected, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase)) throw new IOException("파일 해시 불일치");
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => http.Dispose();

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
public readonly record struct RemoteFrame(byte[] Bytes, double CaptureMilliseconds, double EncodeMilliseconds, bool Reused, int Quality, double CursorX, double CursorY, bool LocalCursor, bool CursorVisible, int CursorHotX, int CursorHotY, string CursorHash, byte[]? CursorPng);
