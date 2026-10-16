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
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public List<RecentConnection> Recent { get; set; } = [];
    private static string DefaultPath => Path.Combine(HostSettings.DataDirectory, "client.json");

    public readonly record struct WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

    public void RememberWindow(double left, double top, double width, double height, bool maximized)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) || width < 320 || height < 240) return;
        WindowLeft = left;
        WindowTop = top;
        WindowWidth = width;
        WindowHeight = height;
        WindowMaximized = maximized;
    }

    public bool TryGetWindowPlacement(double screenLeft, double screenTop, double screenWidth, double screenHeight, out WindowPlacement placement)
    {
        placement = default;
        if (WindowLeft is not double left || WindowTop is not double top || WindowWidth is not double width || WindowHeight is not double height) return false;
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) || width < 320 || height < 240 || screenWidth <= 0 || screenHeight <= 0) return false;
        var visibleWidth = Math.Min(left + width, screenLeft + screenWidth) - Math.Max(left, screenLeft);
        var visibleHeight = Math.Min(top + height, screenTop + screenHeight) - Math.Max(top, screenTop);
        if (visibleWidth < 80 || visibleHeight < 40) return false;
        placement = new WindowPlacement(left, top, width, height, WindowMaximized);
        return true;
    }

    public void RememberRecent(string address, string? fingerprint, string? display, int quality, int rate)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        Recent ??= [];
        address = address.Trim();
        Recent.RemoveAll(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, new RecentConnection { Address = address, Fingerprint = fingerprint ?? "", Display = display ?? "", FrameQuality = quality, FrameRate = rate });
        if (Recent.Count > 5) Recent.RemoveRange(5, Recent.Count - 5);
    }

    public bool ForgetRecent(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        Recent ??= [];
        return Recent.RemoveAll(item => string.Equals(item.Address, address.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
    }
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

public sealed class RecentConnection
{
    public string Address { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Display { get; set; } = "";
    public int FrameQuality { get; set; } = 55;
    public int FrameRate { get; set; } = 15;
}
