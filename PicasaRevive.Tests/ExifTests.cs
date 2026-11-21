using System.Text.Json.Nodes;
using System.Text;
using PicasaRevive.Core;
using SkiaSharp;
using Xunit;

namespace PicasaRevive.Tests;

public class ExifTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>A 300x200 JPEG with a hand-built EXIF block: Make, Orientation 6, DateTimeOriginal, PixelXDimension.</summary>
    private string WriteJpegWithExif(string name)
    {
        var plain = TestImages.WriteJpeg(_tmp.Sub("pics"), "plain_" + name, 300, 200, new SKColor(200, 90, 40));
        var jpeg = File.ReadAllBytes(plain);
        File.Delete(plain);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
        w.Write(Encoding.ASCII.GetBytes("II")); w.Write((ushort)42); w.Write((uint)8);
        // IFD0 at 8: 3 entries, ends at 50
        w.Write((ushort)3);
        w.Write((ushort)0x010F); w.Write((ushort)2); w.Write((uint)8); w.Write((uint)50);                 // Make -> offset 50
        w.Write((ushort)0x0112); w.Write((ushort)3); w.Write((uint)1); w.Write((ushort)6); w.Write((ushort)0); // Orientation 6
        w.Write((ushort)0x8769); w.Write((ushort)4); w.Write((uint)1); w.Write((uint)58);                 // Exif IFD -> 58
        w.Write((uint)0);
        w.Write(Encoding.ASCII.GetBytes("TestCam\0"));                                                    // 50..58
        // Exif IFD at 58: 2 entries, ends at 88
        w.Write((ushort)2);
        w.Write((ushort)0x9003); w.Write((ushort)2); w.Write((uint)20); w.Write((uint)88);                // DateTimeOriginal
        w.Write((ushort)0xA002); w.Write((ushort)3); w.Write((uint)1); w.Write((ushort)300); w.Write((ushort)0);
        w.Write((uint)0);
        w.Write(Encoding.ASCII.GetBytes("2021:05:06 07:08:09\0"));
        w.Flush();
        var exif = ms.ToArray();

        var path = Path.Combine(_tmp.Sub("pics"), name);
        using var fs = File.Create(path);
        fs.Write(jpeg, 0, 2);
        int len = exif.Length + 2;
        fs.Write(new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len });
        fs.Write(exif);
        fs.Write(jpeg, 2, jpeg.Length - 2);
        return path;
    }

    private static string? Find(List<ExifSection> sections, string section, string label) =>
        sections.FirstOrDefault(s => s.Title == section)?.Items.FirstOrDefault(i => i.Label == label)?.Value;

    [Fact]
    public void Timestamp_UsesCameraTime_AndSkipsPhotosWithoutIt()
    {
        var tagged = WriteJpegWithExif("tagged.jpg");
        var plain = TestImages.WriteJpeg(_tmp.Sub("pics"), "plain.jpg", 300, 200, SKColors.Gray);
        var recipe = new JsonObject { ["design"] = new JsonObject { ["stamp"] = "datetime" } };
        Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9), ExifInfo.CaptureTime(tagged));
        Assert.Null(ExifInfo.CaptureTime(plain));
        Assert.Equal(ImageOps.RenderJpeg(plain, null, 0, 0), ImageOps.RenderJpeg(plain, recipe, 0, 0));
        Assert.NotEqual(ImageOps.RenderJpeg(tagged, null, 0, 0), ImageOps.RenderJpeg(tagged, recipe, 0, 0));
    }

    [Theory]
    [InlineData("frame", "mat")]
    [InlineData("frame", "gilt")]
    [InlineData("frame", "ink")]
    [InlineData("frame", "film")]
    [InlineData("style", "outline")]
    [InlineData("style", "gold")]
    [InlineData("style", "retro")]
    [InlineData("style", "neon")]
    [InlineData("title", "Kyoto in spring")]
    public void Export_AppliesEachDesign_AndKeepsOriginal(string field, string value)
    {
        var path = WriteJpegWithExif("trip.jpg");
        var original = File.ReadAllBytes(path);
        var design = new JsonObject { [field] = value };
        if (field == "style") design["art"] = "Kyoto";
        var recipe = new JsonObject { ["design"] = design };
        var photo = new Photo { FilePath = path, Filename = "trip.jpg", Edits = recipe };
        var result = ImageOps.Export(photo, _tmp.Sub("out"));
        using var expected = SKBitmap.Decode(ImageOps.RenderJpeg(path, recipe, 0, 0, 95));
        using var clean = SKBitmap.Decode(ImageOps.RenderJpeg(path, null, 0, 0, 95));
        using var exported = SKBitmap.Decode(result);
        Assert.True(expected.Pixels.SequenceEqual(exported.Pixels));
        Assert.False(clean.Pixels.SequenceEqual(exported.Pixels));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Read_ReturnsCuratedSections_AndAllTags()
    {
        var file = WriteJpegWithExif("cam.jpg");

        var sections = ExifInfo.Read(file);

        Assert.Equal("TestCam", Find(sections, "Camera", "Camera make"));
        Assert.Equal("2021:05:06 07:08:09", Find(sections, "Exposure", "Date taken"));
        Assert.Contains(sections, s => s.Title.StartsWith("All: Exif"));
    }

    [Fact]
    public void Read_FileWithoutExif_HasNoCuratedSections_AndMissingFileIsEmpty()
    {
        var file = TestImages.WriteJpeg(_tmp.Sub("pics"), "plain.jpg", 64, 64, SKColors.Gray);
        Assert.DoesNotContain(ExifInfo.Read(file), s => s.Title is "Camera" or "Exposure" or "GPS");
        Assert.Empty(ExifInfo.Read(Path.Combine(_tmp.Sub("pics"), "missing.jpg")));
    }

    [Fact]
    public void Export_KeepsExif_ResetsOrientation_AndUpdatesDimensions()
    {
        var file = WriteJpegWithExif("shot.jpg");
        var photo = new Photo { FilePath = file, Filename = "shot.jpg" };

        var exported = ImageOps.Export(photo, _tmp.Sub("out"));

        using (var bmp = SKBitmap.Decode(exported))
            Assert.Equal((200, 300), (bmp.Width, bmp.Height)); // orientation 6 is baked into the pixels
        var sections = ExifInfo.Read(exported);
        Assert.Equal("TestCam", Find(sections, "Camera", "Camera make"));
        Assert.Equal("2021:05:06 07:08:09", Find(sections, "Exposure", "Date taken"));
        Assert.StartsWith("Top, left", Find(sections, "Exposure", "Orientation"));
        Assert.Equal("200 pixels", Find(sections, "All: Exif SubIFD", "Exif Image Width"));
        Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9), Scanner.ReadExif(exported).Taken);
    }

    [Fact]
    public void CopyExif_LeavesNonJpegAndExifLessSourcesAlone()
    {
        var source = TestImages.WriteJpeg(_tmp.Sub("pics"), "noexif.jpg", 32, 32, SKColors.Red);
        var jpeg = File.ReadAllBytes(source);
        Assert.Same(jpeg, ExifInfo.CopyExif(source, jpeg));

        var withExif = WriteJpegWithExif("src.jpg");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        Assert.Same(png, ExifInfo.CopyExif(withExif, png));
    }
}
