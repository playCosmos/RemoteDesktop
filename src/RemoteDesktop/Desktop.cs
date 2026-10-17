using System.Drawing;

namespace RemoteDesktop;

public record InputEvent(string Kind, double X = 0, double Y = 0, int Code = 0, bool Down = false, string? Text = null, string? Display = null)
{
    public const int MaxWheelDelta = 1200;
}

public sealed class InputBacklog
{
    private readonly object gate = new();
    private bool releaseRequested;

    public bool Note()
    {
        lock (gate)
        {
            var first = !releaseRequested;
            releaseRequested = true;
            return first;
        }
    }

    public bool Take()
    {
        lock (gate)
        {
            if (!releaseRequested) return false;
            releaseRequested = false;
            return true;
        }
    }

    public void Reset()
    {
        lock (gate) releaseRequested = false;
    }
}
public record RemoteDisplay(string Id, string Name, int Left, int Top, int Width, int Height, bool Primary)
{ public override string ToString() => Name + " · " + Width + "×" + Height; }
public readonly record struct CaptureTiming(double CaptureMilliseconds, double EncodeMilliseconds, bool Reused, int Quality);
public readonly record struct CapturedFrame(byte[] Bytes, CaptureTiming Timing, double CursorX, double CursorY, bool LocalCursor, bool CursorVisible, int CursorHotX, int CursorHotY, string CursorHash, byte[]? CursorPng);

public static class CursorPlacement
{
    public static Point DrawPoint(int cursorX, int cursorY, Rectangle view, int hotspotX, int hotspotY)
        => new(cursorX - view.Left - hotspotX, cursorY - view.Top - hotspotY);

    public readonly record struct PointerMark(double Left, double Top, double Width, double Height, bool Shown);

    public static PointerMark Place(double pointerX, double pointerY, int hotX, int hotY, int cursorWidth, int cursorHeight, double imageWidth, double imageHeight, double boxWidth, double boxHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || boxWidth <= 0 || boxHeight <= 0 || cursorWidth <= 0 || cursorHeight <= 0 || !double.IsFinite(pointerX) || !double.IsFinite(pointerY)) return new(0, 0, 0, 0, false);
        var scale = Math.Min(boxWidth / imageWidth, boxHeight / imageHeight);
        var drawnWidth = imageWidth * scale;
        var drawnHeight = imageHeight * scale;
        var offsetX = (boxWidth - drawnWidth) / 2;
        var offsetY = (boxHeight - drawnHeight) / 2;
        if (pointerX < offsetX || pointerY < offsetY || pointerX > offsetX + drawnWidth || pointerY > offsetY + drawnHeight) return new(0, 0, 0, 0, false);
        return new(pointerX - hotX * scale, pointerY - hotY * scale, cursorWidth * scale, cursorHeight * scale, true);
    }
}

public sealed class Desktop
{
    private readonly DisplayCapture capture = new();
    private readonly InputInjector input = new();
    public static bool SmoothCapture { get; set; }
    public CaptureTiming LastTiming => capture.LastTiming;

    static Desktop() => DesktopThread.EnsureStarted();

    public Desktop()
    {
        if (SmoothCapture) DesktopThread.AttachPump(capture.PumpOnce);
    }

    public static RemoteDisplay[] Displays()
    {
        var all = System.Windows.Forms.SystemInformation.VirtualScreen;
        return [new("all", "전체 모니터", all.Left, all.Top, all.Width, all.Height, false), .. System.Windows.Forms.Screen.AllScreens.Select((s, i) => new RemoteDisplay(s.DeviceName, "모니터 " + (i + 1) + (s.Primary ? " (기본)" : ""), s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary))];
    }

    public static Rectangle Bounds(string? display)
    {
        var screens = Displays();
        var selected = display is null or "" ? screens.First(s => s.Primary) : screens.FirstOrDefault(s => s.Id == display) ?? throw new ArgumentException("모니터가 연결 해제되었습니다. 목록을 새로고침하세요.");
        return new Rectangle(selected.Left, selected.Top, selected.Width, selected.Height);
    }

    public static (int X, int Y) MapPoint(Rectangle selected, Rectangle all, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Invalid coordinates");
        return ((int)((selected.Left + Math.Clamp(x, 0, 1) * (selected.Width - 1) - all.Left) * 65535 / Math.Max(1, all.Width - 1)),
            (int)((selected.Top + Math.Clamp(y, 0, 1) * (selected.Height - 1) - all.Top) * 65535 / Math.Max(1, all.Height - 1)));
    }

    public static Rectangle Viewport(Rectangle bounds, double x, double y, double width, double height) => DisplayCapture.Viewport(bounds, x, y, width, height);

    public CapturedFrame CaptureDetailed(string? display = null, long quality = 55, double viewX = 0, double viewY = 0, double viewWidth = 1, double viewHeight = 1, int fps = 15, bool localCursor = false)
        => capture.CaptureDetailed(display, quality, viewX, viewY, viewWidth, viewHeight, fps, localCursor);

    public string? Apply(InputEvent e) => DesktopThread.Invoke(() => input.ApplyCore(e));
    public void Release() => DesktopThread.Invoke(input.ReleaseCore);
}
