using System.Drawing.Imaging;
using System.Text;

namespace Yako.Screenshot;

/// <summary>
/// Headless check of the invariants the GUI depends on, runnable as
/// <c>Yako.Screenshot.exe --selftest</c>. Guards DPI awareness, monitor enumeration, that the
/// crop never resamples, that the Figma payload declares the right size around the right
/// pixels, and that the overlay's dirty-rect repainting lands the same pixels a full repaint
/// would. Writes a report to %TEMP% and exits non-zero on failure.
/// </summary>
internal static class SelfTest
{
    public static void Run()
    {
        var log = new StringBuilder();
        int failures = 0;

        void Check(string name, bool ok, string detail)
        {
            log.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
            if (!ok) failures++;
        }

        int awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext());
        Check("dpi awareness", awareness == Native.DPI_AWARENESS_PER_MONITOR_AWARE,
            $"awareness={awareness} (expected {Native.DPI_AWARENESS_PER_MONITOR_AWARE} = per-monitor)");

        var monitors = ScreenCapture.GetMonitors();
        var virtualBounds = ScreenCapture.GetVirtualBounds();
        log.AppendLine($"       virtual desktop = {virtualBounds}");
        foreach (var m in monitors)
            log.AppendLine($"       monitor {m.Bounds} scale={m.Scale:0.###} ({m.Scale * 100:0}%)");

        // If DPI awareness were wrong these metrics would be virtualised and would not add up.
        var union = monitors[0].Bounds;
        foreach (var m in monitors) union = Rectangle.Union(union, m.Bounds);
        Check("virtual bounds = union of monitors", union == virtualBounds, $"{union} vs {virtualBounds}");

        using var capture = ScreenCapture.Capture();
        Check("capture size = virtual desktop size",
            capture.Image.Size == virtualBounds.Size,
            $"{capture.Image.Size} vs {virtualBounds.Size}");

        foreach (var m in monitors)
        {
            var selection = new Rectangle(m.Bounds.X + 17, m.Bounds.Y + 23, 301, 187); // deliberately odd
            double scale = DpiScaler.ScaleForSelection(selection, monitors);

            // The crop must be exactly the selection - any difference means something resampled.
            using var crop = DpiScaler.Crop(capture.Image, virtualBounds, selection);
            Check($"crop keeps every pixel (scale={scale:0.##})",
                crop.Size == selection.Size,
                $"selected {selection.Width}x{selection.Height}, produced {crop.Width}x{crop.Height}");

            var logical = DpiScaler.LogicalSize(selection.Size, scale);
            var expected = scale > 1.0
                ? new Size((int)Math.Round(selection.Width / scale), (int)Math.Round(selection.Height / scale))
                : selection.Size;
            Check($"logical size divides out the UI scale (scale={scale:0.##})",
                logical == expected,
                $"{selection.Width}x{selection.Height} physical -> {logical.Width}x{logical.Height} @1x");
        }

        var probe = new Rectangle(monitors[0].Bounds.X + 40, monitors[0].Bounds.Y + 40, 240, 160);
        double probeScale = DpiScaler.ScaleForSelection(probe, monitors);

        // Plain Copy: a bitmap keeping every pixel, surviving a clipboard round-trip.
        using (var bmp = DpiScaler.Crop(capture.Image, virtualBounds, probe))
        {
            var native = bmp.Size;
            if (Output.CopyToClipboard(bmp))
            {
                using var back = Clipboard.GetImage();
                Check("clipboard bitmap keeps native size", back is not null && back.Size == native,
                    $"expected {native.Width}x{native.Height}, got {(back is null ? "null" : $"{back.Width}x{back.Height}")}");
            }
            else
            {
                Check("clipboard bitmap keeps native size", false, "clipboard was not writable");
            }
        }

        // Copy for Figma: native raster, logical size declared around it.
        using (var bmp = DpiScaler.Crop(capture.Image, virtualBounds, probe))
        {
            var logical = DpiScaler.LogicalSize(probe.Size, probeScale);
            if (Output.CopyAsSvg(bmp, logical))
            {
                string svg = Clipboard.GetText();
                bool declares = svg.Contains($"width=\"{logical.Width}\" height=\"{logical.Height}\"");
                Check("svg declares the logical size", declares,
                    $"expected width=\"{logical.Width}\" height=\"{logical.Height}\"");

                Size embedded = Size.Empty;
                int marker = svg.IndexOf("base64,", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    int from = marker + "base64,".Length;
                    int to = svg.IndexOf('"', from);
                    if (to > from)
                    {
                        var bytes = Convert.FromBase64String(svg[from..to]);
                        using var ms = new MemoryStream(bytes);
                        using var img = Image.FromStream(ms);
                        embedded = img.Size;
                    }
                }
                Check("svg embeds the native raster", embedded == bmp.Size,
                    $"expected {bmp.Width}x{bmp.Height}, got {embedded.Width}x{embedded.Height}");

                Check("svg payload carries no bitmap format", !Clipboard.ContainsImage(),
                    "a bitmap alongside the SVG would be preferred by Figma, defeating the point");
            }
            else
            {
                Check("svg declares the logical size", false, "clipboard was not writable");
            }
        }

        // ---- overlay painting ----
        // The overlay repaints only the rect a state change dirtied, because it spans the whole
        // virtual desktop and redrawing all of it per mouse move stuttered. That is only correct
        // if the result is indistinguishable from redrawing everything, so: drive a run of state
        // changes twice - once repainting just the dirty rect, once repainting the lot - and
        // require the two images to come out byte-identical.
        {
            // A synthetic desktop: negative origin and two different scales, so coordinate
            // translation and the DPI-sized chrome are both in play, over a high-frequency
            // pattern where a blit landing a pixel out cannot hide.
            var bounds = new Rectangle(-300, -100, 1600, 1000);
            var synthetic = new[]
            {
                new MonitorInfo(new Rectangle(-300, -100, 800, 1000), 1.0),
                new MonitorInfo(new Rectangle(500, -100, 800, 1000), 1.5),
            };

            var pattern = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var pg = Graphics.FromImage(pattern))
            {
                pg.Clear(Color.FromArgb(40, 90, 160));
                using var light = new SolidBrush(Color.FromArgb(230, 215, 120));
                using var dark = new SolidBrush(Color.FromArgb(90, 30, 60));
                for (int x = 0; x < bounds.Width; x += 16) pg.FillRectangle(light, x, 0, 7, bounds.Height);
                for (int y = 0; y < bounds.Height; y += 24) pg.FillRectangle(dark, 0, y, bounds.Width, 5);
            }

            using var synthCapture = new CaptureResult
            {
                Image = pattern, VirtualBounds = bounds, Monitors = synthetic,
            };
            using var overlay = new OverlayForm(synthCapture, new Settings());

            var whole = new Rectangle(0, 0, bounds.Width, bounds.Height);
            using var incremental = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using var reference = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using var gi = Graphics.FromImage(incremental);
            using var gr = Graphics.FromImage(reference);

            // Both images start from the same full paint of the same state.
            overlay.IdleAtForTest(new Point(120, 90));
            overlay.PaintForTest(gi, whole);
            overlay.PaintForTest(gr, whole);

            (string Name, Func<Rectangle> Apply)[] steps =
            {
                ("cursor label moves", () => overlay.IdleAtForTest(new Point(126, 97))),
                ("cursor label jumps", () => overlay.IdleAtForTest(new Point(700, 620))),
                ("first selection", () => overlay.SelectForTest(new Rectangle(100, 80, 300, 200), false)),
                ("selection grows", () => overlay.SelectForTest(new Rectangle(100, 80, 330, 230), false)),
                ("selection shrinks", () => overlay.SelectForTest(new Rectangle(100, 80, 140, 90), false)),
                ("selection moves onto the 1.5x display", () => overlay.SelectForTest(new Rectangle(900, 400, 140, 90), false)),
                ("tooltip appears", () => overlay.SelectForTest(new Rectangle(900, 400, 140, 90), true)),
                ("tooltip goes away", () => overlay.SelectForTest(new Rectangle(900, 400, 140, 90), false)),
                ("toolbar flips above a bottom-edge selection", () => overlay.SelectForTest(new Rectangle(950, 870, 400, 120), false)),
                ("badge hangs inside a top-edge selection", () => overlay.SelectForTest(new Rectangle(40, 2, 500, 300), false)),
                ("selection spans both displays", () => overlay.SelectForTest(new Rectangle(600, 200, 600, 400), false)),
                ("back to idle", () => overlay.IdleAtForTest(new Point(400, 500))),
            };

            string mismatch = "";
            foreach (var (name, apply) in steps)
            {
                var dirty = apply();
                overlay.PaintForTest(gi, dirty);      // only what the change dirtied
                overlay.PaintForTest(gr, whole);      // the whole desktop
                var diff = Difference(incremental, reference);
                if (diff.Pixels > 0)
                {
                    mismatch = $"'{name}' left {diff.Pixels} stale pixel(s) in {diff.Box}, "
                        + $"off by up to {diff.MaxDelta} (dirty rect was {dirty})";
                    break;
                }
            }
            Check("dirty-rect repaint matches a full repaint", mismatch.Length == 0,
                mismatch.Length == 0 ? $"{steps.Length} state changes, pixel-identical" : mismatch);

            // Painting in pieces must also compose: the clip rect is whatever Windows hands us.
            using (var tiled = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb))
            using (var gt = Graphics.FromImage(tiled))
            {
                overlay.SelectForTest(new Rectangle(300, 250, 700, 400), true);
                overlay.PaintForTest(gr, whole);
                for (int ty = 0; ty < bounds.Height; ty += 137)
                    for (int tx = 0; tx < bounds.Width; tx += 211)
                        overlay.PaintForTest(gt, new Rectangle(tx, ty, 211, 137));

                var seam = Difference(tiled, reference);
                Check("tiled repaint matches a full repaint", seam.Pixels == 0,
                    seam.Pixels == 0
                        ? "any clip Windows hands us composes to the same image"
                        : $"{seam.Pixels} pixels differ by up to {seam.MaxDelta} in {seam.Box}");
            }

            // The selection must show the capture untouched and everything else the veil - the
            // two halves of what dimming used to bake into a second full-desktop bitmap.
            var sel = new Rectangle(200, 150, 200, 120);
            overlay.SelectForTest(sel, false);
            overlay.PaintForTest(gr, whole);

            var inside = new Point(sel.X + 97, sel.Y + 61);
            var outside = new Point(60, 40);

            Color capturedInside = pattern.GetPixel(inside.X, inside.Y);
            Color paintedInside = reference.GetPixel(inside.X, inside.Y);
            Check("selection shows the capture untouched",
                (paintedInside.ToArgb() & 0xFFFFFF) == (capturedInside.ToArgb() & 0xFFFFFF),
                $"captured {capturedInside.R},{capturedInside.G},{capturedInside.B} -> painted {paintedInside.R},{paintedInside.G},{paintedInside.B}");

            Color capturedOutside = pattern.GetPixel(outside.X, outside.Y);
            Color paintedOutside = reference.GetPixel(outside.X, outside.Y);
            double keep = 1.0 - 120 / 255.0;
            bool veiled =
                Math.Abs(paintedOutside.R - capturedOutside.R * keep) <= 2 &&
                Math.Abs(paintedOutside.G - capturedOutside.G * keep) <= 2 &&
                Math.Abs(paintedOutside.B - capturedOutside.B * keep) <= 2;
            Check("everything outside the selection is veiled", veiled,
                $"captured {capturedOutside.R},{capturedOutside.G},{capturedOutside.B} -> painted {paintedOutside.R},{paintedOutside.G},{paintedOutside.B}");
        }

        log.AppendLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yako-selftest.txt");
        File.WriteAllText(path, log.ToString());
        Console.Out.Write(log.ToString());
        Console.Out.Write($"report: {path}\n");
        Console.Out.Flush();
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private readonly record struct Diff(int Pixels, int MaxDelta, Rectangle Box);

    /// <summary>How two same-sized bitmaps differ: pixel count, worst channel, bounding box.</summary>

    private static Diff Difference(Bitmap a, Bitmap b)
    {
        var area = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var db = b.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var rowA = new int[area.Width];
        var rowB = new int[area.Width];
        int count = 0, max = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        try
        {
            for (int y = 0; y < area.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(da.Scan0 + y * da.Stride, rowA, 0, area.Width);
                System.Runtime.InteropServices.Marshal.Copy(db.Scan0 + y * db.Stride, rowB, 0, area.Width);
                for (int x = 0; x < area.Width; x++)
                {
                    int pa = rowA[x], pb = rowB[x];
                    if ((pa & 0x00FFFFFF) == (pb & 0x00FFFFFF)) continue;
                    count++;
                    for (int shift = 0; shift <= 16; shift += 8)
                        max = Math.Max(max, Math.Abs(((pa >> shift) & 0xFF) - ((pb >> shift) & 0xFF)));
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }
            }
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
        return new Diff(count, max, count == 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1));
    }
}
