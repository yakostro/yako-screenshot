using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Yako.Screenshot;

/// <summary>
/// The app's crop-marks mark, drawn on a 32-unit grid. One source of truth so the tray icon,
/// the exe icon and any installer artwork can never drift apart.
/// </summary>
internal static class AppIconArt
{
    private const float Grid = 32f;
    private static readonly Color Accent = Color.FromArgb(0x4C, 0x9A, 0xFF);

    public static void Draw(Graphics g, int size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float unit = size / Grid;
        g.ScaleTransform(unit, unit);

        using var fill = new SolidBrush(Color.FromArgb(70, Accent));
        g.FillRectangle(fill, 10f, 10f, 12f, 12f);

        // Thicken the strokes at tiny sizes: a 3-unit pen lands on 1.5 physical pixels at
        // 16px and antialiases away to a grey smudge.
        float stroke = size <= 24 ? 4.5f : 3f;
        using var pen = new Pen(Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, 10f, 3f, 10f, 29f);
        g.DrawLine(pen, 22f, 3f, 22f, 29f);
        g.DrawLine(pen, 3f, 10f, 29f, 10f);
        g.DrawLine(pen, 3f, 22f, 29f, 22f);
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        Draw(g, size);
        return bmp;
    }

    /// <summary>
    /// Writes a multi-resolution .ico so Explorer, the Start Menu, the Alt-Tab list and any
    /// installer each pick a size that needs no rescaling.
    ///
    /// Small frames are uncompressed 32-bit DIBs, because System.Drawing cannot read PNG
    /// frames back and renders them as noise. Large frames are PNG: as DIBs the 128 and 256
    /// entries alone cost 327 KB, and the only thing that reads them is the Windows shell,
    /// which handles PNG frames correctly.
    /// </summary>
    public static void WriteIco(string path, params int[] sizes)
    {
        using var file = File.Create(path);
        BuildIco(file, sizes);
    }

    /// <summary>
    /// A tray-ready <see cref="Icon"/> at one size, built through the same real ICO format the
    /// exe uses. Bitmap.GetHicon() looks simpler but hands the shell an icon it declines to
    /// draw - the tray slot ends up blank - so go through a proper icon stream instead.
    /// </summary>
    public static Icon CreateIcon(int size)
    {
        using var ms = new MemoryStream();
        BuildIco(ms, new[] { size });
        ms.Position = 0;
        return new Icon(ms, size, size);
    }

    private static void BuildIco(Stream stream, int[] sizes)
    {
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var bmp = Render(size);
            frames.Add(size > 64 ? PngFrame(bmp) : DibFrame(bmp));
        }

        var w = new BinaryWriter(stream);

        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)sizes.Length);

        // All directory entries precede the data, so offsets start past the whole table.
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));   // 0 means 256
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);            // palette size
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // colour planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
            w.Write(frame);

        w.Flush();
    }

    private static byte[] PngFrame(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] DibFrame(Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        int colourStride = width * 4;                      // 32bpp is always 4-byte aligned
        int maskStride = (width + 31) / 32 * 4;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // BITMAPINFOHEADER
        w.Write(40);                                       // biSize
        w.Write(width);                                    // biWidth
        w.Write(height * 2);                               // biHeight: colour rows + mask rows
        w.Write((ushort)1);                                // biPlanes
        w.Write((ushort)32);                               // biBitCount
        w.Write(0);                                        // biCompression = BI_RGB
        w.Write(colourStride * height + maskStride * height);
        w.Write(0); w.Write(0);                            // pixels per metre
        w.Write(0); w.Write(0);                            // palette

        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[colourStride];
            for (int y = height - 1; y >= 0; y--)          // DIB rows run bottom-up
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride), row, 0, colourStride);
                w.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // AND mask left zeroed: the 32-bit alpha channel does the masking.
        w.Write(new byte[maskStride * height]);
        w.Flush();
        return ms.ToArray();
    }
}
