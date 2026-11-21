using System.Text.Json.Nodes;
using PicasaRevive.Core;
using SkiaSharp;

namespace PicasaRevive.Tests;

public class ImagingTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly AppPaths _paths;
    private readonly ImageOps _images;

    public ImagingTests()
    {
        _paths = new AppPaths(_tmp.Sub("data"));
        _images = new ImageOps(_paths);
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void ToneMath_NeutralRecipe_IsIdentity()
    {
        var e = new EditRecipe();
        Assert.True(ToneMath.IsIdentity(ToneMath.BuildLut(e)));
        Assert.True(ToneMath.IsIdentity(ToneMath.BuildMatrix(e)));
    }

    [Fact]
    public void ToneMath_MatchesThePreviewFormulas()
    {
        // brightness = 1 + (brightness + fill_light * 0.5) / 100, clamped to >= 0.1
        Assert.Equal(1.45, ToneMath.BrightnessFactor(new EditRecipe { Brightness = 20, FillLight = 50 }), 6);
        Assert.Equal(0.1, ToneMath.BrightnessFactor(new EditRecipe { Brightness = -100, FillLight = 0 }), 6);
        // contrast = 1 + contrast / 100, clamped to >= 0.1 ; saturate = 1 + saturation / 100, clamped to >= 0
        Assert.Equal(0.1, ToneMath.ContrastFactor(new EditRecipe { Contrast = -100 }), 6);
        Assert.Equal(0.0, ToneMath.SaturationFactor(new EditRecipe { Saturation = -100 }), 6);
        Assert.Equal(1.5, ToneMath.SaturationFactor(new EditRecipe { Saturation = 50 }), 6);
    }

    [Fact]
    public void ToneMath_ShadowsLiftDarksMoreThanBrights_AndKeepEndpoints()
    {
        double dark = ToneMath.ToneCurve(0.25, 100, 0) - 0.25;
        double bright = ToneMath.ToneCurve(0.85, 100, 0) - 0.85;
        Assert.True(dark > bright && dark > 0.05);
        Assert.Equal(0, ToneMath.ToneCurve(0, 100, 100), 6);
        Assert.Equal(1, ToneMath.ToneCurve(1, -100, -100), 6);
    }

    [Fact]
    public void ToneMath_BlackAndWhite_RemovesColour()
    {
        var m = ToneMath.BuildMatrix(new EditRecipe { Effect = "bw" });
        var (r, g, b) = ToneMath.Apply(m, 0.8, 0.3, 0.1);
        Assert.Equal(r, g, 6);
        Assert.Equal(g, b, 6);
    }

    [Fact]
    public void EditRecipe_ParsesLooseJson_AndRejectsUnknownEffects()
    {
        var e = EditRecipe.From(new JsonObject
        {
            ["brightness"] = "15",          // numbers as strings are tolerated
            ["rotation"] = -90,
            ["effect"] = "<script>",
            ["crop"] = new JsonArray(0.5, 0.5, 0.9, 0.9),
            ["feeling_lucky"] = 1,
        });

        Assert.Equal(15, e.Brightness);
        Assert.Equal(270, e.Rotation);
        Assert.Equal("none", e.Effect);
        Assert.Equal(new[] { 0.5, 0.5, 0.5, 0.5 }, e.Crop);   // clamped to stay inside the image
        Assert.True(e.FeelingLucky);
        Assert.Equal(15, e.WithLuckyApplied().Contrast);
    }

    [Fact]
    public void EditRecipe_VignetteStrength_FollowsEffectAndSlider()
    {
        Assert.Equal(0, new EditRecipe().VignetteStrength);
        Assert.Equal(0.5, new EditRecipe { Effect = "vignette" }.VignetteStrength);
        Assert.Equal(0, new EditRecipe { Effect = "vignette", Vignette = 0 }.VignetteStrength);
        Assert.Equal(0.35, new EditRecipe { Effect = "sepia", Vignette = 35 }.VignetteStrength);
    }

    [Fact]
    public void Thumbnail_IsAtMost380px_AndReportsOriginalSize()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "big.jpg", 2400, 1600, new SKColor(40, 120, 200));

        var (thumb, w, h) = _images.GenerateThumbnail(file);

        Assert.Equal((2400, 1600), (w, h));
        Assert.True(File.Exists(thumb));
        Assert.StartsWith(_paths.ThumbDir, thumb);
        using var bmp = SKBitmap.Decode(thumb);
        Assert.Equal(380, bmp.Width);
        Assert.InRange(bmp.Height, 252, 254);
    }

    [Fact]
    public void Render_RotationSwapsDimensions_AndMaxDimLimitsSize()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "wide.jpg", 1200, 800, SKColors.Gray);

        using var rotated = ImageOps.Render(file, new JsonObject(), 90, 0);
        Assert.Equal((800, 1200), (rotated.Width, rotated.Height));

        using var recipeRotated = ImageOps.Render(file, new JsonObject { ["rotation"] = 90 }, 90, 600);
        Assert.Equal((600, 400), (recipeRotated.Width, recipeRotated.Height)); // 90 + 90 = 180: landscape again
    }

    [Fact]
    public void Render_CropKeepsTheRequestedRegion()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "crop.jpg", 1000, 800, SKColors.Gray);
        using var cropped = ImageOps.Render(file, new JsonObject { ["crop"] = new JsonArray(0.25, 0.5, 0.5, 0.25) }, 0, 0);
        Assert.Equal((500, 200), (cropped.Width, cropped.Height));
    }

    [Fact]
    public void Render_BrightnessAndEffectsChangePixels()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "tone.jpg", 300, 200, new SKColor(100, 60, 40));

        var plain = TestImages.CenterPixel(ImageOps.RenderJpeg(file, new JsonObject(), 0, 0));
        var brighter = TestImages.CenterPixel(ImageOps.RenderJpeg(file, new JsonObject { ["brightness"] = 40 }, 0, 0));
        var bw = TestImages.CenterPixel(ImageOps.RenderJpeg(file, new JsonObject { ["effect"] = "bw" }, 0, 0));
        var lucky = TestImages.CenterPixel(ImageOps.RenderJpeg(file, new JsonObject { ["feeling_lucky"] = true }, 0, 0));

        Assert.InRange(plain.Red, 96, 104);
        Assert.InRange(brighter.Red, 136, 144);              // 100 * 1.4
        Assert.True(Math.Abs(bw.Red - bw.Green) <= 3 && Math.Abs(bw.Green - bw.Blue) <= 3);
        Assert.NotEqual(plain, lucky);
    }

    [Fact]
    public void Render_VignetteDarkensCornersNotCentre()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "vig.jpg", 400, 300, new SKColor(200, 200, 200));
        using var bmp = ImageOps.Render(file, new JsonObject { ["effect"] = "vignette", ["vignette"] = 100 }, 0, 0);

        Assert.InRange(bmp.GetPixel(200, 150).Red, 195, 205);
        Assert.True(bmp.GetPixel(2, 2).Red < 80);
    }

    [Fact]
    public void Export_WritesFullResolutionJpeg_AndNeverOverwrites()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "holiday.jpg", 900, 600, new SKColor(100, 60, 40));
        var photo = new Photo { FilePath = file, Filename = "holiday.jpg", Rotation = 90, Edits = new JsonObject { ["effect"] = "bw" } };
        var dest = Path.Combine(_tmp.Path, "exports");

        var first = ImageOps.Export(photo, dest);
        var second = ImageOps.Export(photo, dest);

        Assert.EndsWith("holiday_svacitra_export.jpg", first);
        Assert.EndsWith("holiday_svacitra_export_2.jpg", second);
        using var bmp = SKBitmap.Decode(first);
        Assert.Equal((600, 900), (bmp.Width, bmp.Height));
        var px = bmp.GetPixel(300, 450);
        Assert.True(Math.Abs(px.Red - px.Blue) <= 3);
        // The original file is untouched.
        using var original = SKBitmap.Decode(file);
        Assert.Equal((900, 600), (original.Width, original.Height));
    }

    [Fact]
    public void EditedThumbnail_AppearsWithEdits_AndGoesAwayWhenCleared()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "t.jpg", 800, 600, SKColors.OrangeRed);
        var (thumb, _, _) = _images.GenerateThumbnail(file);
        var photo = new Photo { FilePath = file, Filename = "t.jpg", ThumbPath = thumb, Rotation = 90 };

        _images.RefreshEditedThumbnail(photo);
        var edited = _images.ResolveThumb(photo)!;
        Assert.EndsWith("_e.jpg", edited);
        using (var bmp = SKBitmap.Decode(edited)) Assert.True(bmp.Height > bmp.Width);

        photo.Rotation = 0;
        _images.RefreshEditedThumbnail(photo);
        Assert.False(File.Exists(edited));
        Assert.Equal(thumb, _images.ResolveThumb(photo));
    }

    [Fact]
    public void SampleLibrary_AndScanner_RetainEditsAcrossOfflineRescan()
    {
        var sampleDir = SampleLibrary.Ensure(_paths.SampleLibraryDir);
        Assert.Equal(10, Directory.EnumerateFiles(sampleDir, "*.jpg", SearchOption.AllDirectories).Count());

        using var catalog = new Catalog(_paths.DatabaseFile);
        using var scanner = new Scanner(catalog, _images);

        scanner.ScanDirectory(sampleDir);
        Assert.Equal(10, catalog.Count());
        Assert.Equal(3, catalog.GetSummary().Folders.Count);
        Assert.Contains(Catalog.NormalizeDir(sampleDir), catalog.GetWatchedFolders());
        var status = scanner.GetStatus();
        Assert.False(status.IsScanning);
        Assert.Equal(10, status.TotalFound);
        Assert.Equal(0, status.Errors);

        var beach = catalog.Query(new PhotoQuery { Search = "golden_hour_beach" }).Photos.Single();
        Assert.Equal((1280, 850), (beach.Width, beach.Height));
        Assert.Equal((2024, 8), (beach.Year, beach.Month));
        Assert.True(File.Exists(beach.ThumbPath));

        // Second scan: nothing changed, so nothing is re-indexed and user data survives.
        catalog.ToggleStar(beach.Id);
        long version = catalog.LibraryVersion;
        scanner.ScanDirectory(sampleDir);
        Assert.Equal(version, catalog.LibraryVersion);
        Assert.Equal(1, catalog.GetPhoto(beach.Id)!.IsStarred);

        catalog.SaveEdits(beach.Id, new JsonObject { ["brightness"] = 15 });
        catalog.UpdatePhotoAi(beach.Id, "Beach", new[] { "sea" }, new Dictionary<string, int>());
        var moved = Path.Combine(_tmp.Path, "temporarily-offline");
        Directory.Move(beach.FolderPath, moved);
        scanner.ScanDirectory(sampleDir);
        Assert.Equal(10, catalog.Count());
        Assert.True(catalog.GetPhoto(beach.Id)!.Offline);
        Assert.True(catalog.GetSummary().Folders.Single(f => f.FolderPath == beach.FolderPath).Offline);
        Directory.Move(moved, beach.FolderPath);
        scanner.ScanDirectory(sampleDir);
        var back = catalog.GetPhoto(beach.Id)!;
        Assert.False(back.Offline);
        Assert.Equal(1, back.IsStarred);
        Assert.Equal(15, (int)back.Edits["brightness"]!);
        Assert.Equal("Beach", back.AiCaption);
        Assert.Contains("sea", back.AiTags);

    }
    [Fact]
    public async Task ConcurrentExports_KeepBothFiles()
    {
        var src = TestImages.WriteJpeg(_tmp.Path, "test.jpg", 200, 150, SKColors.Red);
        var photo = new Photo { FilePath = src };
        var copies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => ImageOps.Export(photo, _tmp.Sub("exports")))));
        Assert.Equal(4, copies.Distinct().Count());
        Assert.All(copies, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void SlideshowVideo_EncodesSlidesInOrderAndKeepsDestinationOnCancel()
    {
        var red = new Photo { FilePath = TestImages.WriteJpeg(_tmp.Path, "red.jpg", 200, 100, SKColors.Red) };
        var blue = new Photo { FilePath = TestImages.WriteJpeg(_tmp.Path, "blue.jpg", 100, 200, SKColors.Blue) };
        var file = Path.Combine(_tmp.Path, "show.mp4");
        Photo Select(long id) => id == 1 ? red : blue;
        var progress = new List<int>();
        SlideshowVideo.Export(file, new long[] { 2, 1 }, Select, progress.Add, CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, progress);
        using (var video = new OpenCvSharp.VideoCapture(file))
        {
            Assert.Equal(1280, video.FrameWidth);
            Assert.Equal(720, video.FrameHeight);
            Assert.Equal(168, video.FrameCount);
            using var frame = new OpenCvSharp.Mat();
            video.PosFrames = 24;
            Assert.True(video.Read(frame));
            Assert.True(frame.At<OpenCvSharp.Vec3b>(360, 640).Item0 > 200);
            video.PosFrames = 108;
            Assert.True(video.Read(frame));
            Assert.True(frame.At<OpenCvSharp.Vec3b>(360, 640).Item2 > 200);
        }
        using var stop = new CancellationTokenSource();
        var original = File.ReadAllBytes(file);
        Assert.Throws<OperationCanceledException>(() => SlideshowVideo.Export(file, new long[] { 1, 2 }, Select, _ => stop.Cancel(), stop.Token));
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(_tmp.Path, ".picasa-*.mp4"));
    }

}
