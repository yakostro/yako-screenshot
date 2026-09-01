using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Yako.Screenshot;

/// <summary>
/// The heart of the tool: turns a physical-pixel selection into an image sized as if
/// Windows UI scaling were 100%, so a paste into Figma measures what the design measures.
/// </summary>
internal static class DpiScaler
{
    /// <summary>
    /// The scale to divide by. A selection straddling two displays of different DPI has no
    /// single right answer, so the display covering most of it wins - predictable beats clever.
    /// </summary>
    public static double ScaleForSelection(Rectangle selectionInVirtualCoords, MonitorInfo[] monitors)
    {
        if (monitors.Length == 0) return 1.0;

        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            var hit = Rectangle.Intersect(m.Bounds, selectionInVirtualCoords);
            long area = (long)hit.Width * hit.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = m;
            }
        }
        if (best is not null) return best.Scale;

        // Zero-area selection (a click, or a 1px line): fall back to the nearest display centre.
        var c = new Point(
            selectionInVirtualCoords.X + selectionInVirtualCoords.Width / 2,
            selectionInVirtualCoords.Y + selectionInVirtualCoords.Height / 2);

        MonitorInfo nearest = monitors[0];
        long nearestDist = long.MaxValue;
        foreach (var m in monitors)
        {
            long dx = c.X - (m.Bounds.X + m.Bounds.Width / 2);
            long dy = c.Y - (m.Bounds.Y + m.Bounds.Height / 2);
            long dist = dx * dx + dy * dy;
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = m;
            }
        }
        return nearest.Scale;
    }

    /// <summary>
    /// The size a selection represents once the Windows UI scale is divided out - i.e. what
    /// it measures on screen in design units. Nothing is resampled to get this; it is the
    /// number the SVG wrapper declares so a paste lands at 100%.
    /// </summary>
    public static Size LogicalSize(Size physical, double scale)
    {
        if (scale <= 0 || Math.Abs(scale - 1.0) < 0.0005) return physical;
        return new Size(
            Math.Max(1, (int)Math.Round(physical.Width / scale)),
            Math.Max(1, (int)Math.Round(physical.Height / scale)));
    }

    /// <summary>
    /// Crops the capture to the selection. No resampling of any kind: the result is
    /// byte-identical to the pixels on screen, which is the whole point - averaging them
    /// down to 1x threw away three quarters of the samples and no filter could give them back.
    /// </summary>
    public static Bitmap Crop(Bitmap source, Rectangle virtualBounds, Rectangle selectionInVirtualCoords)
    {
        // The selection is in virtual coordinates; the bitmap starts at the virtual origin.
        var crop = new Rectangle(
            selectionInVirtualCoords.X - virtualBounds.X,
            selectionInVirtualCoords.Y - virtualBounds.Y,
            selectionInVirtualCoords.Width,
            selectionInVirtualCoords.Height);

        crop = Rectangle.Intersect(crop, new Rectangle(0, 0, source.Width, source.Height));
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            crop = new Rectangle(
                Math.Clamp(crop.X, 0, source.Width - 1),
                Math.Clamp(crop.Y, 0, source.Height - 1),
                1, 1);
        }

        var result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(source, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
        return result;
    }
}
