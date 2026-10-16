using System.IO;

namespace RemoteDesktop;

public sealed class ClipboardSync(RemoteClient remote, IFileClipboard local, Action<string> status, string? cachePath = null)
{
    private readonly SemaphoreSlim mutex = new(1);
    private readonly string origin = Guid.NewGuid().ToString("N");
    private readonly string cache = cachePath ?? Path.Combine(HostSettings.DataDirectory, "ClipboardCache", "client");
    private uint observedLocal;
    private uint? uploadedLocal;
    private string remoteRevision = "";
    private volatile bool autoEnabled = true;
    public bool AutoEnabled { get => autoEnabled; set => autoEnabled = value; }
    public async Task Initialize(CancellationToken ct)
    { await mutex.WaitAsync(ct); try { observedLocal = (await local.Read(ct)).Sequence; remoteRevision = (await remote.Clipboard(ct)).Id; } finally { mutex.Release(); } }
    public async Task Run(CancellationToken ct)
    {
        var wasEnabled = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!AutoEnabled) { wasEnabled = false; await Task.Delay(300, ct); continue; }
                if (!wasEnabled) { await Initialize(ct); wasEnabled = true; }
                var current = await local.Read(ct);
                if (current.Sequence != observedLocal)
                {
                    if (current.Paths.Length > 0) await Push(ct);
                    else if (current.Text.Length > 0) await PushText(ct);
                    else observedLocal = current.Sequence;
                }
                await Pull(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { status("클립보드: " + ex.Message); }
            await Task.Delay(600, ct);
        }
    }
    public async Task<bool> Push(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var selected = await local.Read(ct);
            if (selected.Paths.Length == 0 || selected.Origin.Length > 0) { observedLocal = selected.Sequence; return false; }
            if (uploadedLocal == selected.Sequence) return true;
            if (selected.Paths.Any(p => !File.Exists(p) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new ArgumentException("일반 파일만 복사할 수 있습니다. 폴더·링크는 지원하지 않습니다.");
            var snapshot = await remote.Clipboard(ct);
            ClipboardManifest result;
            try { result = await remote.SendClipboard(selected.Paths, origin, snapshot.Id, ct, status); }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            { observedLocal = selected.Sequence; throw; }
            observedLocal = selected.Sequence; uploadedLocal = selected.Sequence; remoteRevision = result.Id;
            status("클립보드 파일 준비 완료 · 상대 탐색기에서 붙여넣으세요."); return true;
        }
        finally { mutex.Release(); }
    }
    public async Task<bool> PushAny(CancellationToken ct)
    {
        var selected = await local.Read(ct);
        return selected.Paths.Length > 0 ? await Push(ct) : selected.Text.Length > 0 ? await PushText(ct) : false;
    }
    public async Task<bool> PushText(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var selected = await local.Read(ct);
            if (selected.Paths.Length != 0 || selected.Origin.Length > 0 || selected.Text.Length == 0) { observedLocal = selected.Sequence; return false; }
            ClipboardPaths.ValidateText(selected.Text);
            if (uploadedLocal == selected.Sequence) return true;
            var snapshot = await remote.Clipboard(ct);
            ClipboardManifest result;
            try { result = await remote.SendClipboardText(selected.Text, origin, snapshot.Id, ct); }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            { observedLocal = selected.Sequence; throw; }
            observedLocal = selected.Sequence; uploadedLocal = selected.Sequence; remoteRevision = result.Id;
            status("텍스트 클립보드 동기화 완료"); return true;
        }
        finally { mutex.Release(); }
    }
    public async Task Pull(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var snapshot = await remote.Clipboard(ct);
            if (snapshot.Id == remoteRevision) return;
            uploadedLocal = null;
            if (snapshot.Origin == origin) { remoteRevision = snapshot.Id; return; }
            if (snapshot.Files.Length == 0 && snapshot.Text.Length > 0)
            {
                ClipboardPaths.ValidateText(snapshot.Text);
                var beforeText = await local.Read(ct);
                if (beforeText.Sequence != observedLocal) { remoteRevision = snapshot.Id; return; }
                var published = await local.Write([], snapshot.Text, "remote:" + snapshot.Id, beforeText.Sequence, ct);
                remoteRevision = snapshot.Id;
                if (published) { observedLocal = (await local.Read(ct)).Sequence; status("텍스트 클립보드 수신 완료"); }
                else status("새로 복사한 로컬 클립보드를 유지했습니다.");
                return;
            }
            if (snapshot.Files.Length == 0) { remoteRevision = snapshot.Id; return; }
            ClipboardPaths.Validate(snapshot.Files);
            var before = await local.Read(ct);
            if (before.Sequence != observedLocal) { remoteRevision = snapshot.Id; return; }
            var directory = Path.Combine(cache, Guid.NewGuid().ToString("N")); var retained = false;
            try
            {
                var paths = new string[snapshot.Files.Length];
                for (var index = 0; index < paths.Length; index++)
                {
                    var file = snapshot.Files[index]; paths[index] = ClipboardPaths.FileAt(directory, index, file.Name);
                    status($"클립보드 수신 {index + 1}/{paths.Length}: {file.Name}");
                    await remote.DownloadClipboard(snapshot.Id, index, paths[index], ct);
                    if (new FileInfo(paths[index]).Length != file.Size) throw new IOException("클립보드 파일 크기 불일치");
                }
                if ((await remote.Clipboard(ct)).Id != snapshot.Id) return;
                retained = await local.Write(paths, "", "remote:" + snapshot.Id, before.Sequence, ct);
                remoteRevision = snapshot.Id;
                if (retained) { observedLocal = (await local.Read(ct)).Sequence; status("클립보드 파일 수신 완료 · 이 PC의 탐색기에서 Ctrl+V로 붙여넣으세요."); }
                else status("새로 복사한 로컬 파일을 유지했습니다.");
            }
            finally { if (!retained) ClipboardPaths.DeleteBatch(cache, directory); }
        }
        finally { mutex.Release(); }
    }
}
