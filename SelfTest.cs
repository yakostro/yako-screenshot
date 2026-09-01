using System.Text;

namespace Yako.Screenshot;

/// <summary>
/// Headless check of the invariants the GUI depends on, runnable as
/// <c>Yako.Screenshot.exe --selftest</c>. Guards DPI awareness, monitor enumeration, that the
/// crop never resamples, and that the Figma payload declares the right size around the right
/// pixels. Writes a report to %TEMP% and exits non-zero on failure.
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

        log.AppendLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yako-selftest.txt");
        File.WriteAllText(path, log.ToString());
        Console.Out.Write(log.ToString());
        Console.Out.Write($"report: {path}\n");
        Console.Out.Flush();
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
