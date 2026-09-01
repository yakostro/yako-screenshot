using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Yako.Screenshot;

internal static class Output
{
    /// <summary>
    /// Publishes the bitmap in two formats: PNG for apps that prefer it (Figma, browsers,
    /// Slack) because it round-trips exact pixels, and a plain bitmap for Paint and Office.
    /// </summary>
    public static bool CopyToClipboard(Bitmap bmp)
    {
        try
        {
            var data = new DataObject();

            var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            png.Position = 0;
            data.SetData("PNG", false, png);

            data.SetData(DataFormats.Bitmap, true, new Bitmap(bmp));

            // copy: true flushes our data so it outlives this process; the retry arguments
            // cover the normal case of another app briefly holding the clipboard open.
            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 100);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// Publishes native pixels wrapped in an SVG that declares a smaller display size, so a
    /// paste lands at the logical size while the raster keeps every original pixel. This is
    /// the only clipboard payload that can carry "display size != pixel size"; a plain bitmap
    /// cannot, which is why a 1x resample was destroying quality.
    /// </summary>
    public static bool CopyAsSvg(Bitmap nativePixels, Size logicalSize)
    {
        try
        {
            using var png = new MemoryStream();
            nativePixels.Save(png, ImageFormat.Png);
            string base64 = Convert.ToBase64String(png.ToArray());

            string svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"" +
                $" width=\"{logicalSize.Width}\" height=\"{logicalSize.Height}\"" +
                $" viewBox=\"0 0 {logicalSize.Width} {logicalSize.Height}\">" +
                $"<image x=\"0\" y=\"0\" width=\"{logicalSize.Width}\" height=\"{logicalSize.Height}\"" +
                $" xlink:href=\"data:image/png;base64,{base64}\"/></svg>";

            var data = new DataObject();

            // Text formats ONLY, deliberately. With a bitmap format also on the clipboard,
            // Figma and most other apps take the bitmap and the SVG's size declaration -
            // the entire point of this path - is thrown away.
            data.SetData(DataFormats.UnicodeText, false, svg);
            data.SetData("image/svg+xml", false, new MemoryStream(Encoding.UTF8.GetBytes(svg)));

            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 100);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <summary>Returns false when the user cancels, so the caller can keep the overlay open.</summary>
    public static bool SaveToFile(Bitmap bmp, Settings settings)
    {
        string initial = settings.LastSaveDirectory is { Length: > 0 } dir && Directory.Exists(dir)
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        using var dlg = new SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg",
            DefaultExt = "png",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = initial,
            FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png",
        };

        if (dlg.ShowDialog() != DialogResult.OK) return false;

        var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
        bmp.Save(dlg.FileName, ext is ".jpg" or ".jpeg" ? ImageFormat.Jpeg : ImageFormat.Png);

        settings.LastSaveDirectory = System.IO.Path.GetDirectoryName(dlg.FileName);
        settings.Save();
        return true;
    }
}
