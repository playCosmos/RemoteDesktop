using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteDesktop;

System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
var root = Path.Combine(Path.GetTempPath(), "rdc-check-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var settings = new HostSettings();
var salt = RandomNumberGenerator.GetBytes(32);
const string password = "integration-password-123";
settings.Salt = Convert.ToBase64String(salt);
settings.PasswordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, HostSettings.Iterations, HashAlgorithmName.SHA256, 32));
using var rsa = RSA.Create(2048);
var req = new CertificateRequest("CN=integration", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
var pin = Convert.ToHexString(SHA256.HashData(cert.RawData));
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c != null && Convert.ToHexString(SHA256.HashData(c.RawData)) == pin, AllowAutoRedirect = false, ClientCertificateOptions = ClientCertificateOption.Manual, UseProxy = false }) { BaseAddress = new Uri($"https://localhost:{port}/v1/") };
var hostClipboard = new TestFileClipboard();
var hostClipboardCache = Path.Combine(root, "clipboard-host");
await using var host = new RemoteHost(settings, hostClipboard, hostClipboardCache);
try
{
    Check(Desktop.SmoothCapture, "direct host enables stutter reduction from its saved setting");
    Check(cert.HasPrivateKey, "server TLS certificate has a current-user private key");
    await host.StartAsync(port, cert);
    Check(settings.Verify(password) && !settings.Verify("wrong"), "salted password verification");
    var hostPath = Path.Combine(root, "host.json");
    var persistedHost = new HostSettings { Port = 45999 };
    persistedHost.SetPassword(password, hostPath);
    var restartedHost = HostSettings.Load(hostPath);
    Check(restartedHost.HasPassword && restartedHost.Verify(password), "server password works after restart without re-entry");
    Check(restartedHost.Port == 45999, "server port survives restart");
    Check(restartedHost.UseSmoothHost, "stutter reduction stays enabled when the setting is absent");
    File.WriteAllText(hostPath, "{\"Port\":45999}");
    Check(HostSettings.Load(hostPath).UseSmoothHost, "older host settings keep stutter reduction enabled");
    var smoothOff = HostSettings.Load(hostPath);
    smoothOff.SmoothHost = false;
    smoothOff.Save(hostPath);
    Check(!HostSettings.Load(hostPath).UseSmoothHost, "stutter reduction can be turned off");
    Check(!File.ReadAllText(hostPath).Contains(password), "server never persists plaintext password");
    var dataDirectory = Path.Combine(root, "server-data");
    Directory.CreateDirectory(dataDirectory);
    new HostSettings { Port = 46001 }.SetPassword(password, dataDirectory);
    Check(Directory.Exists(dataDirectory) && File.Exists(Path.Combine(dataDirectory, "settings.json")), "password save writes settings.json inside the data directory");
    Check(HostSettings.Load(dataDirectory).Verify(password) && HostSettings.Load(dataDirectory).Port == 46001, "password load accepts the data directory");
    var savedPath = Path.Combine(root, "client.json");
    new ClientSettings { Address = "192.168.1.20:45831", Fingerprint = pin, Display = "all", FrameQuality = 75, FrameRate = 30, ClipboardAutoSync = false }.Save(savedPath);
    var saved = ClientSettings.Load(savedPath);
    Check(saved.Address == "192.168.1.20:45831" && saved.Fingerprint == pin && saved.Display == "all" && saved.FrameQuality == 75 && saved.FrameRate == 30 && !saved.ClipboardAutoSync, "last connection and session preferences survive reload");
    Check(!File.ReadAllText(savedPath).Contains("password", StringComparison.OrdinalIgnoreCase), "connection preferences contain no password");
    File.WriteAllText(savedPath, "invalid json");
    Check(ClientSettings.Load(savedPath).Address == "127.0.0.1:45831", "corrupt preferences recover to defaults");
    var virtualBounds = new System.Drawing.Rectangle(-1920, -1080, 3840, 2160);
    Check(Desktop.MapPoint(virtualBounds, virtualBounds, 0, 0) == (0, 0) && Desktop.MapPoint(virtualBounds, virtualBounds, 1, 1) == (65535, 65535), "whole-desktop corners with negative origin");
    var secondary = new System.Drawing.Rectangle(-1920, -1080, 1920, 1080);
    Check(Desktop.MapPoint(secondary, virtualBounds, 0, 0) == (0, 0) && Desktop.MapPoint(secondary, virtualBounds, 1, 1).X < 32768, "secondary monitor maps into correct half of desktop");
    Check(Desktop.MapPoint(virtualBounds, virtualBounds, -10, 20) == (0, 65535), "out-of-view dragging clamps to desktop bounds");
    try { Desktop.MapPoint(virtualBounds, virtualBounds, double.NaN, 0); Check(false, "invalid coordinates"); } catch (ArgumentException) { Check(true, "nonfinite coordinates rejected"); }
    using (var response = await http.GetAsync("frame")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "screen requires authentication");
    using (var response = await http.GetAsync("displays")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "monitor list requires authentication");
    using (var response = await http.GetAsync("clipboard")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "file clipboard requires authentication");
    using (var response = await http.PutAsJsonAsync("clipboard/text", new ClipboardTextUpdate("test", "", "unauthorized"))) Check(response.StatusCode == HttpStatusCode.Unauthorized, "text clipboard requires authentication");
    using (var response = await http.PostAsJsonAsync("login", new { password = "wrong" })) Check(response.StatusCode == HttpStatusCode.Unauthorized, "wrong password rejected");
    using (var response = await http.PostAsJsonAsync("login", new { password })) Check((int)response.StatusCode == 429, "login rate limit");
    await Task.Delay(2100);
    using (var response = await http.PostAsJsonAsync("login", new { password }))
    {
        Check(response.IsSuccessStatusCode, "password login without host approval");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.GetProperty("token").GetString());
    }
    using (var response = await http.GetAsync("files")) Check(response.StatusCode == HttpStatusCode.NotFound, "legacy shared-folder API removed");
    using (var response = await http.GetAsync("health")) Check(response.StatusCode == HttpStatusCode.NotFound, "unused health probe removed");
    using (var response = await http.PostAsJsonAsync("input", new { kind = "key", code = 9999 })) Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid keyboard event rejected");
    using (var response = await http.GetAsync("frame?display=missing-monitor")) Check(response.StatusCode == HttpStatusCode.BadRequest, "missing monitor rejected before capture");
    using (var response = await http.PostAsJsonAsync("input", new { kind = "move", display = "missing-monitor", x = 0.5, y = 0.5 })) Check(response.StatusCode == HttpStatusCode.BadRequest, "missing monitor rejected before pointer injection");
    using (var body = new StringContent(new string('x', 40000)))
    using (var response = await http.PostAsync("input", body)) Check(response.StatusCode == HttpStatusCode.BadRequest, "oversized control message rejected");
    var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
    var clipBefore = (await http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!;
    var stagedFiles = new[] { new ClipboardFile("한글 복사.bin", payload.Length) };
    async Task<string> BeginClipboard(ClipboardFile[] files, string revision)
    {
        using var response = await http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test-client", revision, files)); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
    async Task<HttpResponseMessage> SendClipboardBytes(string id, byte[] bytes, string hash)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"clipboard/uploads/{id}/0") { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("X-Content-SHA256", hash); return await http.SendAsync(request);
    }
    var clipBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    using (var response = await SendClipboardBytes(clipBatch, payload, new string('0',64))) Check(response.StatusCode == HttpStatusCode.BadRequest, "clipboard rejects corrupt file hash");
    using (var response = await http.PostAsync($"clipboard/uploads/{clipBatch}/commit", null)) Check(response.StatusCode == HttpStatusCode.Conflict, "partial clipboard batch cannot publish");
    Check((await hostClipboard.Read(CancellationToken.None)).Paths.Length == 0, "failed clipboard upload leaves clipboard untouched");
    using (var response = await SendClipboardBytes(clipBatch, payload, Convert.ToHexString(SHA256.HashData(payload)))) Check(response.IsSuccessStatusCode, "clipboard upload verifies bytes");
    using (var response = await http.PostAsync($"clipboard/uploads/{clipBatch}/commit", null)) Check(response.IsSuccessStatusCode, "complete clipboard batch publishes atomically");
    var committedPaths = (await hostClipboard.Read(CancellationToken.None)).Paths;
    Check(committedPaths.Length == 1 && File.ReadAllBytes(committedPaths[0]).SequenceEqual(payload), "clipboard points to received file, not a remote path");
    Check(Path.GetFileName(committedPaths[0]) == "한글 복사.bin", "clipboard preserves Unicode filename");
    clipBefore = (await http.GetFromJsonAsync<ClipboardManifest>("clipboard"))!;
    var cancelledBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    using (var response = await http.DeleteAsync($"clipboard/uploads/{cancelledBatch}")) Check(response.IsSuccessStatusCode && !Directory.Exists(Path.Combine(hostClipboardCache,cancelledBatch)), "cancel cleans staged clipboard files");
    using (var response = await http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test",clipBefore.Id,[new("../escape.bin",1)]))) Check(response.StatusCode == HttpStatusCode.BadRequest, "clipboard filename traversal rejected");
    using (var response = await http.PostAsJsonAsync("clipboard/uploads", new ClipboardUpload("test",clipBefore.Id,[new("oversize.bin",RemoteHost.MaxFileBytes+1)]))) Check(response.StatusCode == HttpStatusCode.BadRequest, "clipboard file size limit enforced before upload");
    var staleBatch = await BeginClipboard(stagedFiles, clipBefore.Id);
    using (var response = await SendClipboardBytes(staleBatch, payload, Convert.ToHexString(SHA256.HashData(payload)))) response.EnsureSuccessStatusCode();
    hostClipboard.Set([]);
    using (var response = await http.PostAsync($"clipboard/uploads/{staleBatch}/commit", null)) Check(response.StatusCode == HttpStatusCode.Conflict, "new host copy is not overwritten by an older transfer");
    using (var response = await http.DeleteAsync($"clipboard/uploads/{staleBatch}")) response.EnsureSuccessStatusCode();
    await Task.Delay(2100);
    using (var response = await http.PostAsJsonAsync("login", new { password })) Check(response.StatusCode == HttpStatusCode.Conflict, "only one active session");
    host.Disconnect();
    using (var response = await http.GetAsync("ping")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "host disconnect revokes token");
    using var wrongPinClient = new RemoteClient($"localhost:{port}", new string('0',64));
    try { await wrongPinClient.Login(password); Check(false,"wrong certificate pin"); } catch (HttpRequestException) { Check(true,"wrong certificate pin rejected"); }
    await Task.Delay(2100);
    using var client = new RemoteClient($"localhost:{port}", pin);
    await client.Login(password);
    var source = Path.Combine(root, "client.bin"); await File.WriteAllBytesAsync(source, payload);
    var localClipboard = new TestFileClipboard();
    var localClipboardCache = Path.Combine(root,"clipboard-client");
    var sync = new ClipboardSync(client,localClipboard,_ => { },localClipboardCache);
    await sync.Initialize(CancellationToken.None);
    localClipboard.Set([source]);
    Check(await sync.Push(CancellationToken.None), "local file copy pushes to remote clipboard");
    var pushed = await hostClipboard.Read(CancellationToken.None);
    Check(pushed.Paths.Length == 1 && File.ReadAllBytes(pushed.Paths[0]).SequenceEqual(payload), "local-to-remote clipboard roundtrip");
    var localSequence = (await localClipboard.Read(CancellationToken.None)).Sequence;
    await sync.Pull(CancellationToken.None);
    Check((await localClipboard.Read(CancellationToken.None)).Sequence == localSequence, "clipboard origin suppresses echo loop");
    var copyA = Path.Combine(root,"copy-a"); var copyB = Path.Combine(root,"copy-b"); Directory.CreateDirectory(copyA); Directory.CreateDirectory(copyB);
    var sameA = Path.Combine(copyA,"같은 이름.txt"); var sameB = Path.Combine(copyB,"같은 이름.txt");
    File.WriteAllText(sameA,"첫 번째"); File.WriteAllText(sameB,"두 번째");
    hostClipboard.Set([sameA,sameB]);
    var remoteManifest = await client.Clipboard(CancellationToken.None);
    Check(remoteManifest.Files.Length == 2 && !JsonSerializer.Serialize(remoteManifest).Contains(root), "clipboard manifest hides source paths");
    await sync.Pull(CancellationToken.None);
    var receivedClipboard = await localClipboard.Read(CancellationToken.None);
    Check(receivedClipboard.Paths.Length == 2 && File.ReadAllText(receivedClipboard.Paths[0]) == "첫 번째" && File.ReadAllText(receivedClipboard.Paths[1]) == "두 번째", "remote-to-local clipboard preserves duplicate basenames");
    Check(!await sync.Push(CancellationToken.None), "received clipboard is not re-uploaded");
    var executableSource = Path.Combine(root, "remote-tool.exe");
    var executableBytes = payload.ToArray(); executableBytes[0] = (byte)'M'; executableBytes[1] = (byte)'Z';
    await File.WriteAllBytesAsync(executableSource, executableBytes);
    localClipboard.Set([executableSource]);
    Check(await sync.Push(CancellationToken.None), "executable extension can enter clipboard transfer");
    var receivedExecutable = await hostClipboard.Read(CancellationToken.None);
    Check(Path.GetFileName(receivedExecutable.Paths.Single()) == "remote-tool.exe" && File.ReadAllBytes(receivedExecutable.Paths.Single()).SequenceEqual(executableBytes), "executable name and bytes reach remote clipboard unchanged");
    hostClipboard.Set([source]);
    localClipboard.BeforeWrite = () => localClipboard.Set([sameA]);
    await sync.Pull(CancellationToken.None);
    Check((await localClipboard.Read(CancellationToken.None)).Paths.SequenceEqual([sameA]), "local copy during download wins over stale remote clipboard");
    try { await client.DownloadClipboard(remoteManifest.Id,0,Path.Combine(root,"stale.bin"),CancellationToken.None); Check(false,"stale clipboard"); }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { Check(true,"old clipboard snapshot cannot read new files"); }
    localClipboard.Set([], "클라이언트에서 복사한 한글 텍스트\r\n두 번째 줄");
    Check(await sync.PushText(CancellationToken.None), "local text copy pushes to remote clipboard");
    var hostText = await hostClipboard.Read(CancellationToken.None);
    Check(hostText.Paths.Length == 0 && hostText.Text == "클라이언트에서 복사한 한글 텍스트\r\n두 번째 줄", "local-to-remote Unicode text clipboard roundtrip");
    var textSequence = hostText.Sequence;
    await sync.Pull(CancellationToken.None);
    Check((await hostClipboard.Read(CancellationToken.None)).Sequence == textSequence, "text clipboard origin suppresses echo loop");
    hostClipboard.Set([], "서버에서 복사한 text ✓");
    await sync.Pull(CancellationToken.None);
    var receivedText = await localClipboard.Read(CancellationToken.None);
    Check(receivedText.Paths.Length == 0 && receivedText.Text == "서버에서 복사한 text ✓", "remote-to-local text clipboard roundtrip");
    Check(!await sync.PushText(CancellationToken.None), "received text is not re-uploaded");
    hostClipboard.Set([], "원격 텍스트");
    localClipboard.BeforeWrite = () => localClipboard.Set([], "새 로컬 텍스트");
    await sync.Pull(CancellationToken.None);
    Check((await localClipboard.Read(CancellationToken.None)).Text == "새 로컬 텍스트", "local text copy wins over stale remote text");
    var textManifest = await client.Clipboard(CancellationToken.None);
    try { await client.SendClipboardText("오래된 텍스트", "test", remoteManifest.Id, CancellationToken.None); Check(false, "stale text revision"); }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { Check(true, "stale text revision cannot overwrite newer clipboard"); }
    try { await client.SendClipboardText(new string('x', ClipboardPaths.MaxTextCharacters + 1), "test", textManifest.Id, CancellationToken.None); Check(false, "oversized text"); }
    catch (ArgumentException) { Check(true, "text clipboard size limit enforced before upload"); }
    var longText = new string('가', 40_000);
    await client.SendClipboardText(longText, "test", (await client.Clipboard(CancellationToken.None)).Id, CancellationToken.None);
    Check((await hostClipboard.Read(CancellationToken.None)).Text == longText, "text beyond control-message limit reaches clipboard");
    if (args.Contains("--capture"))
    {
        var jpeg = await client.Frame(CancellationToken.None);
        Check(jpeg.Length > 100 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "desktop capture returns JPEG");
        var lowQuality = await client.Frame(CancellationToken.None, quality: 30);
        var highQuality = await client.Frame(CancellationToken.None, quality: 75);
        using (var lowDecoded = System.Drawing.Image.FromStream(new MemoryStream(lowQuality)))
        using (var highDecoded = System.Drawing.Image.FromStream(new MemoryStream(highQuality)))
            Check(lowDecoded.Width == highDecoded.Width && lowDecoded.Height == highDecoded.Height && lowQuality[0] == 0xFF && highQuality[0] == 0xFF, "quality presets return valid JPEG frames");
        var displays = await client.Displays() ?? [];
        Check(displays.Any(d => d.Id == "all") && displays.Any(d => d.Primary), "monitor API includes virtual desktop and primary display");
        foreach (var display in displays)
        {
            var frame = await client.Frame(CancellationToken.None, display.Id);
            using var decoded = System.Drawing.Image.FromStream(new MemoryStream(frame));
            Check(decoded.Width == display.Width && decoded.Height == display.Height, $"capture dimensions: {display.Name} ({display.Width}x{display.Height})");
        }
        var cropped = await client.Frame(CancellationToken.None, "all", 30, .25, .25, .5, .5);
        using var croppedDecoded = System.Drawing.Image.FromStream(new MemoryStream(cropped));
        Check(croppedDecoded.Width == 1920 && croppedDecoded.Height == 540, "viewport capture reduces requested frame area");
        Desktop.SmoothCapture = true;
        var smoothDesktop = new Desktop();
        var smoothFrame = smoothDesktop.CaptureDetailed(null, 55, 0, 0, 1, 1, 30, true);
        Check(smoothFrame.Bytes.Length > 100 && smoothFrame.Bytes[0] == 0xFF && smoothFrame.Bytes[1] == 0xD8, "smooth capture returns JPEG");
        var repeatedFrame = smoothDesktop.CaptureDetailed(null, 55, 0, 0, 1, 1, 30, true);
        Check(repeatedFrame.Bytes[0] == 0xFF && repeatedFrame.Bytes[1] == 0xD8 && repeatedFrame.LocalCursor, "smooth capture serves a prepared frame");
    }
    Console.WriteLine("Waiting for abandoned-session expiry...");
    await Task.Delay(17000);
    try { await client.Ping(CancellationToken.None); Check(false,"session timeout"); }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { Check(true,"abandoned session expires"); }
    Console.WriteLine($"{passed} integration checks passed.");
}

finally
{
    await host.DisposeAsync();
    // This directory is created above solely for this run.
    Directory.Delete(root, true);
}

sealed class TestFileClipboard : IFileClipboard
{
    private ClipboardSelection value = new(1,"",[]);
    public Action? BeforeWrite;
    public void Set(string[] paths, string text = "", string origin = "") { lock(this) value=new(value.Sequence+1,origin,paths,text); }
    public Task<ClipboardSelection> Read(CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock(this) return Task.FromResult(value); }
    public Task<bool> Write(string[] paths,string text,string origin,uint expectedSequence,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock(this)
        {
            var callback=BeforeWrite; BeforeWrite=null; callback?.Invoke();
            if(value.Sequence!=expectedSequence)return Task.FromResult(false);
            value=new(value.Sequence+1,origin,paths,text); return Task.FromResult(true);
        }
    }
}
