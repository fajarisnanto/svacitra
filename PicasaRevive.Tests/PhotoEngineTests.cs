using System.Text.Json.Nodes;
using ImageMagick;
using PicasaRevive.Core;
using SkiaSharp;
using Xunit;

namespace PicasaRevive.Tests;

public class PhotoEngineTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static SKBitmap NewBitmap(int w, int h, SKColor fill)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(fill);
        return bmp;
    }

    /// <summary>Standard deviation of the green channel over the whole bitmap.</summary>
    private static double StdDev(SKBitmap bmp)
    {
        double sum = 0, sum2 = 0;
        int n = bmp.Width * bmp.Height;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            double v = bmp.GetPixel(x, y).Green;
            sum += v; sum2 += v * v;
        }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
    }

    [Fact]
    public void Recipe_ParsesAndClampsEngineSliders()
    {
        var e = EditRecipe.From(new JsonObject { ["denoise"] = 40, ["clarity"] = 250, ["detail"] = -5 });
        Assert.Equal((40, 100, 0), (e.Denoise, e.Clarity, e.Detail));
        Assert.True(e.HasEngineOps);
        Assert.False(new EditRecipe().HasEngineOps);
        Assert.True(new EditRecipe { RedEye = true }.HasEngineOps);
    }

    [Fact]
    public void Engines_ReportTheirVersions()
    {
        Assert.Matches(@"^\d+\.\d+", PhotoEngine.OpenCvVersion);
        Assert.Contains("Magick.NET", PhotoEngine.MagickVersion);
    }

    [Fact]
    public void Denoise_ReducesNoise()
    {
        using var bmp = NewBitmap(160, 120, new SKColor(120, 120, 120));
        var rnd = new Random(7);
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            byte v = (byte)(120 + rnd.Next(-25, 26));
            bmp.SetPixel(x, y, new SKColor(v, v, v));
        }
        double before = StdDev(bmp);

        PhotoEngine.Apply(bmp, new EditRecipe { Denoise = 100 });

        Assert.True(StdDev(bmp) < before * 0.6, $"noise {before:0.0} -> {StdDev(bmp):0.0}");
    }

    [Fact]
    public void Clarity_IncreasesLocalContrast_AndSharpenBoostsEdges()
    {
        // Low-contrast horizontal gradient 100..140.
        using var flat = NewBitmap(256, 128, SKColors.Gray);
        for (int y = 0; y < flat.Height; y++)
        for (int x = 0; x < flat.Width; x++)
        {
            byte v = (byte)(100 + x * 40 / 255);
            flat.SetPixel(x, y, new SKColor(v, v, v));
        }
        double before = StdDev(flat);
        PhotoEngine.Apply(flat, new EditRecipe { Clarity = 100 });
        Assert.True(StdDev(flat) > before * 1.05, $"contrast {before:0.0} -> {StdDev(flat):0.0}");

        // A soft edge gets overshoot on both sides when sharpened.
        using var edge = NewBitmap(64, 32, new SKColor(90, 90, 90));
        using (var canvas = new SKCanvas(edge))
        using (var paint = new SKPaint { Color = new SKColor(170, 170, 170) })
            canvas.DrawRect(32, 0, 32, 32, paint);
        PhotoEngine.Apply(edge, new EditRecipe { Detail = 100 });
        Assert.True(edge.GetPixel(31, 16).Green < 90);
        Assert.True(edge.GetPixel(32, 16).Green > 170);
        Assert.Equal(90, edge.GetPixel(5, 16).Green);
    }

    [Fact]
    public void RedEye_FixesSmallRoundRedSpots_ButNotLargeRedAreas()
    {
        var skin = new SKColor(215, 170, 140);
        var red = new SKColor(230, 30, 40);
        using var bmp = NewBitmap(400, 300, skin);
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint { Color = red, IsAntialias = true })
        {
            canvas.DrawCircle(140, 120, 7, paint);   // two pupils
            canvas.DrawCircle(250, 120, 7, paint);
            canvas.DrawRect(40, 200, 200, 80, paint); // a red jumper
        }

        PhotoEngine.Apply(bmp, new EditRecipe { RedEye = true });

        var pupil = bmp.GetPixel(140, 120);
        Assert.True(pupil.Red < 80, $"pupil is still red: {pupil}");
        Assert.True(bmp.GetPixel(250, 120).Red < 80);
        Assert.Equal(red.Red, bmp.GetPixel(140, 240).Red);  // large area untouched
        Assert.Equal(skin, bmp.GetPixel(20, 20));            // skin untouched
    }

    [Fact]
    public void Render_AppliesEngineOps_AndEngineOnlyBaseImage()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "eye.jpg", 400, 300, new SKColor(215, 170, 140));
        using (var src = SKBitmap.Decode(file))
        {
            using (var canvas = new SKCanvas(src))
            using (var paint = new SKPaint { Color = new SKColor(230, 30, 40), IsAntialias = true })
                canvas.DrawCircle(200, 150, 8, paint);
            File.WriteAllBytes(file, ImageOps.EncodeJpeg(src, 95));
        }

        using var plain = ImageOps.Render(file, null, 0, 0, applyEdits: false);
        using var baseImage = ImageOps.Render(file, null, 0, 0, applyEdits: false, engineOnly: new EditRecipe { RedEye = true });
        using var full = ImageOps.Render(file, new JsonObject { ["redeye"] = true }, 0, 0);

        Assert.True(plain.GetPixel(200, 150).Red > 200);
        Assert.True(baseImage.GetPixel(200, 150).Red < 90);
        Assert.True(full.GetPixel(200, 150).Red < 90);
    }

    [Fact]
    public void Magick_DecodesAvif_ThroughTheNormalDecoder()
    {
        Assert.Contains(".heic", ImageOps.SupportedExtensions);
        Assert.Contains(".cr2", ImageOps.SupportedExtensions);
        Assert.DoesNotContain(".heic", ImageOps.BrowserNativeExtensions);

        var path = Path.Combine(_tmp.Sub("pics"), "sample.avif");
        using (var img = new MagickImage(new MagickColor(40, 120, 200), 96, 64))
        {
            img.Format = MagickFormat.Avif;
            img.Write(path);
        }

        Assert.Equal((96, 64), ImageOps.ReadDimensions(path));
        using var bmp = ImageOps.Decode(path);
        Assert.Equal((96, 64), (bmp.Width, bmp.Height));
        var c = bmp.GetPixel(48, 32);
        Assert.InRange(c.Red, 25, 55);
        Assert.InRange(c.Green, 105, 135);
        Assert.InRange(c.Blue, 185, 215);
    }

    [Fact]
    public void Magick_UnreadableFile_ReturnsNull()
    {
        var path = Path.Combine(_tmp.Sub("pics"), "broken.heic");
        File.WriteAllText(path, "not an image");
        Assert.Null(PhotoEngine.DecodeWithMagick(path));
        Assert.Throws<InvalidDataException>(() => ImageOps.Decode(path));
    }
}

public class TruncatedThumbnailTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void LooksTruncated_DetectsBlankBottom_ButNotNormalOrAllWhitePictures()
    {
        string Write(string name, Action<SKCanvas> draw)
        {
            using var bmp = new SKBitmap(new SKImageInfo(380, 253, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(bmp)) draw(canvas);
            var path = Path.Combine(_tmp.Sub("thumbs"), name);
            File.WriteAllBytes(path, ImageOps.EncodeJpeg(bmp, 85));
            return path;
        }

        using var paint = new SKPaint { Color = new SKColor(60, 110, 70) };
        var cut = Write("cut.jpg", c => { c.Clear(SKColors.White); c.DrawRect(0, 0, 380, 150, paint); });
        var normal = Write("normal.jpg", c => c.Clear(new SKColor(60, 110, 70)));
        var white = Write("white.jpg", c => c.Clear(SKColors.White));

        Assert.True(ImageOps.LooksTruncated(cut));
        Assert.False(ImageOps.LooksTruncated(normal));
        Assert.False(ImageOps.LooksTruncated(white));
        Assert.False(ImageOps.LooksTruncated(Path.Combine(_tmp.Sub("thumbs"), "missing.jpg")));
    }

    [Fact]
    public void Scan_RedoesACutOffThumbnail_AndKeepsUserData()
    {
        var paths = new AppPaths(_tmp.Sub("data"));
        var images = new ImageOps(paths);
        using var catalog = new Catalog(Path.Combine(_tmp.Sub("data"), "test.db"));
        using var scanner = new Scanner(catalog, images);
        var folder = _tmp.Sub("pics");
        TestImages.WriteJpeg(folder, "a.jpg", 600, 400, new SKColor(60, 110, 70));
        scanner.ScanDirectory(folder);
        var photo = catalog.Query(new PhotoQuery()).Photos.Single();
        catalog.ToggleStar(photo.Id);

        // Simulate the damage: overwrite the cached thumbnail with one whose bottom is blank.
        using (var bmp = new SKBitmap(new SKImageInfo(380, 253, SKColorType.Rgba8888, SKAlphaType.Opaque)))
        {
            using (var canvas = new SKCanvas(bmp))
            using (var paint = new SKPaint { Color = new SKColor(60, 110, 70) })
            {
                canvas.Clear(SKColors.White);
                canvas.DrawRect(0, 0, 380, 120, paint);
            }
            File.WriteAllBytes(photo.ThumbPath, ImageOps.EncodeJpeg(bmp, 85));
        }
        Assert.True(ImageOps.LooksTruncated(photo.ThumbPath));

        scanner.ScanDirectory(folder);

        var after = catalog.GetPhoto(photo.Id)!;
        Assert.False(ImageOps.LooksTruncated(after.ThumbPath));
        Assert.Equal(1, after.IsStarred);
    }
}

public class PreviewCacheTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void EnsurePreview_MakesOneCached1920Copy_AndSkipsSmallPhotos()
    {
        var images = new ImageOps(new AppPaths(_tmp.Sub("data")));
        var bigFile = TestImages.WriteJpeg(_tmp.Sub("pics"), "big.jpg", 4000, 3000, new SKColor(40, 120, 200));
        var big = new Photo { FilePath = bigFile, Filename = "big.jpg", Width = 4000, Height = 3000, FileSize = new FileInfo(bigFile).Length };

        var preview = images.EnsurePreview(big);

        Assert.NotEqual(bigFile, preview);
        Assert.StartsWith(images.PreviewDir, preview);
        using (var bmp = SKBitmap.Decode(preview))
            Assert.Equal((1920, 1440), (bmp.Width, bmp.Height));
        var written = File.GetLastWriteTimeUtc(preview);
        Assert.Equal(preview, images.EnsurePreview(big));
        Assert.Equal(written, File.GetLastWriteTimeUtc(preview));

        // A changed original gets a new preview.
        File.SetLastWriteTimeUtc(bigFile, DateTime.UtcNow.AddMinutes(5));
        Assert.NotEqual(preview, images.EnsurePreview(big));

        var smallFile = TestImages.WriteJpeg(_tmp.Sub("pics"), "small.jpg", 800, 600, SKColors.Gray);
        var small = new Photo { FilePath = smallFile, Filename = "small.jpg", Width = 800, Height = 600, FileSize = new FileInfo(smallFile).Length };
        Assert.Equal(smallFile, images.EnsurePreview(small));
    }
}
