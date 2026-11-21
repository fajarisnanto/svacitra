using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace PicasaRevive.Core;

public readonly record struct ImageStats(double MeanLuma, double StdLuma, double MeanSaturation, double MeanR, double MeanG, double MeanB, double DarkFraction, double BrightFraction, int Width, int Height);

/// <summary>Decoding, thumbnails, the non-destructive edit renderer, export and collages (SkiaSharp).</summary>
public sealed class ImageOps
{
    public const int ThumbSize = 380;
    public const int MaxImageDimension = 16_384;
    public const long MaxImagePixels = 100_000_000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> ThumbLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppPaths _paths;

    public ImageOps(AppPaths paths) => _paths = paths;
    public string CacheDir => _paths.CacheDir;

    public static void ValidateDimensions(int width, int height, string? label = null)
    {
        if (width <= 0 || height <= 0 || width > MaxImageDimension || height > MaxImageDimension || (long)width * height > MaxImagePixels)
            throw new InvalidDataException($"Image dimensions ({width}x{height}) exceed safety limits{(string.IsNullOrEmpty(label) ? "" : " for " + Path.GetFileName(label))}.");
    }

    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        // Decoded by ImageMagick (see PhotoEngine): iPhone HEIC, AVIF and camera RAW.
        ".heic", ".heif", ".avif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw",
    };

    /// <summary>Formats Chromium/WebView2 can display straight from disk (the fast path).</summary>
    public static readonly HashSet<string> BrowserNativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
    };

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };

    // ------------------------------------------------------------------ decode

    /// <summary>
    /// Decodes an image upright (EXIF orientation applied). When <paramref name="targetMaxDim"/> is set,
    /// JPEG/WebP are decoded at a reduced scale, which makes thumbnails of large photos very fast.
    /// </summary>
    public static SKBitmap Decode(string path, int targetMaxDim = 0)
    {
        SKBitmap? bmp = null;
        var origin = SKEncodedOrigin.TopLeft;

        // HEIC / AVIF / camera RAW go straight to ImageMagick, which also applies the orientation itself.
        if (IsMagickFormat(path))
        {
            bmp = PhotoEngine.DecodeWithMagick(path);
            if (bmp is not null) return bmp;
        }

        // Cloud-synced drives (Google Drive, OneDrive) can hand back a file that is not fully there yet. The
        // codec then reports "incomplete input". Retry briefly from a full in-memory read, then reject truncated files.
        bool sawIncomplete = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) Thread.Sleep(40 * attempt);
            var (decoded, decodedOrigin, incomplete) = DecodeWithCodec(path, targetMaxDim, fromMemory: attempt > 0);
            if (incomplete)
            {
                decoded?.Dispose();
                sawIncomplete = true;
                continue;
            }
            bmp = decoded;
            origin = decodedOrigin;
            break;
        }
        if (bmp is null && sawIncomplete)
            throw new InvalidDataException($"Incomplete or corrupt image: {Path.GetFileName(path)}");

        bmp ??= DecodeWhole(path);
        bmp ??= DecodeWithGdi(path);
        if (bmp is null)
        {
            // Last resort for anything Skia and GDI+ could not read. ImageMagick already returns it upright.
            var viaMagick = PhotoEngine.DecodeWithMagick(path);
            if (viaMagick is not null) return viaMagick;
        }
        if (bmp is null) throw new InvalidDataException($"Unsupported or corrupt image: {Path.GetFileName(path)}");

        bmp = ApplyOrigin(bmp, origin);
        return Flatten(bmp);
    }

    /// <summary>One Skia codec attempt. Incomplete is true when only part of the picture could be read.</summary>
    private static (SKBitmap? Bitmap, SKEncodedOrigin Origin, bool Incomplete) DecodeWithCodec(string path, int targetMaxDim, bool fromMemory)
    {
        SKBitmap? bmp = null;
        try
        {
            using var data = fromMemory ? SKData.CreateCopy(ReadAllBytesChecked(path)) : null;
            using var codec = data is not null ? SKCodec.Create(data) : SKCodec.Create(path);
            if (codec is null) return (null, SKEncodedOrigin.TopLeft, false);

            var origin = codec.EncodedOrigin;
            var full = codec.Info;
            ValidateDimensions(full.Width, full.Height, path);

            var size = new SKSizeI(full.Width, full.Height);
            if (targetMaxDim > 0 && Math.Max(full.Width, full.Height) > targetMaxDim)
            {
                float scale = (float)targetMaxDim / Math.Max(full.Width, full.Height);
                size = codec.GetScaledDimensions(scale);
                while (Math.Max(size.Width, size.Height) < targetMaxDim && scale < 1f)
                {
                    scale = Math.Min(1f, scale * 1.3f);
                    size = codec.GetScaledDimensions(scale);
                }
            }
            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            bmp = new SKBitmap(info);
            bmp.Erase(SKColors.Transparent);
            var result = codec.GetPixels(info, bmp.GetPixels());
            if (result == SKCodecResult.Success) return (bmp, origin, false);
            if (result == SKCodecResult.IncompleteInput)
            {
                bmp.Dispose();
                return (null, origin, true);
            }
            bmp.Dispose();
            return (null, origin, false);
        }
        catch (InvalidDataException)
        {
            bmp?.Dispose();
            throw;
        }
        catch (Exception)
        {
            // The caller falls through to the whole-file and GDI+ fallbacks.
            bmp?.Dispose();
            return (null, SKEncodedOrigin.TopLeft, false);
        }
    }

    /// <summary>Reads the whole file and insists on getting as many bytes as the file system says it has.</summary>
    private static byte[] ReadAllBytesChecked(string path)
    {
        var bytes = File.ReadAllBytes(path);
        long expected = new FileInfo(path).Length;
        if (bytes.Length < expected) throw new IOException($"Short read: {bytes.Length} of {expected} bytes");
        return bytes;
    }

    /// <summary>
    /// True when a cached thumbnail looks cut off: its bottom rows are blank white while the top is not.
    /// That is the signature of a photo that was only partly readable when the thumbnail was made.
    /// </summary>
    public static bool LooksTruncated(string thumbPath)
    {
        try
        {
            using var bmp = SKBitmap.Decode(thumbPath);
            if (bmp is null || bmp.Width < 8 || bmp.Height < 8) return false;
            bool RowIsWhite(int y)
            {
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red < 249 || c.Green < 249 || c.Blue < 249) return false;
                }
                return true;
            }
            return RowIsWhite(bmp.Height - 1) && RowIsWhite(bmp.Height - 3) && !RowIsWhite(0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SKBitmap? DecodeWhole(string path)
    {
        try
        {
            var bounds = SKBitmap.DecodeBounds(path);
            if (!bounds.IsEmpty) ValidateDimensions(bounds.Width, bounds.Height, path);
            var b = SKBitmap.Decode(path);
            if (b is null) return null;
            if (b.ColorType == SKColorType.Rgba8888) return b;
            var converted = new SKBitmap(new SKImageInfo(b.Width, b.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(converted)) canvas.DrawBitmap(b, 0, 0);
            b.Dispose();
            return converted;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>GDI+ fallback for formats Skia has no codec for (TIFF).</summary>
    private static SKBitmap? DecodeWithGdi(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var probe = System.Drawing.Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false))
                ValidateDimensions(probe.Width, probe.Height, path);

            using var gdi = new System.Drawing.Bitmap(path);
            using var ms = new MemoryStream();
            gdi.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            using var decoded = SKBitmap.Decode(ms);
            if (decoded is null) return null;
            var converted = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(converted);
            canvas.DrawBitmap(decoded, 0, 0);
            return converted;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Puts transparent images on white so JPEG output does not turn them black.</summary>
    private static SKBitmap Flatten(SKBitmap bmp)
    {
        if (bmp.AlphaType == SKAlphaType.Opaque) return bmp;
        var flat = new SKBitmap(new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bmp, 0, 0);
        }
        bmp.Dispose();
        return flat;
    }

    private static SKBitmap ApplyOrigin(SKBitmap bmp, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => Transform(bmp, 0, true),
        SKEncodedOrigin.BottomRight => Transform(bmp, 180, false),
        SKEncodedOrigin.BottomLeft => Transform(bmp, 180, true),
        SKEncodedOrigin.LeftTop => Transform(bmp, 90, true),
        SKEncodedOrigin.RightTop => Transform(bmp, 90, false),
        SKEncodedOrigin.RightBottom => Transform(bmp, 270, true),
        SKEncodedOrigin.LeftBottom => Transform(bmp, 270, false),
        _ => bmp,
    };

    /// <summary>Rotates clockwise by a multiple of 90°, then optionally mirrors horizontally. Disposes the source.</summary>
    public static SKBitmap Transform(SKBitmap src, int rotation, bool flipHorizontal)
    {
        rotation = EditRecipe.NormalizeRotation(rotation);
        if (rotation == 0 && !flipHorizontal) return src;

        bool swap = rotation is 90 or 270;
        int w = swap ? src.Height : src.Width, h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, src.AlphaType));
        using (var canvas = new SKCanvas(dst))
        {
            if (flipHorizontal) canvas.Scale(-1, 1, w / 2f, 0);
            switch (rotation)
            {
                case 90: canvas.Translate(w, 0); break;
                case 180: canvas.Translate(w, h); break;
                case 270: canvas.Translate(0, h); break;
            }
            canvas.RotateDegrees(rotation);
            canvas.DrawBitmap(src, 0, 0);
        }
        src.Dispose();
        return dst;
    }

    private static SKBitmap ResizeToFit(SKBitmap src, int maxDim)
    {
        if (maxDim <= 0 || (src.Width <= maxDim && src.Height <= maxDim)) return src;
        double scale = (double)maxDim / Math.Max(src.Width, src.Height);
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, src.AlphaType), SKFilterQuality.Medium);
        if (dst is null) return src;
        src.Dispose();
        return dst;
    }

    public static byte[] EncodeJpeg(SKBitmap bmp, int quality)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    public static bool IsMagickFormat(string path) =>
        Array.IndexOf(PhotoEngine.MagickExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    public static (int Width, int Height) ReadDimensions(string path)
    {
        if (IsMagickFormat(path)) return PhotoEngine.PingDimensions(path);
        using var codec = SKCodec.Create(path);
        if (codec is null) return PhotoEngine.PingDimensions(path);
        var info = codec.Info;
        bool swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        return swap ? (info.Height, info.Width) : (info.Width, info.Height);
    }

    // ------------------------------------------------------------------ thumbnails

    public string ThumbPathFor(string filePath, bool edited = false)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        var name = "thumb_" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + (edited ? "_e" : "") + ".jpg";
        return Path.Combine(_paths.ThumbDir, name);
    }

    private static object LockForThumb(string thumbPath) => ThumbLocks.GetOrAdd(thumbPath, _ => new object());

    /// <summary>Writes the cached 380px thumbnail of the untouched original and returns the full (upright) size.</summary>
    public (string ThumbPath, int Width, int Height) GenerateThumbnail(string filePath)
    {
        var (w, h) = ReadDimensions(filePath);
        var bmp = Decode(filePath, ThumbSize);
        if (w == 0 || h == 0) { w = bmp.Width; h = bmp.Height; }
        bmp = ResizeToFit(bmp, ThumbSize);
        try
        {
            var thumbPath = ThumbPathFor(filePath);
            var bytes = EncodeJpeg(bmp, 85);
            lock (LockForThumb(thumbPath)) WriteAtomic(thumbPath, bytes);
            return (thumbPath, w, h);
        }
        finally { bmp.Dispose(); }
    }

    /// <summary>
    /// Refreshes the "_e" thumbnail that reflects saved edits and rotation, or deletes it when the photo is pristine.
    /// </summary>
    public void InvalidateEditedThumbnail(string filePath)
    {
        var path = ThumbPathFor(filePath, edited: true);
        lock (LockForThumb(path)) TryDelete(path);
    }

    public void RefreshEditedThumbnail(Photo photo)
    {
        var path = ThumbPathFor(photo.FilePath, edited: true);
        if (photo.Rotation == 0 && !photo.HasEdits)
        {
            lock (LockForThumb(path)) TryDelete(path);
            return;
        }
        using var bmp = Render(photo.FilePath, photo.Edits, photo.Rotation, ThumbSize);
        var bytes = EncodeJpeg(bmp, 85);
        lock (LockForThumb(path)) WriteAtomic(path, bytes);
    }

    // ------------------------------------------------------------------ screen-size preview cache

    public const int PreviewSize = 1920;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PreviewLocks = new(StringComparer.OrdinalIgnoreCase);

    public string PreviewDir => Path.Combine(_paths.CacheDir, "previews");

    /// <summary>
    /// A local, upright, 1920px JPEG copy of the untouched original, made on first use and then reused.
    /// Edit Studio, the viewer and the slideshow work from it, so a 20 MB original on a slow or cloud-synced
    /// drive is read once instead of on every slider change. Returns the original path when the photo is
    /// already small and browser-readable. The cache key includes size and modified time of the original.
    /// </summary>
    public string EnsurePreview(Photo photo)
    {
        bool native = BrowserNativeExtensions.Contains(Path.GetExtension(photo.FilePath));
        if (native && photo.Width > 0 && Math.Max(photo.Width, photo.Height) <= PreviewSize && photo.FileSize < 3_000_000)
            return photo.FilePath;

        var fi = new FileInfo(photo.FilePath);
        var key = $"{photo.FilePath.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var name = "prev_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 10).ToLowerInvariant() + ".jpg";
        var path = Path.Combine(PreviewDir, name);
        if (File.Exists(path)) return path;

        lock (PreviewLocks.GetOrAdd(path, _ => new object()))
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(PreviewDir);
            var bmp = Decode(photo.FilePath, PreviewSize);
            bmp = ResizeToFit(bmp, PreviewSize);
            try { WriteAtomic(path, EncodeJpeg(bmp, 88)); }
            finally { bmp.Dispose(); }
        }
        PreviewLocks.TryRemove(path, out _);
        return path;
    }

    /// <summary>The thumbnail to show for a photo: edited variant when present, else the cached original one.</summary>
    public string? ResolveThumb(Photo photo)
    {
        if (photo.Rotation != 0 || photo.HasEdits)
        {
            var edited = ThumbPathFor(photo.FilePath, edited: true);
            lock (LockForThumb(edited))
            {
                if (File.Exists(edited) && !LooksTruncated(edited)) return edited;
            }
            try { RefreshEditedThumbnail(photo); if (File.Exists(edited)) return edited; }
            catch (Exception) { /* fall back to the plain thumbnail */ }
        }
        if (!string.IsNullOrEmpty(photo.ThumbPath))
        {
            lock (LockForThumb(photo.ThumbPath))
            {
                if (File.Exists(photo.ThumbPath)) return photo.ThumbPath;
            }
        }
        try { return GenerateThumbnail(photo.FilePath).ThumbPath; }
        catch (Exception) { return null; }
    }

    /// <summary>Resolves and atomically reads the thumbnail bytes under the per-thumbnail lock.</summary>
    public (byte[] Bytes, string ContentType, DateTimeOffset LastModified, string ResolvedThumbPath)? ReadThumbBytes(Photo photo)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var thumb = ResolveThumb(photo);
            if (thumb is not null)
            {
                lock (LockForThumb(thumb))
                {
                    if (File.Exists(thumb))
                        return (File.ReadAllBytes(thumb), "image/jpeg", File.GetLastWriteTimeUtc(thumb), thumb);
                }
            }
        }
        if (File.Exists(photo.FilePath) && BrowserNativeExtensions.Contains(Path.GetExtension(photo.FilePath)))
            return (File.ReadAllBytes(photo.FilePath), ContentTypeFor(photo.FilePath), File.GetLastWriteTimeUtc(photo.FilePath), "");
        return null;
    }

    // ------------------------------------------------------------------ edit renderer

    /// <summary>
    /// Applies the non-destructive recipe: rotate, straighten, crop, resize, tone/colour, sharpen, vignette.
    /// </summary>
    /// <param name="maxDim">Longest side of the result, or 0 for full resolution.</param>
    /// <param name="applyEdits">False renders only the catalog rotation (used for the editor's base image).</param>
    /// <param name="engineOnly">With applyEdits false: still run the OpenCV operations (red-eye, denoise, clarity,
    /// detail) of this recipe. The editor uses it, because those cannot be previewed with CSS filters.</param>
    public static SKBitmap Render(string filePath, System.Text.Json.Nodes.JsonObject? edits, int photoRotation, int maxDim, bool applyEdits = true, EditRecipe? engineOnly = null)
    {
        var e = applyEdits ? EditRecipe.From(edits).WithLuckyApplied() : new EditRecipe();

        int decodeTarget = 0;
        if (maxDim > 0)
        {
            double keep = e.HasCrop ? Math.Max(0.05, Math.Min(e.Crop![2], e.Crop[3])) : 1.0;
            decodeTarget = (int)Math.Min(16000, Math.Ceiling(maxDim / keep * (Math.Abs(e.Straighten) > 0.01 ? 1.25 : 1.0)));
        }

        var bmp = Decode(filePath, decodeTarget);
        bmp = Transform(bmp, photoRotation + e.Rotation, false);
        if (Math.Abs(e.Straighten) > 0.01) bmp = Straighten(bmp, e.Straighten);
        if (e.HasCrop) bmp = Crop(bmp, e.Crop!);
        bmp = ResizeToFit(bmp, maxDim);

        // Engine operations run before tone and colour, in the preview's base image and in the final render alike.
        var engine = applyEdits ? e : engineOnly;
        if (engine is not null) PhotoEngine.Apply(bmp, engine);

        if (applyEdits)
        {
            ApplyColor(bmp, e);
            if (e.Effect == "sharpen") bmp = Sharpen(bmp, 0.6f);
            if (edits?["design"] is System.Text.Json.Nodes.JsonObject design) PhotoDesign.Draw(bmp, design, filePath);
        }
        return bmp;
    }

    public static byte[] RenderJpeg(string filePath, System.Text.Json.Nodes.JsonObject? edits, int photoRotation, int maxDim, int quality = 90, bool applyEdits = true, EditRecipe? engineOnly = null)
    {
        using var bmp = Render(filePath, edits, photoRotation, maxDim, applyEdits, engineOnly);
        return EncodeJpeg(bmp, quality);
    }

    /// <summary>Scale that makes a rotated w×h image still cover its own frame (no empty corners).</summary>
    public static double StraightenScale(double w, double h, double degrees)
    {
        double a = Math.Abs(degrees) * Math.PI / 180.0;
        return Math.Cos(a) + Math.Max(w / h, h / w) * Math.Sin(a);
    }

    private static SKBitmap Straighten(SKBitmap src, double degrees)
    {
        var dst = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, src.AlphaType));
        float scale = (float)StraightenScale(src.Width, src.Height, degrees);
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Translate(src.Width / 2f, src.Height / 2f);
            canvas.RotateDegrees((float)degrees);
            canvas.Scale(scale);
            canvas.Translate(-src.Width / 2f, -src.Height / 2f);
            canvas.DrawBitmap(src, 0, 0, paint);
        }
        src.Dispose();
        return dst;
    }

    private static SKBitmap Crop(SKBitmap src, double[] c)
    {
        int x = Math.Clamp((int)Math.Round(c[0] * src.Width), 0, src.Width - 1);
        int y = Math.Clamp((int)Math.Round(c[1] * src.Height), 0, src.Height - 1);
        int w = Math.Clamp((int)Math.Round(c[2] * src.Width), 1, src.Width - x);
        int h = Math.Clamp((int)Math.Round(c[3] * src.Height), 1, src.Height - y);
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, src.AlphaType));
        using (var canvas = new SKCanvas(dst))
            canvas.DrawBitmap(src, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
        src.Dispose();
        return dst;
    }

    /// <summary>Copies the bitmap's RGBA bytes into a managed array (no pointer arithmetic, no unsafe code).</summary>
    private static byte[] ReadPixels(SKBitmap bmp)
    {
        var buffer = new byte[bmp.RowBytes * bmp.Height];
        Marshal.Copy(bmp.GetPixels(), buffer, 0, buffer.Length);
        return buffer;
    }

    private static void WritePixels(SKBitmap bmp, byte[] buffer) =>
        Marshal.Copy(buffer, 0, bmp.GetPixels(), Math.Min(buffer.Length, bmp.RowBytes * bmp.Height));

    /// <summary>In-place tone LUT + colour matrix + vignette over an RGBA8888 bitmap.</summary>
    public static void ApplyColor(SKBitmap bmp, EditRecipe e)
    {
        var lut = ToneMath.BuildLut(e);
        var md = ToneMath.BuildMatrix(e);
        double vignette = e.VignetteStrength;
        bool lutId = ToneMath.IsIdentity(lut), matId = ToneMath.IsIdentity(md);
        if (lutId && matId && vignette <= 0) return;

        var m = new float[12];
        for (int i = 0; i < 12; i++) m[i] = (float)md[i];
        float o0 = m[3] * 255f, o1 = m[7] * 255f, o2 = m[11] * 255f;

        int w = bmp.Width, h = bmp.Height, stride = bmp.RowBytes;
        var px = ReadPixels(bmp);
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double invCx = cx > 0 ? 1.0 / cx : 0, invCy = cy > 0 ? 1.0 / cy : 0;
        const double invSqrt2 = 0.70710678118654752;

        Parallel.For(0, h, y =>
        {
            int p = y * stride;
            double dy = (y - cy) * invCy;
            for (int x = 0; x < w; x++, p += 4)
            {
                float r = lut[px[p]], g = lut[px[p + 1]], b = lut[px[p + 2]];
                if (!matId)
                {
                    float nr = m[0] * r + m[1] * g + m[2] * b + o0;
                    float ng = m[4] * r + m[5] * g + m[6] * b + o1;
                    float nb = m[8] * r + m[9] * g + m[10] * b + o2;
                    r = nr; g = ng; b = nb;
                }
                if (vignette > 0)
                {
                    double dx = (x - cx) * invCx;
                    float f = (float)ToneMath.VignetteFactor(Math.Sqrt(dx * dx + dy * dy) * invSqrt2, vignette);
                    r *= f; g *= f; b *= f;
                }
                px[p] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r + 0.5f);
                px[p + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g + 0.5f);
                px[p + 2] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b + 0.5f);
            }
        });
        WritePixels(bmp, px);
    }

    /// <summary>3×3 cross sharpen: centre 1+4a, neighbours −a (same kernel as the preview's feConvolveMatrix).</summary>
    private static SKBitmap Sharpen(SKBitmap bmp, float amount)
    {
        int w = bmp.Width, h = bmp.Height, stride = bmp.RowBytes;
        if (w < 3 || h < 3) return bmp;
        var src = ReadPixels(bmp);
        var dst = (byte[])src.Clone();
        float centre = 1 + 4 * amount;

        Parallel.For(1, h - 1, y =>
        {
            int row = y * stride;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int k = i + c;
                    float v = centre * src[k] - amount * (src[k - 4] + src[k + 4] + src[k - stride] + src[k + stride]);
                    dst[k] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5f);
                }
            }
        });
        WritePixels(bmp, dst);
        return bmp;
    }

    // ------------------------------------------------------------------ export

    /// <summary>Renders the photo at full resolution with its saved recipe into <paramref name="destDir"/>.</summary>
    public static string Export(Photo photo, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var stem = Path.GetFileNameWithoutExtension(photo.FilePath);
        var temp = Path.Combine(destDir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, ExifInfo.CopyExif(photo.FilePath, RenderJpeg(photo.FilePath, photo.Edits, photo.Rotation, 0, 95)));
            for (int n = 1; ; n++)
            {
                var suffix = n == 1 ? "" : $"_{n}";
                var target = Path.Combine(destDir, $"{stem}_svacitra_export{suffix}.jpg");
                try { File.Move(temp, target); return target; }
                catch (IOException) when (File.Exists(target)) { }
            }
        }
        finally { File.Delete(temp); }
    }

    // ------------------------------------------------------------------ statistics (offline AI heuristics)

    public static ImageStats ComputeStats(string path)
    {
        var (fw, fh) = ReadDimensions(path);
        using var bmp = ResizeToFit(Decode(path, 96), 96);
        double sumL = 0, sumL2 = 0, sumS = 0, sr = 0, sg = 0, sb = 0;
        int dark = 0, bright = 0, n = bmp.Width * bmp.Height;
        var pixels = bmp.GetPixelSpan();
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            double r = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, b = pixels[i + 2] / 255.0;
            double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            sumL += l; sumL2 += l * l; sumS += max <= 0 ? 0 : (max - min) / max;
            sr += r; sg += g; sb += b;
            if (l < 0.2) dark++;
            if (l > 0.85) bright++;
        }
        if (n == 0) return default;
        double mean = sumL / n;
        return new ImageStats(mean, Math.Sqrt(Math.Max(0, sumL2 / n - mean * mean)), sumS / n, sr / n, sg / n, sb / n,
            (double)dark / n, (double)bright / n, fw == 0 ? bmp.Width : fw, fh == 0 ? bmp.Height : fh);
    }

    // ------------------------------------------------------------------ collage

    /// <summary>Renders a "picture pile" (scattered polaroids) or a tidy grid and returns the output path.</summary>
    public string RenderCollage(IReadOnlyList<Photo> photos, int width, int height, string type, SKColor background)
    {
        width = Math.Clamp(width, 320, 8000);
        height = Math.Clamp(height, 240, 8000);
        using var canvasBmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(canvasBmp);
        canvas.Clear(background);

        int count = photos.Count;
        bool grid = string.Equals(type, "grid", StringComparison.OrdinalIgnoreCase);
        var kind = count == 0 ? "empty" : grid ? "grid" : "picture_pile";
        var outPath = Path.Combine(_paths.CollageDir, $"collage_{kind}_{Guid.NewGuid().ToString("N")[..8]}.jpg");

        if (count > 0)
        {
            int cols = (int)Math.Ceiling(Math.Sqrt(count * (double)width / height));
            cols = Math.Clamp(cols, 1, count);
            int rows = (int)Math.Ceiling(count / (double)cols);
            float margin = grid ? 16 : 60;
            float cellW = (width - margin * 2) / cols, cellH = (height - margin * 2) / rows;
            var rnd = new Random(42);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 110), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9) };
            using var white = new SKPaint { Color = new SKColor(250, 250, 252), IsAntialias = true };

            for (int i = 0; i < count; i++)
            {
                SKBitmap photo;
                try { photo = Render(photos[i].FilePath, photos[i].Edits, photos[i].Rotation, (int)Math.Max(cellW, cellH) * 2); }
                catch (Exception) { continue; }
                using (photo)
                {
                    float cx = margin + (i % cols) * cellW + cellW / 2, cy = margin + (i / cols) * cellH + cellH / 2;
                    if (grid)
                    {
                        // Centre-crop each photo to fill its cell.
                        var cell = SKRect.Create(cx - cellW / 2 + 4, cy - cellH / 2 + 4, cellW - 8, cellH - 8);
                        float srcAspect = (float)photo.Width / photo.Height, dstAspect = cell.Width / cell.Height;
                        SKRect src = srcAspect > dstAspect
                            ? SKRect.Create((photo.Width - photo.Height * dstAspect) / 2, 0, photo.Height * dstAspect, photo.Height)
                            : SKRect.Create(0, (photo.Height - photo.Width / dstAspect) / 2, photo.Width, photo.Width / dstAspect);
                        canvas.DrawBitmap(photo, src, cell, paint);
                    }
                    else
                    {
                        float aspect = (float)photo.Height / photo.Width;
                        float cardW = Math.Min(cellW * 0.92f, cellH * 0.92f / Math.Max(aspect, 0.5f));
                        cardW = Math.Max(cardW, 80);
                        float cardH = cardW * aspect, border = cardW * 0.04f, bottom = cardW * 0.13f;
                        float pw = cardW + border * 2, ph = cardH + border + bottom;

                        canvas.Save();
                        canvas.Translate(cx + rnd.Next(-20, 21), cy + rnd.Next(-20, 21));
                        canvas.RotateDegrees((float)(rnd.NextDouble() * 24 - 12));
                        canvas.DrawRect(SKRect.Create(-pw / 2 + 5, -ph / 2 + 8, pw, ph), shadow);
                        canvas.DrawRect(SKRect.Create(-pw / 2, -ph / 2, pw, ph), white);
                        canvas.DrawBitmap(photo, SKRect.Create(-pw / 2 + border, -ph / 2 + border, cardW, cardH), paint);
                        canvas.Restore();
                    }
                }
            }
        }

        canvas.Flush();
        WriteAtomic(outPath, EncodeJpeg(canvasBmp, 92));
        return outPath;
    }

    // ------------------------------------------------------------------ io helpers

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
