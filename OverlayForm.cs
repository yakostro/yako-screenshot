using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Yako.Screenshot;

/// <summary>
/// A single borderless window covering the whole virtual desktop, showing the frozen
/// capture under a dim veil. Deliberately has no child controls - WinForms would DPI-scale
/// them per monitor, so every piece of chrome is drawn and hit-tested by hand in device pixels.
/// </summary>
internal sealed class OverlayForm : Form
{
    private enum Phase { Idle, Dragging, Adjusting }

    private enum Grip { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    private enum ButtonId { CopySvg, Copy, Save }

    /// <summary>Named ButtonIcon, not Icon, so it does not shadow Form.Icon.</summary>
    private enum ButtonIcon { Figma, Copy, Save }

    private const int WM_DPICHANGED = 0x02E0;
    private const int MinDragPixels = 4;
    private const int TooltipDelayMs = 1000;

    private static readonly Color Accent = Color.FromArgb(0x4C, 0x9A, 0xFF);
    private static readonly Color ChromeBack = Color.FromArgb(240, 26, 26, 30);
    private static readonly Color ChromeText = Color.FromArgb(245, 245, 247);
    private static readonly Color ChromeHint = Color.FromArgb(160, 162, 172);
    private static readonly Color ButtonBack = Color.FromArgb(255, 50, 50, 58);
    private static readonly Color ButtonHover = Color.FromArgb(255, 72, 72, 84);

    private readonly CaptureResult _capture;
    private readonly Settings _settings;
    private readonly Dictionary<int, Font> _fonts = new();
    private readonly List<(Rectangle Rect, ButtonId Id)> _buttons = new();

    private Bitmap? _dimmed;
    private Phase _phase = Phase.Idle;
    private Rectangle _sel;          // client coordinates
    private Point _mouse;
    private Point _dragStart;
    private Grip _activeGrip = Grip.None;
    private Rectangle _gripStartSel;
    private Point _gripStartMouse;
    private Rectangle _barRect;
    private ButtonId? _hoverButton;

    // Hover highlighting is instant, but the tooltip waits: it is a reminder for when you
    // pause, not something that should flash past every time the cursor crosses the bar.
    private readonly System.Windows.Forms.Timer _tooltipTimer;
    private ButtonId? _tooltipFor;

    private bool _saveDialogOpen;

    public OverlayForm(CaptureResult capture, Settings settings)
    {
        _capture = capture;
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Cursor = Cursors.Cross;
        Bounds = capture.VirtualBounds;

        // Pure arithmetic, so this works before the handle exists.
        _mouse = new Point(Cursor.Position.X - capture.VirtualBounds.X, Cursor.Position.Y - capture.VirtualBounds.Y);

        _tooltipTimer = new System.Windows.Forms.Timer { Interval = TooltipDelayMs };
        _tooltipTimer.Tick += (_, _) =>
        {
            _tooltipTimer.Stop();
            if (_hoverButton is null) return;
            _tooltipFor = _hoverButton;
            Invalidate();
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _capture.VirtualBounds; // re-assert, in case creation nudged us
        Native.SetForegroundWindow(Handle);
        Activate();
        Focus();
    }

    protected override void WndProc(ref Message m)
    {
        // The window intentionally spans monitors of differing DPI. Letting WinForms react
        // to WM_DPICHANGED would resize and rescale it out from under us.
        if (m.Msg == WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    // ---------------- coordinate + DPI helpers ----------------

    private Point ToVirtual(Point p) => new(p.X + _capture.VirtualBounds.X, p.Y + _capture.VirtualBounds.Y);

    private Rectangle ToVirtual(Rectangle r) =>
        new(r.X + _capture.VirtualBounds.X, r.Y + _capture.VirtualBounds.Y, r.Width, r.Height);

    /// <summary>Scale of the display under a client point - chrome is sized with this so it
    /// looks physically identical on a 100% and a 175% monitor.</summary>
    private double ScaleAt(Point clientPoint)
    {
        var v = ToVirtual(clientPoint);
        foreach (var m in _capture.Monitors)
            if (m.Bounds.Contains(v))
                return m.Scale;
        return _capture.Monitors.Length > 0 ? _capture.Monitors[0].Scale : 1.0;
    }

    private double SelectionScale() => DpiScaler.ScaleForSelection(ToVirtual(_sel), _capture.Monitors);

    private Font FontPx(double px, FontStyle style = FontStyle.Regular)
    {
        int size = Math.Max(9, (int)Math.Round(px));
        int key = size * 16 + (int)style;
        if (!_fonts.TryGetValue(key, out var f))
        {
            f = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            _fonts[key] = f;
        }
        return f;
    }

    private static int Px(double logical, double scale) => Math.Max(1, (int)Math.Round(logical * scale));

    // ---------------- painting ----------------

    private Bitmap Dimmed => _dimmed ??= BuildDimmed();

    private Bitmap BuildDimmed()
    {
        // Baking the veil once keeps every repaint down to two straight blits, which matters
        // while dragging across a large multi-monitor desktop.
        var b = new Bitmap(_capture.Image.Width, _capture.Image.Height, _capture.Image.PixelFormat);
        using var g = Graphics.FromImage(b);
        g.DrawImageUnscaled(_capture.Image, 0, 0);
        using var veil = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        g.FillRectangle(veil, 0, 0, b.Width, b.Height);
        return b;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Fully covered by OnPaint; skipping this avoids a black flash.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        g.DrawImageUnscaled(Dimmed, 0, 0);

        bool hasSelection = _phase != Phase.Idle && _sel.Width > 0 && _sel.Height > 0;
        if (hasSelection)
        {
            // The selection shows the untouched capture: clip, then blit 1:1 for exact pixels.
            g.SetClip(_sel);
            g.DrawImageUnscaled(_capture.Image, 0, 0);
            g.ResetClip();
        }

        if (_phase == Phase.Idle)
        {
            DrawCursorLabel(g);
            return;
        }

        double scale = SelectionScale();
        using (var pen = new Pen(Accent, Px(1, scale)))
            g.DrawRectangle(pen, _sel.X, _sel.Y, Math.Max(1, _sel.Width - 1), Math.Max(1, _sel.Height - 1));

        DrawSizeBadge(g, scale);

        if (_phase == Phase.Adjusting)
        {
            DrawGrips(g, scale);
            EnsureToolbarLayout(scale);
            DrawToolbar(g, scale);
        }
    }

    private void DrawCursorLabel(Graphics g)
    {
        double scale = ScaleAt(_mouse);
        var font = FontPx(13 * scale);
        const string text = "Select an area";
        var textSize = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);

        int padX = Px(10, scale), padY = Px(7, scale);
        var size = new Size(textSize.Width + padX * 2, textSize.Height + padY * 2);
        var pos = new Point(_mouse.X + Px(18, scale), _mouse.Y + Px(20, scale));

        // Flip to the other side of the cursor rather than run off the edge of the display.
        var host = MonitorClientRectAt(_mouse);
        if (pos.X + size.Width > host.Right) pos.X = _mouse.X - Px(18, scale) - size.Width;
        if (pos.Y + size.Height > host.Bottom) pos.Y = _mouse.Y - Px(20, scale) - size.Height;
        pos.X = Math.Max(host.Left, pos.X);
        pos.Y = Math.Max(host.Top, pos.Y);

        var rect = new Rectangle(pos, size);
        FillRounded(g, rect, Px(6, scale), ChromeBack);
        TextRenderer.DrawText(g, text, font,
            new Rectangle(rect.X + padX, rect.Y + padY, textSize.Width, textSize.Height),
            ChromeText, TextFormatFlags.NoPadding);
    }

    private void DrawSizeBadge(Graphics g, double scale)
    {
        // Primary number is what Copy and Save write out: every captured pixel.
        string main = _sel.Width + " × " + _sel.Height;

        // Secondary is what the Figma button produces - the same pixels, declared at the size
        // they occupy on screen once the Windows UI scale is divided out.
        string? hint = null;
        if (scale > 1.0)
        {
            var logical = LogicalSize();
            hint = logical.Width + " × " + logical.Height + " @1×";
        }

        var mainFont = FontPx(13 * scale, FontStyle.Bold);
        var hintFont = FontPx(11 * scale);
        var flags = TextFormatFlags.NoPadding;
        var max = new Size(int.MaxValue, int.MaxValue);

        var mainSize = TextRenderer.MeasureText(main, mainFont, max, flags);
        var hintSize = hint is null ? Size.Empty : TextRenderer.MeasureText(hint, hintFont, max, flags);

        int padX = Px(8, scale), padY = Px(5, scale), lineGap = hint is null ? 0 : Px(2, scale);
        var size = new Size(
            Math.Max(mainSize.Width, hintSize.Width) + padX * 2,
            mainSize.Height + lineGap + hintSize.Height + padY * 2);

        int gap = Px(8, scale);
        int x = _sel.X;
        int y = _sel.Y - size.Height - gap;
        if (y < ClientRectangle.Top) y = _sel.Y + gap;   // no room above: hang inside
        x = Math.Clamp(x, ClientRectangle.Left, Math.Max(ClientRectangle.Left, ClientRectangle.Right - size.Width));

        var rect = new Rectangle(x, y, size.Width, size.Height);
        FillRounded(g, rect, Px(5, scale), ChromeBack);
        TextRenderer.DrawText(g, main, mainFont,
            new Rectangle(rect.X + padX, rect.Y + padY, mainSize.Width, mainSize.Height),
            ChromeText, flags);

        if (hint is not null)
        {
            TextRenderer.DrawText(g, hint, hintFont,
                new Rectangle(rect.X + padX, rect.Y + padY + mainSize.Height + lineGap, hintSize.Width, hintSize.Height),
                ChromeHint, flags);
        }
    }

    private (Grip Kind, Rectangle Rect)[] GripRects(double scale)
    {
        int s = Math.Max(6, Px(9, scale));
        int half = s / 2;
        int l = _sel.Left, t = _sel.Top, r = _sel.Right - 1, b = _sel.Bottom - 1;
        int mx = l + (r - l) / 2, my = t + (b - t) / 2;

        (Grip, Point)[] anchors =
        {
            (Grip.TopLeft, new Point(l, t)),
            (Grip.Top, new Point(mx, t)),
            (Grip.TopRight, new Point(r, t)),
            (Grip.Right, new Point(r, my)),
            (Grip.BottomRight, new Point(r, b)),
            (Grip.Bottom, new Point(mx, b)),
            (Grip.BottomLeft, new Point(l, b)),
            (Grip.Left, new Point(l, my)),
        };

        var result = new (Grip, Rectangle)[anchors.Length];
        for (int i = 0; i < anchors.Length; i++)
        {
            var p = anchors[i].Item2;
            result[i] = (anchors[i].Item1, new Rectangle(p.X - half, p.Y - half, s, s));
        }
        return result;
    }

    private void DrawGrips(Graphics g, double scale)
    {
        using var fill = new SolidBrush(Color.White);
        using var edge = new Pen(Color.FromArgb(230, 20, 20, 24), Px(1, scale));
        foreach (var (_, rect) in GripRects(scale))
        {
            g.FillRectangle(fill, rect);
            g.DrawRectangle(edge, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }
    }

    private readonly record struct ButtonSpec(ButtonId Id, string Text, ButtonIcon Icon, string[] Tooltip);

    // Figma first: it is the reason this tool exists, so it takes the leftmost slot.
    private static readonly ButtonSpec[] Buttons =
    {
        new(ButtonId.CopySvg, "Copy for Figma", ButtonIcon.Figma, new[]
        {
            "Pastes into Figma at 100%",
            "A PNG inside an SVG frame, so the Windows UI scale is ignored",
            "Ctrl+Shift+C",
        }),
        new(ButtonId.Copy, "Copy", ButtonIcon.Copy, new[]
        {
            "Copies a plain bitmap",
            "Every captured pixel, at full screen resolution",
            "Enter or Ctrl+C",
        }),
        new(ButtonId.Save, "Save…", ButtonIcon.Save, new[]
        {
            "Writes a PNG or JPEG file",
            "Every captured pixel, at full screen resolution",
            "Ctrl+S",
        }),
    };

    private static Size IconSize(double scale)
    {
        int h = Px(16, scale);
        return new Size(h, h);
    }

    private static int ButtonWidth(ButtonSpec spec, Font font, double scale)
    {
        int textW = TextRenderer
            .MeasureText(spec.Text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        return Px(12, scale) * 2 + IconSize(scale).Width + Px(7, scale) + textW;
    }

    private void EnsureToolbarLayout(double scale)
    {
        _buttons.Clear();

        var font = FontPx(13 * scale);
        int gap = Px(6, scale), barPad = Px(6, scale);
        int btnH = Px(32, scale);

        var widths = new int[Buttons.Length];
        int total = 0;
        for (int i = 0; i < Buttons.Length; i++)
        {
            widths[i] = ButtonWidth(Buttons[i], font, scale);
            total += widths[i];
        }
        total += gap * (Buttons.Length - 1);

        int barW = total + barPad * 2, barH = btnH + barPad * 2;
        int margin = Px(10, scale);

        int y = _sel.Bottom + margin;
        if (y + barH > ClientRectangle.Bottom) y = _sel.Top - barH - margin;                          // flip above
        if (y < ClientRectangle.Top) y = Math.Max(ClientRectangle.Top, _sel.Bottom - barH - margin);  // or sit inside
        int x = Math.Clamp(_sel.Right - barW, ClientRectangle.Left, Math.Max(ClientRectangle.Left, ClientRectangle.Right - barW));

        _barRect = new Rectangle(x, y, barW, barH);

        int cx = x + barPad;
        for (int i = 0; i < Buttons.Length; i++)
        {
            _buttons.Add((new Rectangle(cx, y + barPad, widths[i], btnH), Buttons[i].Id));
            cx += widths[i] + gap;
        }
    }

    private void DrawToolbar(Graphics g, double scale)
    {
        FillRounded(g, _barRect, Px(8, scale), ChromeBack);

        var font = FontPx(13 * scale);
        var icon = IconSize(scale);
        int padH = Px(12, scale), iconGap = Px(7, scale);

        foreach (var (rect, id) in _buttons)
        {
            var spec = Array.Find(Buttons, b => b.Id == id);
            bool hover = _hoverButton == id;
            Color back = hover ? ButtonHover : ButtonBack;

            if (_tooltipFor == id) DrawTooltip(g, scale, rect, spec.Tooltip);

            FillRounded(g, rect, Px(6, scale), back);

            var iconRect = new Rectangle(
                rect.X + padH,
                rect.Y + (rect.Height - icon.Height) / 2,
                icon.Width, icon.Height);

            switch (spec.Icon)
            {
                case ButtonIcon.Figma:
                    // The mark is taller than it is wide; keep its aspect inside the slot.
                    int markW = Math.Max(1, (int)Math.Round(iconRect.Height * FigmaGlyph.AspectRatio));
                    FigmaGlyph.Draw(g, new Rectangle(
                        iconRect.X + (iconRect.Width - markW) / 2, iconRect.Y, markW, iconRect.Height));
                    break;

                case ButtonIcon.Copy:
                    Glyphs.DrawCopy(g, iconRect, ChromeText, back);
                    break;

                case ButtonIcon.Save:
                    Glyphs.DrawSave(g, iconRect, ChromeText);
                    break;
            }

            int textX = iconRect.Right + iconGap;
            TextRenderer.DrawText(g, spec.Text, font,
                new Rectangle(textX, rect.Y, Math.Max(0, rect.Right - padH - textX), rect.Height),
                ChromeText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Hover label, centred above the button it describes. First line is the title.</summary>
    private void DrawTooltip(Graphics g, double scale, Rectangle button, string[] lines)
    {
        if (lines.Length == 0) return;

        var titleFont = FontPx(12 * scale);
        var bodyFont = FontPx(11 * scale);
        var flags = TextFormatFlags.NoPadding;
        var max = new Size(int.MaxValue, int.MaxValue);

        var sizes = new Size[lines.Length];
        int w = 0, h = 0, lineGap = Px(3, scale);
        for (int i = 0; i < lines.Length; i++)
        {
            sizes[i] = TextRenderer.MeasureText(lines[i], i == 0 ? titleFont : bodyFont, max, flags);
            w = Math.Max(w, sizes[i].Width);
            h += sizes[i].Height + (i > 0 ? lineGap : 0);
        }

        int padX = Px(10, scale), padY = Px(7, scale);
        var size = new Size(w + padX * 2, h + padY * 2);

        int x = button.X + (button.Width - size.Width) / 2;
        int y = button.Y - size.Height - Px(8, scale);
        if (y < ClientRectangle.Top) y = button.Bottom + Px(8, scale);
        x = Math.Clamp(x, ClientRectangle.Left, Math.Max(ClientRectangle.Left, ClientRectangle.Right - size.Width));

        var rect = new Rectangle(x, y, size.Width, size.Height);
        FillRounded(g, rect, Px(6, scale), ChromeBack);

        int ty = rect.Y + padY;
        for (int i = 0; i < lines.Length; i++)
        {
            TextRenderer.DrawText(g, lines[i], i == 0 ? titleFont : bodyFont,
                new Rectangle(rect.X + padX, ty, sizes[i].Width, sizes[i].Height),
                i == 0 ? ChromeText : ChromeHint, flags);
            ty += sizes[i].Height + lineGap;
        }
    }

    private static void FillRounded(Graphics g, Rectangle rect, int radius, Color color)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        g.SmoothingMode = previous;
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = Math.Min(Math.Max(1, radius * 2), Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private Rectangle MonitorClientRectAt(Point clientPoint)
    {
        var v = ToVirtual(clientPoint);
        foreach (var m in _capture.Monitors)
        {
            if (m.Bounds.Contains(v))
            {
                return new Rectangle(
                    m.Bounds.X - _capture.VirtualBounds.X,
                    m.Bounds.Y - _capture.VirtualBounds.Y,
                    m.Bounds.Width, m.Bounds.Height);
            }
        }
        return ClientRectangle;
    }

    // ---------------- mouse ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _mouse = e.Location;

        HideTooltip();

        if (e.Button == MouseButtons.Right)
        {
            // Right-click discards the current selection and starts over.
            _phase = Phase.Idle;
            _sel = Rectangle.Empty;
            _activeGrip = Grip.None;
            _hoverButton = null;
            Cursor = Cursors.Cross;
            Invalidate();
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        if (_phase == Phase.Adjusting)
        {
            double scale = SelectionScale();
            EnsureToolbarLayout(scale);

            foreach (var (rect, id) in _buttons)
            {
                if (rect.Contains(e.Location))
                {
                    Trigger(id);
                    return;
                }
            }
            if (_barRect.Contains(e.Location)) return; // dead space inside the bar

            var grip = HitTestGrip(e.Location, scale);
            if (grip != Grip.None)
            {
                _activeGrip = grip;
                _gripStartSel = _sel;
                _gripStartMouse = e.Location;
                return;
            }
        }

        _phase = Phase.Dragging;
        _dragStart = e.Location;
        _sel = new Rectangle(e.Location, Size.Empty);
        _activeGrip = Grip.None;
        _hoverButton = null;
        Cursor = Cursors.Cross;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;

        switch (_phase)
        {
            case Phase.Idle:
                Invalidate();
                break;

            case Phase.Dragging:
                _sel = NormalizeCorners(_dragStart, e.Location);
                ClampSelection();
                Invalidate();
                break;

            case Phase.Adjusting when _activeGrip != Grip.None:
                ApplyGrip(e.Location);
                Invalidate();
                break;

            case Phase.Adjusting:
                UpdateHover(e.Location);
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        if (_activeGrip != Grip.None)
        {
            _activeGrip = Grip.None;
            UpdateHover(e.Location);
            Invalidate();
            return;
        }

        if (_phase != Phase.Dragging) return;

        // A click, or a twitch: treat as "nothing selected yet" rather than a 1px capture.
        if (_sel.Width < MinDragPixels || _sel.Height < MinDragPixels)
        {
            _phase = Phase.Idle;
            _sel = Rectangle.Empty;
            Cursor = Cursors.Cross;
        }
        else
        {
            _phase = Phase.Adjusting;
            UpdateHover(e.Location);
        }
        Invalidate();
    }

    private void UpdateHover(Point p)
    {
        double scale = SelectionScale();
        EnsureToolbarLayout(scale);

        ButtonId? hover = null;
        foreach (var (rect, id) in _buttons)
        {
            if (rect.Contains(p))
            {
                hover = id;
                break;
            }
        }

        if (hover != _hoverButton)
        {
            _hoverButton = hover;

            // Moving to another button restarts the wait rather than carrying the old
            // tooltip across, so it never describes the wrong button.
            _tooltipFor = null;
            _tooltipTimer.Stop();
            if (hover is not null) _tooltipTimer.Start();

            Invalidate();   // the tooltip is drawn outside the bar
        }

        Cursor = hover is not null || _barRect.Contains(p)
            ? Cursors.Hand
            : CursorFor(HitTestGrip(p, scale));
    }

    private void HideTooltip()
    {
        _tooltipTimer.Stop();
        if (_tooltipFor is null) return;
        _tooltipFor = null;
        Invalidate();
    }

    private Grip HitTestGrip(Point p, double scale)
    {
        int slack = Px(3, scale);
        foreach (var (kind, rect) in GripRects(scale))
        {
            var padded = rect;
            padded.Inflate(slack, slack);
            if (padded.Contains(p)) return kind;
        }
        return _sel.Contains(p) ? Grip.Move : Grip.None;
    }

    private static Cursor CursorFor(Grip grip) => grip switch
    {
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private void ApplyGrip(Point mouse)
    {
        int dx = mouse.X - _gripStartMouse.X;
        int dy = mouse.Y - _gripStartMouse.Y;

        if (_activeGrip == Grip.Move)
        {
            _sel = new Rectangle(_gripStartSel.X + dx, _gripStartSel.Y + dy, _gripStartSel.Width, _gripStartSel.Height);
            NudgeIntoView();
            return;
        }

        int l = _gripStartSel.Left, t = _gripStartSel.Top, r = _gripStartSel.Right, b = _gripStartSel.Bottom;
        switch (_activeGrip)
        {
            case Grip.TopLeft: l += dx; t += dy; break;
            case Grip.Top: t += dy; break;
            case Grip.TopRight: r += dx; t += dy; break;
            case Grip.Right: r += dx; break;
            case Grip.BottomRight: r += dx; b += dy; break;
            case Grip.Bottom: b += dy; break;
            case Grip.BottomLeft: l += dx; b += dy; break;
            case Grip.Left: l += dx; break;
        }

        // Dragging a handle past the opposite edge flips the rect, as in any graphics editor.
        _sel = new Rectangle(Math.Min(l, r), Math.Min(t, b), Math.Max(1, Math.Abs(r - l)), Math.Max(1, Math.Abs(b - t)));
        ClampSelection();
    }

    private static Rectangle NormalizeCorners(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>Clips the selection to the desktop (used while resizing).</summary>
    private void ClampSelection()
    {
        var clipped = Rectangle.Intersect(_sel, ClientRectangle);
        _sel = clipped.Width > 0 && clipped.Height > 0
            ? clipped
            : new Rectangle(
                Math.Clamp(_sel.X, 0, Math.Max(0, ClientRectangle.Width - 1)),
                Math.Clamp(_sel.Y, 0, Math.Max(0, ClientRectangle.Height - 1)), 1, 1);
    }

    /// <summary>Shifts the selection back inside the desktop, keeping its size (used while moving).</summary>
    private void NudgeIntoView()
    {
        int x = Math.Clamp(_sel.X, ClientRectangle.Left, Math.Max(ClientRectangle.Left, ClientRectangle.Right - _sel.Width));
        int y = Math.Clamp(_sel.Y, ClientRectangle.Top, Math.Max(ClientRectangle.Top, ClientRectangle.Bottom - _sel.Height));
        _sel = new Rectangle(x, y, _sel.Width, _sel.Height);
    }

    // ---------------- keyboard ----------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) == Keys.Shift;
        bool ctrl = (keyData & Keys.Control) == Keys.Control;

        switch (key)
        {
            case Keys.Escape:
                Close();
                return true;

            case Keys.C when ctrl && shift && _phase == Phase.Adjusting:
                Trigger(ButtonId.CopySvg);
                return true;

            case Keys.Return when _phase == Phase.Adjusting:
            case Keys.C when ctrl && _phase == Phase.Adjusting:
                Trigger(ButtonId.Copy);
                return true;

            case Keys.S when ctrl && _phase == Phase.Adjusting:
                Trigger(ButtonId.Save);
                return true;

            case Keys.Left or Keys.Right or Keys.Up or Keys.Down when _phase == Phase.Adjusting:
                int step = shift ? 10 : 1;
                int dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0;
                int dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;

                if (ctrl)
                {
                    // Ctrl+arrow resizes from the bottom-right corner, for pixel-exact sizing.
                    _sel = new Rectangle(_sel.X, _sel.Y, Math.Max(1, _sel.Width + dx), Math.Max(1, _sel.Height + dy));
                    ClampSelection();
                }
                else
                {
                    _sel = new Rectangle(_sel.X + dx, _sel.Y + dy, _sel.Width, _sel.Height);
                    NudgeIntoView();
                }
                Invalidate();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------- actions ----------------

    private void Trigger(ButtonId id)
    {
        switch (id)
        {
            case ButtonId.Copy:
                using (var bmp = Produce())
                    Output.CopyToClipboard(bmp);
                Close();
                break;

            case ButtonId.CopySvg:
                // The SVG carries the logical size separately, so the raster stays native.
                using (var native = Produce())
                    Output.CopyAsSvg(native, LogicalSize());
                Close();
                break;

            case ButtonId.Save:
                DoSave();
                break;
        }
    }

    private void DoSave()
    {
        if (_saveDialogOpen) return;
        _saveDialogOpen = true;
        try
        {
            // A TopMost overlay would sit in front of the dialog and look like a hang.
            Visible = false;
            using var bmp = Produce();
            if (Output.SaveToFile(bmp, _settings))
            {
                Close();
                return;
            }
            Visible = true;              // cancelled: back to adjusting the same selection
            Native.SetForegroundWindow(Handle);
        }
        finally
        {
            _saveDialogOpen = false;
        }
    }

    /// <summary>The crop, with no resampling at all - byte-identical to what is on screen.</summary>
    private Bitmap Produce() => DpiScaler.Crop(_capture.Image, _capture.VirtualBounds, ToVirtual(_sel));

    /// <summary>The size the capture represents once the Windows UI scale is divided out.</summary>
    private Size LogicalSize() => DpiScaler.LogicalSize(_sel.Size, SelectionScale());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tooltipTimer.Dispose();
            _dimmed?.Dispose();
            _dimmed = null;
            foreach (var f in _fonts.Values) f.Dispose();
            _fonts.Clear();
        }
        base.Dispose(disposing);
    }
}
