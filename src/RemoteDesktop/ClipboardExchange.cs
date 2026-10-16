using System.IO;
using System.Security.Cryptography;

namespace RemoteDesktop;

/// <summary>Session-scoped snapshots and staged clipboard files. Remote paths are never accepted.</summary>
public sealed class ClipboardExchange(IFileClipboard clipboard, string cacheRoot)
{
    private readonly SemaphoreSlim mutex = new(1);
    private string owner = "";
    private ClipboardSelection? selected;
    private ClipboardManifest manifest = new("", "", []);
    private readonly Dictionary<string, Pending> pending = [];
    private sealed class Pending(string owner, ClipboardUpload request, string directory)
    {
        public string Owner = owner;
        public ClipboardUpload Request = request;
        public string Directory = directory;
        public bool[] Complete = new bool[request.Files.Length];
    }
    private async Task<ClipboardManifest> ReadCore(string scope, CancellationToken ct)
    {
        var current = await clipboard.Read(ct);
        if (owner == scope && current.Sequence == selected?.Sequence) return manifest;
        var files = current.Paths.Any(path => !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) ? [] : current.Paths.Select(path =>
        {
            var info = new FileInfo(path); return new ClipboardFile(info.Name, info.Length);
        }).ToArray();
        if (files.Length > 0) ClipboardPaths.Validate(files);
        owner = scope; selected = current;
        ClipboardPaths.ValidateText(current.Text);
        manifest = new(Guid.NewGuid().ToString("N"), current.Origin, files, files.Length == 0 ? current.Text : ""); return manifest;
    }
    public async Task<ClipboardManifest> Read(string scope, CancellationToken ct)
    { await mutex.WaitAsync(ct); try { return await ReadCore(scope, ct); } finally { mutex.Release(); } }
    public async Task<string> Begin(string scope, ClipboardUpload request, CancellationToken ct)
    {
        ClipboardPaths.Validate(request.Files);
        if (string.IsNullOrEmpty(request.Origin) || request.Origin.Length > 100) throw new ArgumentException("Invalid clipboard origin");
        await mutex.WaitAsync(ct);
        try
        {
            if ((await ReadCore(scope, ct)).Id != request.ExpectedRevision) throw new ClipboardConflictException();
            if (pending.Count >= 4) throw new ArgumentException("클립보드 전송이 이미 진행 중입니다.");
            var id = Guid.NewGuid().ToString("N"); var directory = Path.Combine(cacheRoot, id); Directory.CreateDirectory(directory);
            pending.Add(id, new(scope, request, directory)); return id;
        }
        finally { mutex.Release(); }
    }
    public async Task<ClipboardManifest> SetText(string scope, ClipboardTextUpdate request, CancellationToken ct)
    {
        ClipboardPaths.ValidateText(request.Text);
        if (string.IsNullOrEmpty(request.Origin) || request.Origin.Length > 100) throw new ArgumentException("Invalid clipboard origin");
        await mutex.WaitAsync(ct);
        try
        {
            if ((await ReadCore(scope, ct)).Id != request.ExpectedRevision) throw new ClipboardConflictException();
            if (!await clipboard.Write([], request.Text, request.Origin, selected!.Sequence, ct)) throw new ClipboardConflictException();
            selected = null;
            return await ReadCore(scope, ct);
        }
        finally { mutex.Release(); }
    }
    private Pending Get(string scope, string id) => pending.TryGetValue(id, out var value) && value.Owner == scope ? value : throw new ClipboardConflictException();
    public async Task Receive(string scope, string id, int index, Stream input, string expectedHash, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var batch = Get(scope, id);
            if (index < 0 || index >= batch.Request.Files.Length || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid clipboard file");
            var file = batch.Request.Files[index]; var path = ClipboardPaths.FileAt(batch.Directory, index, file.Name);
            batch.Complete[index] = false;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; long count = 0; int n;
                    while ((n = await input.ReadAsync(buffer, ct)) > 0)
                    { count += n; if (count > file.Size) throw new ArgumentException("File size mismatch"); hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), ct); }
                    if (count != file.Size) throw new ArgumentException("File size mismatch");
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("File hash mismatch");
                batch.Complete[index] = true;
            }
            catch { if (File.Exists(path)) File.Delete(path); throw; }
        }
        finally { mutex.Release(); }
    }
    public async Task<ClipboardManifest> Commit(string scope, string id, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var batch = Get(scope, id);
            if (batch.Complete.Any(done => !done)) throw new ClipboardConflictException();
            if ((await ReadCore(scope, ct)).Id != batch.Request.ExpectedRevision) throw new ClipboardConflictException();
            var paths = batch.Request.Files.Select((f, i) => ClipboardPaths.FileAt(batch.Directory, i, f.Name)).ToArray();
            if (!await clipboard.Write(paths, "", batch.Request.Origin, selected!.Sequence, ct)) throw new ClipboardConflictException();
            pending.Remove(id); selected = null;
            return await ReadCore(scope, ct);
        }
        finally { mutex.Release(); }
    }
    public async Task<FileStream> Open(string scope, string id, int index, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var current = await ReadCore(scope, ct);
            if (current.Id != id || index < 0 || index >= current.Files.Length) throw new ClipboardConflictException();
            if ((File.GetAttributes(selected!.Paths[index]) & FileAttributes.ReparsePoint) != 0) throw new ClipboardConflictException();
            var stream = new FileStream(selected!.Paths[index], FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (stream.Length != current.Files[index].Size) { stream.Dispose(); throw new ClipboardConflictException(); }
            return stream;
        }
        finally { mutex.Release(); }
    }
    public async Task Cancel(string scope, string id)
    {
        await mutex.WaitAsync();
        try { if (pending.TryGetValue(id, out var value) && value.Owner == scope) { ClipboardPaths.DeleteBatch(cacheRoot, value.Directory); pending.Remove(id); } }
        finally { mutex.Release(); }
    }
    public async Task EndSession(string scope)
    {
        await mutex.WaitAsync();
        try { foreach (var pair in pending.Where(p => p.Value.Owner == scope).ToArray()) { ClipboardPaths.DeleteBatch(cacheRoot, pair.Value.Directory); pending.Remove(pair.Key); } if (owner == scope) { selected = null; owner = ""; } }
        finally { mutex.Release(); }
    }
}
public sealed class ClipboardConflictException : Exception
{ public ClipboardConflictException() : base("클립보드가 변경되었거나 전송이 만료되었습니다. 다시 복사하세요.") { } }
