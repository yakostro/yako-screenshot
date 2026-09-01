using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Yako.Screenshot;

/// <summary>One physical display: its bounds in virtual-screen pixels and its DPI scale.</summary>
internal sealed record MonitorInfo(Rectangle Bounds, double Scale);

/// <summary>A frozen snapshot of the whole desktop plus the DPI facts needed to interpret it.</summary>
internal sealed class CaptureResult : IDisposable
{
    public required Bitmap Image { get; init; }

    /// <summary>Virtual-screen rect of the captured image. The origin is often negative.</summary>
    public required Rectangle VirtualBounds { get; init; }

    public required MonitorInfo[] Monitors { get; init; }

    public void Dispose() => Image.Dispose();
}

internal static class ScreenCapture
{
    /// <summary>
    /// Bounding box of every display, in physical pixels. With a display placed left of or
    /// above the primary one, X and/or Y are negative - every coordinate downstream is
    /// relative to this rect, never to the primary monitor.
    /// </summary>
    public static Rectangle GetVirtualBounds() => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    public static CaptureResult Capture()
    {
        var bounds = GetVirtualBounds();
        var bmp = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppRgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        }
        catch
        {
            bmp.Dispose();
            throw;
        }

        return new CaptureResult { Image = bmp, VirtualBounds = bounds, Monitors = GetMonitors() };
    }

    public static MonitorInfo[] GetMonitors()
    {
        var found = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref Native.RECT clip, IntPtr data)
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(hMonitor, ref mi))
                found.Add(new MonitorInfo(mi.rcMonitor.ToRectangle(), ScaleOf(hMonitor)));
            return true;
        }

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // A machine always has at least one display; if enumeration somehow fails, assume 100%
        // over the whole virtual desktop rather than dividing by a bogus factor.
        return found.Count > 0
            ? found.ToArray()
            : new[] { new MonitorInfo(GetVirtualBounds(), 1.0) };
    }

    private static double ScaleOf(IntPtr hMonitor)
    {
        if (Native.GetDpiForMonitor(hMonitor, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;
        return 1.0;
    }
}
