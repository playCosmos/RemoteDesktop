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

public record ClipboardFile(string Name, long Size);
public record ClipboardManifest(string Id, string Origin, ClipboardFile[] Files, string Text = "");
public record ClipboardUpload(string Origin, string ExpectedRevision, ClipboardFile[] Files);
public record ClipboardTextUpdate(string Origin, string ExpectedRevision, string Text);

public static class ClipboardPaths
{
    public const int MaxFiles = 100;
    public const long MaxBatchBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxTextCharacters = 1_000_000;
    public static void ValidateText(string? text)
    {
        if (text is null || text.Length > MaxTextCharacters) throw new ArgumentException("클립보드 텍스트는 100만 자까지 지원합니다.");
    }
    public static ClipboardFile[] Accept(string[] paths)
    {
        if (paths is null || paths.Length is < 1 or > MaxFiles) throw new ArgumentException("한 번에 파일 1~100개를 복사할 수 있습니다. 폴더는 지원하지 않습니다.");
        var files = new ClipboardFile[paths.Length];
        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("일반 파일만 복사할 수 있습니다. 폴더·링크는 지원하지 않습니다.");
            files[index] = new ClipboardFile(Path.GetFileName(path), new FileInfo(path).Length);
        }
        Validate(files);
        return files;
    }
    public static void Validate(ClipboardFile[] files)
    {
        if (files is null || files.Length is < 1 or > MaxFiles) throw new ArgumentException("한 번에 파일 1~100개를 복사할 수 있습니다. 폴더는 지원하지 않습니다.");
        long total = 0;
        foreach (var file in files)
        {
            if (file is null) throw new ArgumentException("Invalid clipboard file");
            var name = file.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) throw new ArgumentException("지원하지 않는 파일 이름입니다.");
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) throw new ArgumentException("예약된 파일 이름입니다.");
            if (file.Size < 0 || file.Size > RemoteHost.MaxFileBytes) throw new ArgumentException("파일당 최대 1GiB입니다.");
            total += file.Size;
        }
        if (total > MaxBatchBytes) throw new ArgumentException("한 번에 최대 4GiB까지 복사할 수 있습니다.");
    }
    public static string FileAt(string directory, int index, string name)
    { var folder = Path.Combine(directory, index.ToString()); Directory.CreateDirectory(folder); return Path.Combine(folder, name); }
    public static void DeleteBatch(string root, string batch)
    {
        var absolute = Path.GetFullPath(batch); var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid cache path");
        if (Directory.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) == 0) Directory.Delete(absolute, true);
    }

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
        var removed = 0;
        foreach (var batch in Directory.EnumerateDirectories(root))
        {
            if (kept.Contains(Path.GetFullPath(batch))) continue;
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
}
