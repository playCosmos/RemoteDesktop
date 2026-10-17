using System.IO;

namespace RemoteDesktop;

public sealed class ClipboardSync(RemoteClient remote, IFileClipboard local, Action<string> status, string? cachePath = null)
{
    public static class ClipboardClear
    {
        public static bool PushEmpty(int paths, int textLength, bool ownWrite)
            => paths == 0 && textLength == 0 && !ownWrite;

        public static bool ApplyEmpty(int files, int textLength, bool ownOrigin, bool localChanged, bool localAlreadyEmpty)
            => files == 0 && textLength == 0 && !ownOrigin && !localChanged && !localAlreadyEmpty;
    }

    private readonly SemaphoreSlim mutex = new(1);
    private readonly string origin = Guid.NewGuid().ToString("N");
    private readonly string cache = cachePath ?? Path.Combine(HostSettings.DataDirectory, "ClipboardCache", "client");
    private uint observedLocal;
    private uint? uploadedLocal;
    private string remoteRevision = "";
    private string declinedRevision = "";
    private CancellationTokenSource? transfer;
    private volatile bool autoEnabled = true;
    public bool AutoEnabled { get => autoEnabled; set => autoEnabled = value; }
    public bool Transferring { get; private set; }
    public event Action<bool>? TransferringChanged;
    public void CancelTransfer()
    {
        try { transfer?.Cancel(); } catch (ObjectDisposedException) { }
    }
    public async Task Initialize(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var current = await local.Read(ct);
            observedLocal = current.Sequence;
            remoteRevision = (await remote.Clipboard(ct)).Id;
            try { ClipboardPaths.DeleteRetired(cache, current.Paths); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        finally { mutex.Release(); }
    }
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
                    else if (current.Text.Length > 0 || ClipboardClear.PushEmpty(current.Paths.Length, current.Text.Length, current.Origin.Length > 0)) await PushText(ct);
                    else observedLocal = current.Sequence;
                }
                await Pull(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { status("클립보드: " + UiText.Explain(ex, "다시 시도하세요.")); }
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
            ClipboardPaths.Accept(selected.Paths);
            var snapshot = await remote.Clipboard(ct);
            ClipboardManifest result;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transfer = linked;
            SetTransferring(true);
            try
            {
                try { result = await remote.SendClipboard(selected.Paths, origin, snapshot.Id, linked.Token, status); }
                catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                { observedLocal = selected.Sequence; throw; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    observedLocal = selected.Sequence;
                    status(UiText.TransferCancelled());
                    return false;
                }
            }
            finally { FinishTransfer(linked); }
            observedLocal = selected.Sequence; uploadedLocal = selected.Sequence; remoteRevision = result.Id;
            status("클립보드 파일 준비 완료 · 상대 탐색기에서 붙여넣으세요."); return true;
        }
        finally { mutex.Release(); }
    }
    public async Task PushFiles(string[] paths, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            ClipboardPaths.Accept(paths);
            var snapshot = await remote.Clipboard(ct);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transfer = linked;
            SetTransferring(true);
            ClipboardManifest result;
            try
            {
                try { result = await remote.SendClipboard(paths, origin, snapshot.Id, linked.Token, status); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    status(UiText.TransferCancelled());
                    return;
                }
            }
            finally { FinishTransfer(linked); }
            remoteRevision = result.Id;
            status("클립보드 파일 준비 완료 · 상대 탐색기에서 붙여넣으세요.");
        }
        finally { mutex.Release(); }
    }
    public async Task<bool> PushAny(CancellationToken ct)
    {
        var selected = await local.Read(ct);
        if (selected.Paths.Length > 0) return await Push(ct);
        if (selected.Text.Length > 0 || ClipboardClear.PushEmpty(selected.Paths.Length, selected.Text.Length, selected.Origin.Length > 0)) return await PushText(ct);
        return false;
    }
    public async Task<bool> PushText(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var selected = await local.Read(ct);
            var clearing = ClipboardClear.PushEmpty(selected.Paths.Length, selected.Text.Length, selected.Origin.Length > 0);
            if (selected.Paths.Length != 0 || selected.Origin.Length > 0 || (selected.Text.Length == 0 && !clearing)) { observedLocal = selected.Sequence; return false; }
            ClipboardPaths.ValidateText(selected.Text);
            if (uploadedLocal == selected.Sequence) return true;
            var snapshot = await remote.Clipboard(ct);
            ClipboardManifest result;
            try { result = await remote.SendClipboardText(selected.Text, origin, snapshot.Id, ct); }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            { observedLocal = selected.Sequence; throw; }
            observedLocal = selected.Sequence; uploadedLocal = selected.Sequence; remoteRevision = result.Id;
            status(clearing ? UiText.ClipboardCleared() : "텍스트 클립보드 동기화 완료"); return true;
        }
        finally { mutex.Release(); }
    }
    public async Task<bool> Pull(CancellationToken ct, bool manual = false)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var snapshot = await remote.Clipboard(ct);
            if (!manual && snapshot.Id == declinedRevision) return true;
            if (manual) declinedRevision = "";
            if (snapshot.Id == remoteRevision) return true;
            uploadedLocal = null;
            if (snapshot.Origin == origin) { remoteRevision = snapshot.Id; return true; }
            if (snapshot.Files.Length == 0 && snapshot.Text.Length > 0)
            {
                ClipboardPaths.ValidateText(snapshot.Text);
                var beforeText = await local.Read(ct);
                if (beforeText.Sequence != observedLocal) { remoteRevision = snapshot.Id; return true; }
                var published = await local.Write([], snapshot.Text, "remote:" + snapshot.Id, beforeText.Sequence, ct);
                remoteRevision = snapshot.Id;
                if (published) { observedLocal = (await local.Read(ct)).Sequence; status("텍스트 클립보드 수신 완료"); }
                else status("새로 복사한 로컬 클립보드를 유지했습니다.");
                return true;
            }
            if (snapshot.Files.Length == 0)
            {
                var beforeClear = await local.Read(ct);
                var alreadyEmpty = beforeClear.Paths.Length == 0 && beforeClear.Text.Length == 0;
                if (!ClipboardClear.ApplyEmpty(snapshot.Files.Length, snapshot.Text.Length, snapshot.Origin == origin, beforeClear.Sequence != observedLocal, alreadyEmpty))
                { remoteRevision = snapshot.Id; return true; }
                var cleared = await local.Write([], "", "remote:" + snapshot.Id, beforeClear.Sequence, ct);
                remoteRevision = snapshot.Id;
                if (cleared) { observedLocal = (await local.Read(ct)).Sequence; status(UiText.ClipboardCleared()); }
                else status("새로 복사한 로컬 클립보드를 유지했습니다.");
                return true;
            }
            ClipboardPaths.Validate(snapshot.Files);
            var before = await local.Read(ct);
            if (before.Sequence != observedLocal) { remoteRevision = snapshot.Id; return true; }
            var directory = Path.Combine(cache, Guid.NewGuid().ToString("N")); var retained = false;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transfer = linked;
            SetTransferring(true);
            try
            {
                var stored = new string[snapshot.Files.Length];
                for (var index = 0; index < stored.Length; index++)
                {
                    var file = snapshot.Files[index]; stored[index] = ClipboardPaths.FileAt(directory, index, file.Name);
                    var reported = -1;
                    await remote.DownloadClipboard(snapshot.Id, index, stored[index], linked.Token, (sent, size) =>
                    {
                        var percent = size <= 0 ? 100 : (int)Math.Clamp(sent * 100 / size, 0, 100);
                        if (!UiText.ShouldReportProgress(reported, percent)) return;
                        reported = percent;
                        status(UiText.ClipboardFileProgress("클립보드 수신", index, stored.Length, file.Name, sent, size));
                    });
                    if (new FileInfo(stored[index]).Length != file.Size) throw new IOException("클립보드 파일 크기 불일치");
                }
                if ((await remote.Clipboard(linked.Token)).Id != snapshot.Id) return true;
                var published = ClipboardPaths.Publish(directory, snapshot.Files);
                retained = await local.Write(published, "", "remote:" + snapshot.Id, before.Sequence, linked.Token);
                remoteRevision = snapshot.Id;
                if (retained) { observedLocal = (await local.Read(ct)).Sequence; status("클립보드 파일 수신 완료 · 이 PC의 탐색기에서 Ctrl+V로 붙여넣으세요."); }
                else status("새로 복사한 로컬 파일을 유지했습니다.");
                if (retained)
                {
                    try { ClipboardPaths.DeleteRetired(cache, published); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                declinedRevision = snapshot.Id;
                status(UiText.TransferCancelled());
                return false;
            }
            finally
            {
                FinishTransfer(linked);
                if (!retained) ClipboardPaths.DeleteBatch(cache, directory);
            }
        }
        finally { mutex.Release(); }
    }

    private void FinishTransfer(CancellationTokenSource linked)
    {
        if (ReferenceEquals(transfer, linked)) transfer = null;
        linked.Dispose();
        SetTransferring(false);
    }

    private void SetTransferring(bool value)
    {
        if (Transferring == value) return;
        Transferring = value;
        TransferringChanged?.Invoke(value);
    }
}
