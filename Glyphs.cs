using System.Drawing.Drawing2D;

namespace Yako.Screenshot;

/// <summary>
/// Toolbar icons drawn from GDI+ paths on a 24x24 unit grid, so they stay crisp at any
/// monitor scale and the project still ships no binary assets.
/// </summary>
internal static class Glyphs
{
    private const float Grid = 24f;

    /// <summary>Two offset sheets: the classic copy mark.</summary>
    public static void DrawCopy(Graphics g, Rectangle bounds, Color stroke, Color behind)
    {
        var state = Begin(g, bounds, out float unit);
        using var pen = new Pen(stroke, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Back sheet first, then the front one filled with the button colour so it reads
        // as sitting on top rather than as a transparent overlap.
        using (var back = RoundedRect(3f, 3f, 14f, 14f, 3f))
            g.DrawPath(pen, back);

        using (var front = RoundedRect(7f, 7f, 14f, 14f, 3f))
        {
            using var fill = new SolidBrush(behind);
            g.FillPath(fill, front);
            g.DrawPath(pen, front);
        }

        End(g, state, unit);
    }

    /// <summary>A pencil, tip pointing up-right: the annotate tool.</summary>
    public static void DrawPencil(Graphics g, Rectangle bounds, Color stroke)
    {
        var state = Begin(g, bounds, out float unit);
        g.TranslateTransform(12f, 12f);
        g.RotateTransform(-45f);
        g.TranslateTransform(-12f, -12f);

        using var pen = new Pen(stroke, 1.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Body: flat tail, long shaft, a point on the right.
        using (var body = new GraphicsPath())
        {
            body.AddLine(2f, 9f, 13f, 9f);
            body.AddLine(13f, 9f, 18.5f, 12f);
            body.AddLine(18.5f, 12f, 13f, 15f);
            body.AddLine(13f, 15f, 2f, 15f);
            body.CloseFigure();
            g.DrawPath(pen, body);
        }

        // Eraser/ferrule seam, then the wood/graphite seam inside the point.
        g.DrawLine(pen, 5f, 9f, 5f, 15f);
        g.DrawLine(pen, 15f, 10.2f, 15f, 13.8f);

        End(g, state, unit);
    }

    /// <summary>An arrow dropping into a tray: save to a file.</summary>
    public static void DrawSave(Graphics g, Rectangle bounds, Color stroke)
    {
        var state = Begin(g, bounds, out float unit);
        using var pen = new Pen(stroke, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        // tray
        using (var tray = new GraphicsPath())
        {
            tray.AddLine(4f, 15f, 4f, 19.5f);
            tray.AddLine(4f, 19.5f, 20f, 19.5f);
            tray.AddLine(20f, 19.5f, 20f, 15f);
            g.DrawPath(pen, tray);
        }

        // shaft and arrowhead
        g.DrawLine(pen, 12f, 3.5f, 12f, 14.5f);
        using (var head = new GraphicsPath())
        {
            head.AddLine(7.5f, 10f, 12f, 14.5f);
            head.AddLine(12f, 14.5f, 16.5f, 10f);
            g.DrawPath(pen, head);
        }

        End(g, state, unit);
    }

    private static GraphicsState Begin(Graphics g, Rectangle bounds, out float unit)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        unit = bounds.Height / Grid;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(unit, unit);
        return state;
    }

    private static void End(Graphics g, GraphicsState state, float unit) => g.Restore(state);

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2f;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180f, 90f);
        path.AddArc(x + w - d, y, d, d, 270f, 90f);
        path.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
        path.AddArc(x, y + h - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}
