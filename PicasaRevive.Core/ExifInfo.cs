using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SkiaSharp;

namespace PicasaRevive.Core;

public sealed record ExifItem(string Label, string Value);

public sealed record ExifSection(string Title, List<ExifItem> Items);

/// <summary>Reads photo metadata for the Properties dialog, and carries EXIF over to exported copies.</summary>
public static class ExifInfo
{
    private static readonly (int Tag, string Label)[] CameraTags =
    {
        (ExifDirectoryBase.TagMake, "Camera make"),
        (ExifDirectoryBase.TagModel, "Camera model"),
        (0xA433, "Lens make"),
        (0xA434, "Lens"),
        (ExifDirectoryBase.TagSoftware, "Software"),
        (ExifDirectoryBase.TagArtist, "Artist"),
        (ExifDirectoryBase.TagCopyright, "Copyright"),
        (ExifDirectoryBase.TagImageDescription, "Description"),
    };

    private static readonly (int Tag, string Label)[] ExposureTags =
    {
        (ExifDirectoryBase.TagDateTimeOriginal, "Date taken"),
        (ExifDirectoryBase.TagExposureTime, "Shutter speed"),
        (ExifDirectoryBase.TagFNumber, "Aperture"),
        (ExifDirectoryBase.TagIsoEquivalent, "ISO"),
        (ExifDirectoryBase.TagFocalLength, "Focal length"),
        (0xA405, "Focal length (35 mm)"),
        (ExifDirectoryBase.TagExposureBias, "Exposure compensation"),
        (ExifDirectoryBase.TagExposureProgram, "Exposure program"),
        (ExifDirectoryBase.TagMeteringMode, "Metering"),
        (ExifDirectoryBase.TagFlash, "Flash"),
        (0xA403, "White balance"),
        (ExifDirectoryBase.TagOrientation, "Orientation"),
        (0xA001, "Colour space"),
    };

    public static DateTime? CaptureTime(string path)
    {
        try
        {
            foreach (var dir in ImageMetadataReader.ReadMetadata(path).OfType<ExifSubIfdDirectory>())
                if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken)) return taken;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// Curated sections (Camera, Exposure, GPS) followed by one section per metadata directory with every tag.
    /// Never throws: unreadable files give an empty list.
    /// </summary>
    public static List<ExifSection> Read(string path)
    {
        var sections = new List<ExifSection>();
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(path); }
        catch (Exception) { return sections; }

        var exifDirs = dirs.OfType<ExifDirectoryBase>().Where(d => d is not ExifThumbnailDirectory).ToList();

        void Curated(string title, (int Tag, string Label)[] tags)
        {
            var items = new List<ExifItem>();
            foreach (var (tag, label) in tags)
            {
                foreach (var d in exifDirs)
                {
                    if (!d.ContainsTag(tag)) continue;
                    var value = Clean(SafeDescription(d, tag));
                    if (value.Length == 0) continue;
                    items.Add(new ExifItem(label, value));
                    break;
                }
            }
            if (items.Count > 0) sections.Add(new ExifSection(title, items));
        }

        Curated("Camera", CameraTags);
        Curated("Exposure", ExposureTags);

        try
        {
            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
            var loc = gps?.GetGeoLocation();
            if (loc is not null && !(loc.Latitude == 0 && loc.Longitude == 0))
            {
                var items = new List<ExifItem>
                {
                    new("Latitude", loc.Latitude.ToString("0.000000", CultureInfo.InvariantCulture)),
                    new("Longitude", loc.Longitude.ToString("0.000000", CultureInfo.InvariantCulture)),
                };
                var alt = Clean(SafeDescription(gps!, GpsDirectory.TagAltitude));
                if (alt.Length > 0) items.Add(new ExifItem("Altitude", alt));
                sections.Add(new ExifSection("GPS", items));
            }
        }
        catch (Exception) { /* GPS is optional */ }

        foreach (var d in dirs)
        {
            if (d is GpsDirectory && sections.All(s => s.Title != "GPS")) continue;
            var items = new List<ExifItem>();
            foreach (var tag in d.Tags)
            {
                var value = Clean(tag.Description ?? "");
                if (value.Length == 0) continue;
                items.Add(new ExifItem(tag.Name, value));
            }
            if (items.Count > 0) sections.Add(new ExifSection("All: " + d.Name, items));
        }
        return sections;
    }

    private static string SafeDescription(MetadataExtractor.Directory d, int tag)
    {
        try { return d.GetDescription(tag) ?? ""; }
        catch (Exception) { return ""; }
    }

    private static string Clean(string s)
    {
        s = s.Replace('\0', ' ').Trim();
        return s.Length > 300 ? s[..300] + "..." : s;
    }

    // ------------------------------------------------------------------ copying EXIF into rendered JPEGs

    /// <summary>
    /// Returns <paramref name="jpeg"/> with the EXIF block of the source photo inserted, so exported and
    /// AI-generated copies keep date taken, camera, exposure and GPS. The block is adjusted for the new
    /// pixels: orientation is reset (rotation is already baked in), the stale embedded thumbnail is
    /// unlinked, and the pixel dimensions are updated. On any problem the JPEG is returned unchanged.
    /// </summary>
    public static byte[] CopyExif(string sourcePath, byte[] jpeg)
    {
        try
        {
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
            var exif = ReadExifSegment(sourcePath);
            if (exif is null) return jpeg;

            int width = 0, height = 0;
            try { var info = SKBitmap.DecodeBounds(jpeg); width = info.Width; height = info.Height; }
            catch (Exception) { /* dimensions stay as they were */ }
            Normalize(exif, width, height);

            // Insert after SOI and any JFIF (APP0) segments; drop an EXIF block the target already has.
            int pos = 2;
            while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] == 0xE0)
                pos += 2 + ((jpeg[pos + 2] << 8) | jpeg[pos + 3]);
            int resume = pos;
            if (pos + 10 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] == 0xE1 && IsExifHeader(jpeg, pos + 4))
                resume = pos + 2 + ((jpeg[pos + 2] << 8) | jpeg[pos + 3]);
            if (pos > jpeg.Length || resume > jpeg.Length) return jpeg;

            int segLen = exif.Length + 2;
            if (segLen > 0xFFFF) return jpeg;
            var result = new byte[pos + 4 + exif.Length + (jpeg.Length - resume)];
            Buffer.BlockCopy(jpeg, 0, result, 0, pos);
            result[pos] = 0xFF; result[pos + 1] = 0xE1;
            result[pos + 2] = (byte)(segLen >> 8); result[pos + 3] = (byte)segLen;
            Buffer.BlockCopy(exif, 0, result, pos + 4, exif.Length);
            Buffer.BlockCopy(jpeg, resume, result, pos + 4 + exif.Length, jpeg.Length - resume);
            return result;
        }
        catch (Exception)
        {
            return jpeg;
        }
    }

    private static bool IsExifHeader(byte[] b, int at) =>
        at + 6 <= b.Length && b[at] == 'E' && b[at + 1] == 'x' && b[at + 2] == 'i' && b[at + 3] == 'f' && b[at + 4] == 0 && b[at + 5] == 0;

    /// <summary>The APP1 payload ("Exif\0\0" + TIFF data) of a JPEG file, or null.</summary>
    private static byte[]? ReadExifSegment(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[4];
        if (fs.Read(head, 0, 2) != 2 || head[0] != 0xFF || head[1] != 0xD8) return null;
        while (fs.Read(head, 0, 4) == 4)
        {
            if (head[0] != 0xFF) return null;
            int marker = head[1];
            if (marker == 0xDA || marker == 0xD9) return null;
            int len = ((head[2] << 8) | head[3]) - 2;
            if (len < 0) return null;
            if (marker == 0xE1 && len >= 14)
            {
                var payload = new byte[len];
                fs.ReadExactly(payload, 0, len);
                if (IsExifHeader(payload, 0)) return payload;
            }
            else
            {
                fs.Seek(len, SeekOrigin.Current);
            }
        }
        return null;
    }

    private static void Normalize(byte[] p, int width, int height)
    {
        const int t = 6; // TIFF header starts after "Exif\0\0"
        if (p.Length < t + 8) return;
        bool le = p[t] == 'I';

        int U16(int at) => le ? p[at] | (p[at + 1] << 8) : (p[at] << 8) | p[at + 1];
        long U32(int at) => le
            ? (uint)(p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24))
            : (uint)((p[at] << 24) | (p[at + 1] << 16) | (p[at + 2] << 8) | p[at + 3]);
        void W16(int at, int v) { if (le) { p[at] = (byte)v; p[at + 1] = (byte)(v >> 8); } else { p[at] = (byte)(v >> 8); p[at + 1] = (byte)v; } }
        void W32(int at, long v)
        {
            if (le) { p[at] = (byte)v; p[at + 1] = (byte)(v >> 8); p[at + 2] = (byte)(v >> 16); p[at + 3] = (byte)(v >> 24); }
            else { p[at] = (byte)(v >> 24); p[at + 1] = (byte)(v >> 16); p[at + 2] = (byte)(v >> 8); p[at + 3] = (byte)v; }
        }
        void WriteDim(int entry, int value)
        {
            int type = U16(entry + 2);
            if (type == 3) { W32(entry + 8, 0); W16(entry + 8, Math.Min(value, 65535)); }
            else if (type == 4) W32(entry + 8, value);
        }

        long ifd0L = t + U32(t + 4);
        if (ifd0L + 2 > p.Length) return;
        int ifd0 = (int)ifd0L;
        int n = U16(ifd0);
        if (ifd0 + 2 + 12L * n + 4 > p.Length) return;

        long exifIfd = -1;
        for (int i = 0; i < n; i++)
        {
            int e = ifd0 + 2 + 12 * i;
            int tag = U16(e), type = U16(e + 2);
            if (tag == 0x0112 && type == 3) W16(e + 8, 1);          // orientation: pixels are already upright
            else if (tag == 0x8769) exifIfd = t + U32(e + 8);
        }
        W32(ifd0 + 2 + 12 * n, 0);                                   // unlink IFD1 (the old thumbnail)

        if (width <= 0 || height <= 0 || exifIfd < 0 || exifIfd + 2 > p.Length) return;
        int sub = (int)exifIfd;
        int m = U16(sub);
        if (sub + 2 + 12L * m > p.Length) return;
        for (int i = 0; i < m; i++)
        {
            int e = sub + 2 + 12 * i;
            int tag = U16(e);
            if (tag == 0xA002) WriteDim(e, width);
            else if (tag == 0xA003) WriteDim(e, height);
        }
    }
}
