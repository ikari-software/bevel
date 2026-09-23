using System.Text;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// bevel-9elh: REAL content previews (ImageIO for images, CGPDFDocument for PDFs) — the thumbnail
/// source behind the taskbar's stack grid. Fixtures are synthesized here (an uncompressed BMP, a
/// hand-written one-page PDF) so the tests need no binary assets and no Quick Look.
/// </summary>
public class MacOSThumbnailProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bevel-thumb-" + Guid.NewGuid().ToString("N")[..10]);

    public MacOSThumbnailProviderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── The no-I/O format gate ───────────────────────────────────────────────

    [Theory]
    [InlineData("/x/photo.JPG", true)]
    [InlineData("/x/scan.pdf", true)]
    [InlineData("/x/shot.heic", true)]
    [InlineData("/x/notes.txt", false)]      // no decoder here — the caller falls back to a type icon
    [InlineData("/x/archive.zip", false)]
    [InlineData("/x/folder", false)]
    public void CanPreview_admits_only_formats_it_can_actually_decode(string path, bool expected)
        => Assert.Equal(expected, new MacOSThumbnailProvider().CanPreview(path));

    [Fact]
    public async Task Unpreviewable_file_yields_null_not_a_blank_tile()
    {
        var txt = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(txt, "hello");

        Assert.Null(await new MacOSThumbnailProvider().GetThumbnailAsync(txt, 128));
    }

    [Fact]
    public async Task Missing_file_yields_null_and_does_not_throw()
        => Assert.Null(await new MacOSThumbnailProvider()
            .GetThumbnailAsync(Path.Combine(_dir, "gone-" + Guid.NewGuid().ToString("N") + ".png"), 128));

    [Fact]
    public async Task Corrupt_image_yields_null_rather_than_throwing()
    {
        var fake = Path.Combine(_dir, "truncated.png");
        await File.WriteAllBytesAsync(fake, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });

        Assert.Null(await new MacOSThumbnailProvider().GetThumbnailAsync(fake, 128));
    }

    // ── Fit: the aspect contract ─────────────────────────────────────────────

    [Theory]
    [InlineData(200, 100, 64, 64, 32)]   // landscape: longest side capped
    [InlineData(100, 200, 64, 32, 64)]   // portrait
    [InlineData(64, 64, 64, 64, 64)]     // exact
    [InlineData(24, 24, 128, 24, 24)]    // never upscaled into blur
    public void Fit_caps_the_longest_side_and_preserves_aspect(int w, int h, int max, int outW, int outH)
        => Assert.Equal((outW, outH), MacOSThumbnailProvider.Fit(w, h, max));

    // ── Real decodes (macOS only) ────────────────────────────────────────────

    [Fact]
    public async Task Decodes_an_image_to_its_actual_content()
    {
        if (!OperatingSystem.IsMacOS()) return; // macOS-only: ImageIO.

        // A 200×100 bitmap, left half red, right half blue — content we can recognise in the preview.
        var bmp = Path.Combine(_dir, "photo.bmp");
        await File.WriteAllBytesAsync(bmp, TwoToneBmp(200, 100));

        var image = await new MacOSThumbnailProvider().GetThumbnailAsync(bmp, 64);

        Assert.NotNull(image);
        Assert.Equal(64, image!.Width);
        Assert.Equal(32, image.Height);   // aspect preserved, not squashed into a square
        Assert.Equal(64 * 32 * 4, image.Bgra.Length);

        // The preview really is the file's CONTENT: red on the left, blue on the right (BGRA order).
        var left = PixelAt(image, 8, 16);
        var right = PixelAt(image, 55, 16);
        Assert.True(left.R > 150 && left.B < 100, $"left pixel should be red, was {left}");
        Assert.True(right.B > 150 && right.R < 100, $"right pixel should be blue, was {right}");
    }

    [Fact]
    public async Task Decodes_a_pdf_first_page_onto_white()
    {
        if (!OperatingSystem.IsMacOS()) return; // macOS-only: CoreGraphics PDF.

        var pdf = Path.Combine(_dir, "doc.pdf");
        await File.WriteAllBytesAsync(pdf, OnePagePdf());

        var image = await new MacOSThumbnailProvider().GetThumbnailAsync(pdf, 64);

        Assert.NotNull(image);
        Assert.Equal(64, image!.Width);
        Assert.Equal(32, image.Height);           // 200×100 MediaBox, aspect preserved
        var corner = PixelAt(image, 1, 1);        // margin outside the drawn rect: the white page
        Assert.True(corner.R > 200 && corner.G > 200 && corner.B > 200, $"page should be white, was {corner}");
        var middle = PixelAt(image, 32, 16);      // the blue rectangle the page paints
        Assert.True(middle.B > 150 && middle.R < 100, $"page content should be blue, was {middle}");
    }

    [Fact]
    public async Task Caches_per_path_and_size()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var bmp = Path.Combine(_dir, "cached.bmp");
        await File.WriteAllBytesAsync(bmp, TwoToneBmp(40, 40));

        var provider = new MacOSThumbnailProvider();
        var first = await provider.GetThumbnailAsync(bmp, 64);
        var second = await provider.GetThumbnailAsync(bmp, 64);

        Assert.NotNull(first);
        Assert.Same(first!.Bgra, second!.Bgra);   // second call is the cached PalImage, no re-decode
    }

    [Fact]
    public async Task Rewriting_the_file_invalidates_the_cached_preview()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(_dir, "replaced.bmp");
        await File.WriteAllBytesAsync(path, TwoToneBmp(200, 100));
        var provider = new MacOSThumbnailProvider();
        var before = await provider.GetThumbnailAsync(path, 64);

        // Same path, different content AND a newer write stamp — the preview must follow the file.
        await File.WriteAllBytesAsync(path, TwoToneBmp(100, 200));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var after = await provider.GetThumbnailAsync(path, 64);

        Assert.Equal(32, before!.Height);  // landscape
        Assert.Equal(64, after!.Height);   // now portrait: a fresh decode, not the cached landscape tile
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private readonly record struct Rgb(byte R, byte G, byte B);

    private static Rgb PixelAt(Bevel.Pal.Abstractions.PalImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return new Rgb(image.Bgra[i + 2], image.Bgra[i + 1], image.Bgra[i + 0]);
    }

    /// <summary>An uncompressed 24-bit BMP: left half red, right half blue. No encoder needed, and
    /// ImageIO reads BMP, so the decode under test is the real one.</summary>
    private static byte[] TwoToneBmp(int width, int height)
    {
        var rowBytes = (width * 3 + 3) / 4 * 4;      // BMP rows pad to 4 bytes
        var pixels = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * rowBytes + x * 3;
                var leftHalf = x < width / 2;
                pixels[i + 0] = leftHalf ? (byte)0 : (byte)255;     // blue channel
                pixels[i + 1] = 0;                                   // green
                pixels[i + 2] = leftHalf ? (byte)255 : (byte)0;      // red
            }

        var file = new byte[54 + pixels.Length];
        var w = new BinaryWriter(new MemoryStream(file));
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(file.Length);
        w.Write(0);                 // reserved
        w.Write(54);                // pixel-data offset
        w.Write(40);                // DIB header size
        w.Write(width);
        w.Write(height);            // positive: bottom-up rows
        w.Write((short)1);          // planes
        w.Write((short)24);         // bpp
        w.Write(0);                 // BI_RGB
        w.Write(pixels.Length);
        w.Write(2835); w.Write(2835);   // 72 dpi
        w.Write(0); w.Write(0);         // palette
        w.Write(pixels);
        w.Flush();
        return file;
    }

    /// <summary>A minimal valid one-page PDF (200×100) painting a blue rectangle, with a real xref
    /// table so CoreGraphics parses it the normal way rather than by reconstruction.</summary>
    private static byte[] OnePagePdf()
    {
        const string content = "0 0 1 rg 10 10 180 80 re f\n";
        var objects = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]/Contents 4 0 R>>",
            $"<</Length {content.Length}>>\nstream\n{content}endstream",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<</Size ").Append(objects.Length + 1).Append("/Root 1 0 R>>\nstartxref\n")
           .Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
