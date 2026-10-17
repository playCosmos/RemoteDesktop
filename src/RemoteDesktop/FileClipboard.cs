using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RemoteDesktop;

public record ClipboardSelection(uint Sequence, string Origin, string[] Paths, string Text = "");
public interface IFileClipboard
{
    Task<ClipboardSelection> Read(CancellationToken ct);
    Task<bool> Write(string[] paths, string text, string origin, uint expectedSequence, CancellationToken ct);
}

public sealed class WindowsFileClipboard : IFileClipboard
{
    private const string Marker = "PersonalRemoteDesktop.FileOrigin";
    private static readonly Lazy<Dispatcher> Worker = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "File clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    public static uint Sequence => GetClipboardSequenceNumber();
    private static async Task<T> OnClipboard<T>(Func<T> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await Worker.Value.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); return action(); }).Task; }
            catch (ExternalException) when (attempt < 5) { await Task.Delay(60, ct); }
        }
    }
    public Task<ClipboardSelection> Read(CancellationToken ct) => OnClipboard(() =>
    {
        var sequence = Sequence;
        var data = Clipboard.GetDataObject();
        var paths = data?.GetDataPresent(DataFormats.FileDrop) == true ? (string[]?)data.GetData(DataFormats.FileDrop) ?? [] : [];
        var text = paths.Length == 0 ? ReadText(data) : "";
        var origin = data?.GetDataPresent(Marker) == true ? DecodeText(data.GetData(Marker), unicode: true) : "";
        if (sequence != Sequence) throw new ExternalException("클립보드가 변경되었습니다.");
        return new ClipboardSelection(sequence, origin, paths, text);
    }, ct);
    public Task<bool> Write(string[] paths, string text, string origin, uint expectedSequence, CancellationToken ct) => OnClipboard(() =>
    {
        if (Sequence != expectedSequence) return false;
        var data = new DataObject();
        if (paths.Length > 0)
        {
            var list = new StringCollection(); list.AddRange(paths);
            data.SetFileDropList(list);
            // Always copy, even if the source was cut. Never delete a source file remotely.
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        }
        else data.SetData(DataFormats.UnicodeText, text);
        data.SetData(Marker, origin);
        Clipboard.SetDataObject(data, true); return true;
    }, ct);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public static string DecodeText(object? value, bool unicode)
    {
        switch (value)
        {
            case string text: return TrimTerminator(text);
            case byte[] bytes: return DecodeBytes(bytes, unicode);
            case MemoryStream stream: return DecodeBytes(stream.ToArray(), unicode);
            default: return "";
        }
    }

    private static string DecodeBytes(byte[] bytes, bool unicode)
    {
        if (bytes.Length == 0) return "";
        var text = unicode ? System.Text.Encoding.Unicode.GetString(bytes) : System.Text.Encoding.Default.GetString(bytes);
        return TrimTerminator(text);
    }

    private static string TrimTerminator(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == (char)0) end--;
        return end == text.Length ? text : text[..end];
    }

    private static string ReadText(IDataObject? data)
    {
        if (data == null) return "";
        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = DecodeText(data.GetData(DataFormats.UnicodeText), unicode: true);
            if (text.Length > 0) return text;
        }
        if (data.GetDataPresent(DataFormats.Text))
        {
            var text = DecodeText(data.GetData(DataFormats.Text), unicode: false);
            if (text.Length > 0) return text;
        }
        if (data.GetDataPresent(DataFormats.StringFormat))
        {
            var text = DecodeText(data.GetData(DataFormats.StringFormat), unicode: true);
            if (text.Length > 0) return text;
        }
        if (Clipboard.ContainsText()) return Clipboard.GetText();
        return "";
    }
}

public record ClipboardFile(string Name, long Size, bool Directory = false);
public record ClipboardManifest(string Id, string Origin, ClipboardFile[] Files, string Text = "");
public record ClipboardUpload(string Origin, string ExpectedRevision, ClipboardFile[] Files);
public record ClipboardTextUpdate(string Origin, string ExpectedRevision, string Text);
public readonly record struct ClipboardLeaf(string Source, string Name, long Size, bool Directory = false);

public static class ClipboardPaths
{
    public const int MaxFiles = 100;
    public const long MaxBatchBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxTextCharacters = 1_000_000;
    const int MaxDepth = 32;
    const int MaxNameLength = 240;
    public static void ValidateText(string? text)
    {
        if (text is null || text.Length > MaxTextCharacters) throw new ArgumentException("클립보드 텍스트는 100만 자까지 지원합니다.");
    }
    public static ClipboardFile[] Accept(string[] paths)
        => Collect(paths).Select(leaf => new ClipboardFile(leaf.Name, leaf.Size, leaf.Directory)).ToArray();

    public static ClipboardLeaf[] Collect(string[] paths)
    {
        if (paths is null || paths.Length is < 1 or > MaxFiles) throw new ArgumentException(TooMany());
        var leaves = new List<ClipboardLeaf>();
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(PlainFiles());
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException(PlainFiles()); }
            if (!File.Exists(full) && !Directory.Exists(full)) throw new ArgumentException(PlainFiles());
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException(PlainFiles());
            if ((attributes & FileAttributes.Directory) == 0)
            {
                leaves.Add(new ClipboardLeaf(full, Path.GetFileName(full), new FileInfo(full).Length));
                continue;
            }
            var top = UniqueFolder(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), folderNames);
            var found = new List<ClipboardLeaf>();
            Walk(full, top, full, found, leaves.Count, 0);
            if (found.Count == 0) found.Add(new ClipboardLeaf(full, top, 0, true));
            found.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            leaves.AddRange(found);
        }
        if (leaves.Count is < 1 or > MaxFiles) throw new ArgumentException(TooMany());
        var files = new ClipboardFile[leaves.Count];
        for (var index = 0; index < leaves.Count; index++) files[index] = new ClipboardFile(leaves[index].Name, leaves[index].Size);
        Validate(files);
        return leaves.ToArray();
    }

    public static void Validate(ClipboardFile[] files)
    {
        if (files is null || files.Length is < 1 or > MaxFiles) throw new ArgumentException(TooMany());
        long total = 0;
        foreach (var file in files)
        {
            if (file is null) throw new ArgumentException("Invalid clipboard file");
            Segments(file.Name);
            if (file.Directory)
            {
                if (file.Size != 0) throw new ArgumentException("빈 폴더만 폴더로 복사할 수 있습니다.");
                continue;
            }
            if (file.Size < 0 || file.Size > RemoteHost.MaxFileBytes) throw new ArgumentException("파일당 최대 1GiB입니다.");
            total += file.Size;
        }
        if (total > MaxBatchBytes) throw new ArgumentException("한 번에 최대 4GiB까지 복사할 수 있습니다.");
    }

    public static string[] Segments(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.IndexOf((char)92) >= 0 || name.IndexOf(':') >= 0) throw new ArgumentException("지원하지 않는 파일 이름입니다.");
        var parts = name.Split('/');
        if (parts.Length == 0 || parts.Length > MaxDepth) throw new ArgumentException("지원하지 않는 파일 이름입니다.");
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part.Length > 200 || part is "." or ".." || part != Path.GetFileName(part) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' ')) throw new ArgumentException("지원하지 않는 파일 이름입니다.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) throw new ArgumentException("예약된 파일 이름입니다.");
        }
        return parts;
    }

    public static string FileAt(string directory, int index, string name)
        => FileAt(directory, index, new ClipboardFile(name, 0));

    public static string FileAt(string directory, int index, ClipboardFile file)
    {
        var parts = Segments(file.Name);
        var root = Path.GetFullPath(directory);
        var current = !file.Directory && parts.Length == 1 ? Path.Combine(root, "f", index.ToString()) : Path.Combine(root, "d");
        var last = file.Directory ? parts.Length : parts.Length - 1;
        for (var part = 0; part < last; part++) current = Path.Combine(current, parts[part]);
        Directory.CreateDirectory(current);
        var full = file.Directory ? Path.GetFullPath(current) : Path.GetFullPath(Path.Combine(current, parts[^1]));
        var parent = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("지원하지 않는 파일 이름입니다.");
        return full;
    }

    public static string[] Publish(string directory, ClipboardFile[] files)
    {
        Validate(files);
        var paths = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < files.Length; index++)
        {
            var stored = FileAt(directory, index, files[index]);
            var parts = Segments(files[index].Name);
            if (!files[index].Directory && parts.Length == 1) { paths.Add(stored); continue; }
            var top = Path.GetFullPath(Path.Combine(Path.GetFullPath(directory), "d", parts[0]));
            if (folders.Add(top)) paths.Add(top);
        }
        return paths.ToArray();
    }

    public static void DeleteBatch(string root, string batch)
    {
        var absolute = Path.GetFullPath(batch); var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid cache path");
        if (Directory.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) == 0) Directory.Delete(absolute, true);
    }

    static void Walk(string directory, string top, string root, List<ClipboardLeaf> into, int already, int depth)
    {
        if (depth > MaxDepth) throw new ArgumentException("지원하지 않는 파일 이름입니다.");
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ArgumentException("폴더를 읽지 못했습니다."); }
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ArgumentException("폴더를 읽지 못했습니다."); }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException(PlainFiles());
            if ((attributes & FileAttributes.Directory) != 0)
            {
                var before = into.Count;
                Walk(entry, top, root, into, already, depth + 1);
                if (into.Count != before) continue;
                if (already + into.Count >= MaxFiles) throw new ArgumentException(TooMany());
                var relativeDir = Path.GetRelativePath(root, entry).Replace((char)92, '/');
                into.Add(new ClipboardLeaf(Path.GetFullPath(entry), top + "/" + relativeDir, 0, true));
                continue;
            }
            if (already + into.Count >= MaxFiles) throw new ArgumentException(TooMany());
            var relative = Path.GetRelativePath(root, entry).Replace((char)92, '/');
            into.Add(new ClipboardLeaf(Path.GetFullPath(entry), top + "/" + relative, new FileInfo(entry).Length));
        }
    }

    static string UniqueFolder(string name, HashSet<string> taken)
    {
        var candidate = name;
        var number = 2;
        while (!taken.Add(candidate))
        {
            candidate = name + " (" + number + ")";
            number++;
            if (number > MaxFiles) throw new ArgumentException("지원하지 않는 파일 이름입니다.");
        }
        return candidate;
    }

    static string TooMany() => "한 번에 파일 1~100개를 복사할 수 있습니다.";
    static string PlainFiles() => "파일이나 폴더만 복사할 수 있습니다. 링크는 지원하지 않습니다.";

    public static int DeleteRetired(string root, IEnumerable<string> keepPaths)
    {
        if (!Directory.Exists(root)) return 0;
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in keepPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var absolute = Path.GetFullPath(path);
            if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = absolute[parent.Length..];
            var slash = relative.IndexOf(Path.DirectorySeparatorChar);
            kept.Add(slash < 0 ? absolute : absolute[..(parent.Length + slash)]);
        }
        var names = new List<string>();
        foreach (var batch in Directory.EnumerateDirectories(root)) names.Add(Path.GetFileName(batch));
        var keepNames = new List<string>();
        foreach (var path in kept) keepNames.Add(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var removed = 0;
        foreach (var name in RetiredNames(names, keepNames))
        {
            var batch = Path.Combine(root, name);
            try
            {
                if ((File.GetAttributes(batch) & FileAttributes.ReparsePoint) != 0) continue;
                DeleteBatch(root, batch);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    public static IReadOnlyList<string> RetiredNames(IReadOnlyList<string>? batches, IEnumerable<string>? keep)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keep != null)
            foreach (var name in keep)
                if (!string.IsNullOrWhiteSpace(name)) kept.Add(name);
        var retired = new List<string>();
        if (batches == null) return retired;
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch) || kept.Contains(batch)) continue;
            retired.Add(batch);
        }
        return retired;
    }
}
