using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RemoteDesktop;

readonly record struct CaptureRequest(string? Display, int Quality, int Fps, double X, double Y, double Width, double Height, bool LocalCursor);

internal sealed class DisplayCapture
{
    private readonly object frameGate = new();
    private byte[]? unavailableFrame;
    private Size unavailableFrameSize;
    private readonly object smoothGate = new();
    private readonly ManualResetEventSlim frameReady = new(false);
    private CaptureRequest pendingRequest = new(null, 55, 15, 0, 0, 1, 1, false);
    private CapturedFrame? publishedFrame;
    private CaptureRequest publishedRequest;
    private DateTime requestUntil = DateTime.MinValue;
    private DateTime nextCaptureDue = DateTime.MinValue;
    private DateTime publishedAt = DateTime.MinValue;
    private int frameVersion;
    private int adaptiveQuality;
    private int slowStreak;
    private int fastStreak;
    private byte[]? reusableJpeg;
    private string reusableKey = "";
    private int[]? cleanPixels;
    private bool dxgiRejected;
    private bool secureLatched;
    public CaptureTiming LastTiming { get; private set; } = new(0, 0, false, 55);

    public CapturedFrame CaptureDetailed(string? display = null, long quality = 55, double viewX = 0, double viewY = 0, double viewWidth = 1, double viewHeight = 1, int fps = 15, bool localCursor = false)
    {
        _ = Desktop.Bounds(display);
        var request = new CaptureRequest(display, (int)Math.Clamp(quality, 25, 90), fps is 5 or 10 or 15 or 30 ? fps : 15, viewX, viewY, viewWidth, viewHeight, localCursor);
        if (!Desktop.SmoothCapture) return CaptureOnCaller(request);
        int seen;
        lock (smoothGate)
        {
            pendingRequest = request;
            requestUntil = DateTime.UtcNow.AddSeconds(1);
            if (publishedFrame is null) nextCaptureDue = DateTime.MinValue;
            seen = frameVersion;
            if (publishedFrame is { } ready && SameView(publishedRequest, request) && DateTime.UtcNow - publishedAt <= Freshness(request.Fps)) return ready;
        }
        var waited = Stopwatch.StartNew();
        while (waited.ElapsedMilliseconds < 700)
        {
            frameReady.Wait(40);
            lock (smoothGate)
            {
                if (publishedFrame is { } ready && frameVersion != seen && SameView(publishedRequest, request)) return ready;
            }
        }
        lock (smoothGate)
        {
            if (publishedFrame is { } ready && SameView(publishedRequest, request)) return ready;
        }
        return CaptureOnCaller(request);
    }

    public bool PumpOnce()
    {
        CaptureRequest request;
        lock (smoothGate)
        {
            if (DateTime.UtcNow > requestUntil) return false;
            if (DateTime.UtcNow < nextCaptureDue && publishedFrame is not null && SameView(publishedRequest, pendingRequest)) return false;
            request = pendingRequest;
        }
        CapturedFrame frame;
        try { frame = CaptureSmooth(request); }
        catch (ArgumentException)
        {
            lock (smoothGate) { requestUntil = DateTime.MinValue; }
            return false;
        }
        catch (Exception e) when (e is ExternalException or Win32Exception or InvalidOperationException)
        {
            var view = Viewport(Desktop.Bounds(request.Display), request.X, request.Y, request.Width, request.Height);
            frame = Finish(request, view, UnavailableFrame(view.Size, request.Quality), new CaptureTiming(0, 0, false, request.Quality));
        }
        lock (smoothGate)
        {
            publishedFrame = frame;
            publishedRequest = request;
            publishedAt = DateTime.UtcNow;
            frameVersion++;
            var budget = 1000.0 / request.Fps;
            var spent = frame.Timing.Reused ? 0 : frame.Timing.CaptureMilliseconds + frame.Timing.EncodeMilliseconds;
            nextCaptureDue = DateTime.UtcNow.AddMilliseconds(Math.Max(1, budget - spent));
            Adapt(frame, budget, request.Quality);
        }
        frameReady.Set();
        return true;
    }

    private CapturedFrame CaptureOnCaller(CaptureRequest request)
        => DesktopThread.Invoke(() => CaptureImmediate(request));

    private CapturedFrame CaptureImmediate(CaptureRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        var bounds = Desktop.Bounds(request.Display);
        var view = Viewport(bounds, request.X, request.Y, request.Width, request.Height);
        var secure = SecureReplacement(request, view);
        if (secure.HasValue) return secure.Value;
        try
        {
            using var bitmap = new Bitmap(view.Width, view.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(view.Location, Point.Empty, view.Size);
                if (!request.LocalCursor) DrawCursor(graphics, view);
            }
            var captured = Stopwatch.GetTimestamp();
            var bytes = Encode(bitmap, request.Quality);
            return Finish(request, view, bytes, new CaptureTiming(ElapsedMs(started, captured), ElapsedMs(captured, Stopwatch.GetTimestamp()), false, request.Quality));
        }
        catch (Exception e) when (e is ExternalException or Win32Exception)
        {
            return Finish(request, view, UnavailableFrame(bounds.Size, request.Quality), new CaptureTiming(ElapsedMs(started, Stopwatch.GetTimestamp()), 0, false, request.Quality));
        }
    }

    private CapturedFrame CaptureSmooth(CaptureRequest request)
    {
        var quality = ChooseQuality(request.Quality);
        var started = Stopwatch.GetTimestamp();
        var bounds = Desktop.Bounds(request.Display);
        var view = Viewport(bounds, request.X, request.Y, request.Width, request.Height);
        var secure = SecureReplacement(request, view);
        if (secure.HasValue) return secure.Value;
        var cursor = System.Windows.Forms.Cursor.Position;
        var key = FrameKey(view, quality, request.LocalCursor, cursor);
        if (!dxgiRejected && request.Display != "all" && DxgiDisplayCapture.TryAcquire(request.Display, 8, out var image))
        {
            if (Math.Abs(image.Width - bounds.Width) > 2 || Math.Abs(image.Height - bounds.Height) > 2) dxgiRejected = true;
            else
            {
                if (!image.Changed && reusableJpeg is not null && reusableKey == key) return Reuse(request, view, quality);
                using var bitmap = Crop(image, view);
                using (var graphics = Graphics.FromImage(bitmap)) if (!request.LocalCursor) DrawCursor(graphics, view);
                var captured = Stopwatch.GetTimestamp();
                var encoded = Encode(bitmap, quality);
                reusableJpeg = encoded;
                reusableKey = key;
                return Finish(request, view, encoded, new CaptureTiming(ElapsedMs(started, captured), ElapsedMs(captured, Stopwatch.GetTimestamp()), false, quality));
            }
        }
        using var gdi = new Bitmap(view.Width, view.Height);
        using (var graphics = Graphics.FromImage(gdi)) graphics.CopyFromScreen(view.Location, Point.Empty, view.Size);
        var copied = Stopwatch.GetTimestamp();
        if (Unchanged(gdi) && reusableJpeg is not null && reusableKey == key) return Reuse(request, view, quality);
        using (var graphics = Graphics.FromImage(gdi)) if (!request.LocalCursor) DrawCursor(graphics, view);
        var bytes = Encode(gdi, quality);
        reusableJpeg = bytes;
        reusableKey = key;
        return Finish(request, view, bytes, new CaptureTiming(ElapsedMs(started, copied), ElapsedMs(copied, Stopwatch.GetTimestamp()), false, quality));
    }

    private static bool SameView(CaptureRequest published, CaptureRequest request)
        => published.Display == request.Display && published.LocalCursor == request.LocalCursor
           && Math.Abs(published.X - request.X) < 0.002 && Math.Abs(published.Y - request.Y) < 0.002
           && Math.Abs(published.Width - request.Width) < 0.002 && Math.Abs(published.Height - request.Height) < 0.002;
    private static TimeSpan Freshness(int fps) => TimeSpan.FromMilliseconds(Math.Max(80, 2000.0 / Math.Max(1, fps)));
    private int ChooseQuality(int requested)
    {
        var chosen = adaptiveQuality is >= 25 and <= 90 ? Math.Min(adaptiveQuality, requested) : requested;
        return Math.Clamp(chosen, 25, 90);
    }
    private void Adapt(CapturedFrame frame, double budget, int requested)
    {
        if (frame.Timing.Reused) return;
        var cost = frame.Timing.CaptureMilliseconds + frame.Timing.EncodeMilliseconds;
        if (cost > budget * 1.1) { slowStreak++; fastStreak = 0; }
        else if (cost < budget * 0.45) { fastStreak++; slowStreak = 0; }
        else slowStreak = fastStreak = 0;
        var current = frame.Timing.Quality;
        if (slowStreak >= 2 && current > 25) adaptiveQuality = current >= 75 ? 55 : current >= 55 ? 40 : 25;
        else if (fastStreak >= 8 && current < requested) adaptiveQuality = Math.Min(requested, current < 40 ? 40 : current < 55 ? 55 : 75);
        if (adaptiveQuality > requested) adaptiveQuality = requested;
    }
    private static string FrameKey(Rectangle view, int quality, bool localCursor, Point cursor)
        => view.X + "," + view.Y + "," + view.Width + "," + view.Height + "|" + quality + "|" + (localCursor ? "local" : cursor.X + "," + cursor.Y);
    private CapturedFrame Reuse(CaptureRequest request, Rectangle view, int quality)
        => Finish(request, view, reusableJpeg!, new CaptureTiming(0, 0, true, quality));
    private CapturedFrame Finish(CaptureRequest request, Rectangle view, byte[] bytes, CaptureTiming timing)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var x = (cursor.X - view.Left) / (double)Math.Max(1, view.Width - 1);
        var y = (cursor.Y - view.Top) / (double)Math.Max(1, view.Height - 1);
        LastTiming = timing;
        var shot = request.LocalCursor ? CursorGlyph.Capture() : default;
        return new CapturedFrame(bytes, timing, x, y, request.LocalCursor, shot.Visible, shot.HotspotX, shot.HotspotY, shot.Hash ?? "", shot.Png);
    }

    private CapturedFrame? SecureReplacement(CaptureRequest request, Rectangle view)
    {
        var observed = SecureDesktop.Observe(DateTime.UtcNow);
        if (observed.View == SecureView.Off)
        {
            if (secureLatched) { secureLatched = false; reusableJpeg = null; }
            return null;
        }
        secureLatched = true;
        reusableJpeg = null;
        if (observed.View == SecureView.Live && observed.Jpeg is { Length: > 0 } jpeg)
            return Finish(request, view, jpeg, new CaptureTiming(1, 0, false, request.Quality));
        return Finish(request, view, UnavailableFrame(view.Size, request.Quality), new CaptureTiming(0, 0, false, request.Quality));
    }
    private bool Unchanged(Bitmap bitmap)
    {
        var count = bitmap.Width * bitmap.Height;
        if (count is <= 0 or > 8_000_000) return false;
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (Math.Abs(data.Stride) != bitmap.Width * 4) return false;
            if (cleanPixels is null || cleanPixels.Length != count)
            {
                cleanPixels = new int[count];
                Marshal.Copy(data.Scan0, cleanPixels, 0, count);
                return false;
            }
            var row = new int[bitmap.Width];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                var offset = y * bitmap.Width;
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x] == cleanPixels[offset + x]) continue;
                    Marshal.Copy(data.Scan0, cleanPixels, 0, count);
                    return false;
                }
            }
            return true;
        }
        finally { bitmap.UnlockBits(data); }
    }
    private static Bitmap Crop(DxgiImage image, Rectangle view)
    {
        var x = Math.Clamp(view.Left - image.Left, 0, Math.Max(0, image.Width - 1));
        var y = Math.Clamp(view.Top - image.Top, 0, Math.Max(0, image.Height - 1));
        var width = Math.Clamp(view.Width, 1, image.Width - x);
        var height = Math.Clamp(view.Height, 1, image.Height - y);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(image.Bgra, ((y + row) * image.Width + x) * 4, data.Scan0 + row * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    private static void DrawCursor(Graphics graphics, Rectangle view)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || info.Cursor == 0 || (info.Flags & 1) == 0) return;
        var hotspotX = 0;
        var hotspotY = 0;
        nint mask = 0;
        nint color = 0;
        if (GetIconInfo(info.Cursor, out var icon))
        {
            hotspotX = icon.HotspotX;
            hotspotY = icon.HotspotY;
            mask = icon.Mask;
            color = icon.Color;
        }
        var origin = CursorPlacement.DrawPoint(info.X, info.Y, view, hotspotX, hotspotY);
        var dc = graphics.GetHdc();
        try { DrawIconEx(dc, origin.X, origin.Y, info.Cursor, 0, 0, 0, 0, 3); }
        finally
        {
            graphics.ReleaseHdc(dc);
            if (mask != 0) DeleteObject(mask);
            if (color != 0) DeleteObject(color);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public nint Cursor; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public int HotspotX, HotspotY; public nint Mask, Color; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint cursor, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, int step, nint brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    private static double ElapsedMs(long start, long end) => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    public static Rectangle Viewport(Rectangle bounds, double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new ArgumentException("Invalid viewport");
        x = Math.Clamp(x, 0, 1); y = Math.Clamp(y, 0, 1); width = Math.Clamp(width, 0.01, 1); height = Math.Clamp(height, 0.01, 1);
        x = Math.Clamp(x, 0, 1 - width); y = Math.Clamp(y, 0, 1 - height);
        return new Rectangle(bounds.Left + (int)Math.Round(x * bounds.Width), bounds.Top + (int)Math.Round(y * bounds.Height), Math.Max(1, (int)Math.Round(width * bounds.Width)), Math.Max(1, (int)Math.Round(height * bounds.Height)));
    }
    private byte[] UnavailableFrame(Size size, long quality)
    {
        lock (frameGate)
        {
            if (unavailableFrame is not null && unavailableFrameSize == size) return unavailableFrame;
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(24, 28, 36));
                var text = "원격 화면을 잠시 가져올 수 없습니다." + Environment.NewLine + "서버 PC에 권한 확인 창이 있으면 그곳에서 확인한 뒤 돌아오세요.";
                using var brush = new SolidBrush(Color.White);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var font = CreateCaptionFont();
                graphics.DrawString(text, font, brush, new RectangleF(0, 0, size.Width, size.Height), format);
            }
            unavailableFrame = Encode(bitmap, quality); unavailableFrameSize = size;
            return unavailableFrame;
        }
    }
    private static Font CreateCaptionFont()
    {
        try { return new Font("Malgun Gothic", 16f); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, 16f); }
    }
    private static byte[] Encode(Bitmap bitmap, long quality)
    {
        var level = (int)Math.Clamp(quality, 25, 90);
        try { return ScreenJpeg.Encode(bitmap, level); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException or Win32Exception)
        {
            return EncodeWithGdi(bitmap, level);
        }
    }

    private static byte[] EncodeWithGdi(Bitmap bitmap, int quality)
    {
        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        bitmap.Save(stream, ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg"), parameters);
        return stream.ToArray();
    }
}
