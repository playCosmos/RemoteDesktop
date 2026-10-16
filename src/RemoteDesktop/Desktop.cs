using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RemoteDesktop;

public record InputEvent(string Kind, double X = 0, double Y = 0, int Code = 0, bool Down = false, string? Text = null, string? Display = null);
public record RemoteDisplay(string Id, string Name, int Left, int Top, int Width, int Height, bool Primary)
{ public override string ToString() => $"{Name} · {Width}×{Height}"; }
public readonly record struct CaptureTiming(double CaptureMilliseconds, double EncodeMilliseconds, bool Reused, int Quality);
public readonly record struct CapturedFrame(byte[] Bytes, CaptureTiming Timing, double CursorX, double CursorY, bool LocalCursor);
readonly record struct CaptureRequest(string? Display, int Quality, int Fps, double X, double Y, double Width, double Height, bool LocalCursor);

public sealed class Desktop
{
    private readonly HashSet<int> keys = [];
    private readonly HashSet<int> buttons = [];
    private readonly object frameGate = new();
    private static readonly DesktopInputQueue inputDesktopQueue = new();
    public static bool SmoothCapture { get; set; }
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
    public CaptureTiming LastTiming { get; private set; } = new(0, 0, false, 55);

    public Desktop()
    {
        if (SmoothCapture) inputDesktopQueue.AttachPump(PumpOnce);
    }
    public static RemoteDisplay[] Displays()
    {
        var all = System.Windows.Forms.SystemInformation.VirtualScreen;
        return [new("all", "전체 모니터", all.Left, all.Top, all.Width, all.Height, false), .. System.Windows.Forms.Screen.AllScreens.Select((s, i) => new RemoteDisplay(s.DeviceName, $"모니터 {i + 1}{(s.Primary ? " (기본)" : "")}", s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary))];
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
    public CapturedFrame CaptureDetailed(string? display = null, long quality = 55, double viewX = 0, double viewY = 0, double viewWidth = 1, double viewHeight = 1, int fps = 15, bool localCursor = false)
    {
        _ = Bounds(display);
        var request = new CaptureRequest(display, (int)Math.Clamp(quality, 25, 90), fps is 5 or 10 or 15 or 30 ? fps : 15, viewX, viewY, viewWidth, viewHeight, localCursor);
        if (!SmoothCapture) return CaptureOnCaller(request);
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
    private CapturedFrame CaptureOnCaller(CaptureRequest request)
        => inputDesktopQueue.Invoke(() => CaptureImmediate(request));
    private bool PumpOnce()
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
            var view = Viewport(Bounds(request.Display), request.X, request.Y, request.Width, request.Height);
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
    private CapturedFrame CaptureImmediate(CaptureRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        var bounds = Bounds(request.Display);
        var view = Viewport(bounds, request.X, request.Y, request.Width, request.Height);
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
        var bounds = Bounds(request.Display);
        var view = Viewport(bounds, request.X, request.Y, request.Width, request.Height);
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
        => $"{view.X},{view.Y},{view.Width},{view.Height}|{quality}|{(localCursor ? "local" : cursor.X + "," + cursor.Y)}";
    private CapturedFrame Reuse(CaptureRequest request, Rectangle view, int quality)
        => Finish(request, view, reusableJpeg!, new CaptureTiming(0, 0, true, quality));
    private CapturedFrame Finish(CaptureRequest request, Rectangle view, byte[] bytes, CaptureTiming timing)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var x = (cursor.X - view.Left) / (double)Math.Max(1, view.Width - 1);
        var y = (cursor.Y - view.Top) / (double)Math.Max(1, view.Height - 1);
        LastTiming = timing;
        return new CapturedFrame(bytes, timing, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), request.LocalCursor);
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
        var cursor = System.Windows.Forms.Cursor.Position;
        System.Windows.Forms.Cursors.Default.Draw(graphics, new Rectangle(cursor.X - view.X, cursor.Y - view.Y, 24, 24));
    }
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
                var text = "Remote display is temporarily unavailable\nConfirm the UAC prompt on the server, then return here.";
                using var brush = new SolidBrush(Color.White);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var font = new Font(FontFamily.GenericSansSerif, 16f);
                graphics.DrawString(text, font, brush, new RectangleF(0, 0, size.Width, size.Height), format);
            }
            unavailableFrame = Encode(bitmap, quality); unavailableFrameSize = size;
            return unavailableFrame;
        }
    }
    private static byte[] Encode(Bitmap bitmap, long quality)
    {
        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 25, 90));
        bitmap.Save(stream, ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg"), parameters);
        return stream.ToArray();
    }
    public void Apply(InputEvent e) => inputDesktopQueue.Invoke(() => ApplyCore(e));
    private void ApplyCore(InputEvent e)
    {
        lock (keys)
        {
            switch (e.Kind)
            {
                case "move":
                    if (!double.IsFinite(e.X) || !double.IsFinite(e.Y)) throw new ArgumentException("Invalid coordinates");
                    var point = MapPoint(Bounds(e.Display), System.Windows.Forms.SystemInformation.VirtualScreen, e.X, e.Y);
                    Send(new NativeInput { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = point.X, Y = point.Y, Flags = 0xC001 } } });
                    break;
                case "button":
                    if (e.Code is < 0 or > 2) throw new ArgumentException("Invalid button");
                    if (Mouse((uint)(e.Code == 0 ? (e.Down ? 2 : 4) : e.Code == 1 ? (e.Down ? 8 : 16) : (e.Down ? 32 : 64))))
                    { if (e.Down) buttons.Add(e.Code); else buttons.Remove(e.Code); }
                    break;
                case "wheel": Mouse(0x800, unchecked((uint)Math.Clamp(e.Code, -1200, 1200))); break;
                case "key":
                    if (e.Code is < 1 or > 254) throw new ArgumentException("Invalid key");
                    if (Key((ushort)e.Code, 0, e.Down ? 0u : 2u))
                    { if (e.Down) keys.Add(e.Code); else keys.Remove(e.Code); }
                    break;
                case "text":
                    if (e.Text is null || e.Text.Length > 4096) throw new ArgumentException("Invalid text");
                    foreach (var c in e.Text) { Key(0, c, 4); Key(0, c, 6); }
                    break;
                case "release": ReleaseCore(); break;
                default: throw new ArgumentException("Unknown input");
            }
        }
    }
    public void Release() => inputDesktopQueue.Invoke(ReleaseCore);
    private void ReleaseCore()
    {
        lock (keys)
        {
            foreach (var key in keys) Key((ushort)key, 0, 2);
            foreach (var button in buttons) Mouse((uint)(button == 0 ? 4 : button == 1 ? 16 : 64));
            keys.Clear(); buttons.Clear();
        }
    }
    private static bool Mouse(uint flags, uint data = 0) => Send(new NativeInput { Data = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data } } });
    private static bool Key(ushort key, ushort scan, uint flags)
    {
        if (key is >= 33 and <= 46 or 91 or 92 or 111 or 163 or 165) flags |= 1;
        return Send(new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Scan = scan, Flags = flags } } });
    }
    private static bool Send(NativeInput input) => SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1;
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    private sealed class DesktopInputQueue
    {
        private readonly BlockingCollection<Action> work = new();
        private Func<bool>? smoothPump;
        private bool prioritySet;

        public DesktopInputQueue()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Remote Desktop input desktop" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void AttachPump(Func<bool> callback) => smoothPump = callback;

        public T Invoke<T>(Func<T> callback)
        {
            T? result = default;
            Exception? failure = null;
            using var completed = new ManualResetEventSlim();
            work.Add(() =>
            {
                try { result = callback(); }
                catch (Exception e) { failure = e; }
                finally { completed.Set(); }
            });
            completed.Wait();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result!;
        }

        public void Invoke(Action callback) => Invoke(() => { callback(); return true; });

        private void Run()
        {
            while (!work.IsCompleted)
            {
                ApplyPriority();
                if (!SmoothCapture)
                {
                    if (work.TryTake(out var idle, 250)) idle();
                    continue;
                }
                if (work.TryTake(out var callback))
                {
                    callback();
                    continue;
                }
                if (smoothPump?.Invoke() == true) continue;
                if (work.TryTake(out callback, 15)) callback();
            }
        }

        private void ApplyPriority()
        {
            if (prioritySet) return;
            if (SmoothCapture) HostPerformance.ApplyCaptureThreadPriority();
            else return;
            prioritySet = true;
        }

    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
}
