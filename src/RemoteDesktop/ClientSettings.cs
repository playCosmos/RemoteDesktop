using System.IO;
using System.Text.Json;

namespace RemoteDesktop;

public sealed class ClientSettings
{
    public string Address { get; set; } = "127.0.0.1:45831";
    public string Fingerprint { get; set; } = "";
    public string Display { get; set; } = "";
    public int FrameQuality { get; set; } = 55;
    public int FrameRate { get; set; } = 15;
    public bool ClipboardAutoSync { get; set; } = true;
    private static string DefaultPath => Path.Combine(HostSettings.DataDirectory, "client.json");
    public static ClientSettings Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
