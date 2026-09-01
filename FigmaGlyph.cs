using System.Drawing.Drawing2D;

namespace Yako.Screenshot;

/// <summary>
/// The Figma mark, drawn with GDI+ paths so the project still needs no binary assets.
/// Built on the logo's native 38x57 unit grid of 9.5-unit radii, then transformed to fit.
///
/// The mark is Figma's trademark. It is used here only to label the button that produces
/// their paste format, and implies no endorsement or affiliation.
/// </summary>
internal static class FigmaGlyph
{
    /// <summary>Width / height of the mark, for laying out a box to draw it in.</summary>
    public const double AspectRatio = 38.0 / 57.0;

    private static readonly Color Orange = Color.FromArgb(0xF2, 0x4E, 0x1E);
    private static readonly Color Pink = Color.FromArgb(0xFF, 0x72, 0x62);
    private static readonly Color Purple = Color.FromArgb(0xA2, 0x59, 0xFF);
    private static readonly Color Blue = Color.FromArgb(0x1A, 0xBC, 0xFE);
    private static readonly Color Green = Color.FromArgb(0x0A, 0xCF, 0x83);

    public static void Draw(Graphics g, Rectangle bounds)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Draw in logo units and let the transform do the scaling.
        float scale = bounds.Height / 57f;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(scale, scale);

        LeftRounded(g, Orange, top: 0f);    // top-left
        RightRounded(g, Pink, top: 0f);     // top-right
        LeftRounded(g, Purple, top: 19f);   // middle-left

        using (var brush = new SolidBrush(Blue))
            g.FillEllipse(brush, 19f, 19f, 19f, 19f);   // middle-right

        BottomLeft(g, Green);

        g.Restore(state);
    }

    /// <summary>A square whose left edge is a semicircle bulging outwards.</summary>
    private static void LeftRounded(Graphics g, Color color, float top)
    {
        using var path = new GraphicsPath();
        path.AddArc(0f, top, 19f, 19f, 270f, -180f);        // top -> left -> bottom
        path.AddLine(9.5f, top + 19f, 19f, top + 19f);
        path.AddLine(19f, top + 19f, 19f, top);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Mirror of <see cref="LeftRounded"/>, in the right-hand column.</summary>
    private static void RightRounded(Graphics g, Color color, float top)
    {
        using var path = new GraphicsPath();
        path.AddArc(19f, top, 19f, 19f, 270f, 180f);        // top -> right -> bottom
        path.AddLine(28.5f, top + 19f, 19f, top + 19f);
        path.AddLine(19f, top + 19f, 19f, top);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>The descender: rounded top-left corner, fully round bottom.</summary>
    private static void BottomLeft(Graphics g, Color color)
    {
        using var path = new GraphicsPath();
        path.AddArc(0f, 38f, 19f, 19f, 270f, -90f);         // top -> left (quarter)
        path.AddArc(0f, 38f, 19f, 19f, 180f, -180f);        // left -> bottom -> right
        path.AddLine(19f, 47.5f, 19f, 38f);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }
}
