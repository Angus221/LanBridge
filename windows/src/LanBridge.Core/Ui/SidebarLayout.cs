namespace LanBridge.Core;

public static class SidebarLayout
{
    public const double PanelWidth = 392;
    public const double HandleWidth = 18;
    public const double HandleHeight = 84;
    public static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

    public readonly record struct Placement(double Left, double Top, double Width, double Height);

    public static Placement Place(double workLeft, double workTop, double workWidth, double workHeight, bool shown)
    {
        var width = shown ? PanelWidth : HandleWidth;
        var right = workLeft + workWidth;
        return new Placement(right - width, workTop, width, Math.Max(HandleHeight, workHeight));
    }

    public static double HandleTop(double workTop, double workHeight)
        => workTop + Math.Max(0, (workHeight - HandleHeight) / 2);

    public static (double X, double Y, double Width, double Height) ToDips(
        double pxX, double pxY, double pxW, double pxH, double dpiScale)
    {
        var scale = dpiScale <= 0 ? 1 : dpiScale;
        return (pxX / scale, pxY / scale, pxW / scale, pxH / scale);
    }

    public static bool NearRightEdge(double cursorX, double areaLeft, double areaWidth, double threshold)
        => cursorX >= areaLeft + areaWidth - threshold && cursorX < areaLeft + areaWidth + 2;

    public static TimeSpan Duration(bool reducedMotion) => reducedMotion ? TimeSpan.Zero : SlideDuration;
}
