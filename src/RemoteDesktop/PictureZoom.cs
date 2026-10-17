using System.Globalization;

namespace RemoteDesktop;

public readonly record struct PictureView(double X, double Y, double Width, double Height, double Zoom)
{
    public static PictureView Full { get; } = new(0, 0, 1, 1, 1);
}

public static class PictureZoom
{
    public static PictureView At(PictureView current, double zoom, double focusX, double focusY)
    {
        zoom = Math.Clamp(zoom, 1, 4);
        if (zoom <= 1.01) return PictureView.Full;
        focusX = Math.Clamp(focusX, 0, 1);
        focusY = Math.Clamp(focusY, 0, 1);
        var width = 1 / zoom;
        var height = 1 / zoom;
        var globalX = current.X + focusX * current.Width;
        var globalY = current.Y + focusY * current.Height;
        var x = Math.Clamp(globalX - focusX * width, 0, Math.Max(0, 1 - width));
        var y = Math.Clamp(globalY - focusY * height, 0, Math.Max(0, 1 - height));
        return new(x, y, width, height, zoom);
    }

    public static PictureView Pan(PictureView current, double deltaX, double deltaY)
    {
        if (current.Zoom <= 1.01) return current;
        var x = Math.Clamp(current.X - deltaX * current.Width, 0, Math.Max(0, 1 - current.Width));
        var y = Math.Clamp(current.Y - deltaY * current.Height, 0, Math.Max(0, 1 - current.Height));
        return current with { X = x, Y = y };
    }

    public static (double X, double Y) ToDisplay(PictureView view, double localX, double localY)
        => (Math.Clamp(view.X + Math.Clamp(localX, 0, 1) * view.Width, 0, 1), Math.Clamp(view.Y + Math.Clamp(localY, 0, 1) * view.Height, 0, 1));

    public static bool Same(PictureView left, PictureView right)
        => Math.Abs(left.X - right.X) < 0.0005 && Math.Abs(left.Y - right.Y) < 0.0005
           && Math.Abs(left.Width - right.Width) < 0.0005 && Math.Abs(left.Height - right.Height) < 0.0005;

    public static bool CanPan(PictureView view) => view.Zoom > 1.01;

    public static string Notice(PictureView view)
        => view.Zoom <= 1.01
            ? "확대를 원래대로 되돌렸습니다."
            : "화면을 " + view.Zoom.ToString("0.0", CultureInfo.InvariantCulture) + "배로 확대했습니다. 휠 버튼으로 끌면 보이는 영역을 옮깁니다.";
}
