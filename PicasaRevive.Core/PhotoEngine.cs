using System.Runtime.InteropServices;
using ImageMagick;
using OpenCvSharp;
using SkiaSharp;

namespace PicasaRevive.Core;

/// <summary>
/// Features built on two open-source engines:
///   OpenCV (Apache-2.0, via OpenCvSharp4)     - noise reduction, clarity, unsharp-mask sharpening, red-eye removal
///   ImageMagick (Apache-2.0, via Magick.NET)  - decoding HEIC/HEIF, AVIF and camera RAW files
/// Everything works on upright RGBA8888 bitmaps, in place.
/// </summary>
public static class PhotoEngine
{
    /// <summary>Formats that only ImageMagick can read here (Skia and the browser cannot).</summary>
    public static readonly string[] MagickExtensions =
    {
        ".heic", ".heif", ".avif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw",
    };

    public static string OpenCvVersion
    {
        get { try { return Cv2.GetVersionString() ?? ""; } catch (Exception) { return ""; } }
    }

    public static string MagickVersion
    {
        get { try { return MagickNET.Version; } catch (Exception) { return ""; } }
    }

    // ------------------------------------------------------------------ OpenCV: pixel operations

    /// <summary>Applies red-eye removal, noise reduction, clarity and sharpening from the recipe (in that order).</summary>
    public static void Apply(SKBitmap bmp, EditRecipe e)
    {
        if (!e.HasEngineOps || bmp.Width < 8 || bmp.Height < 8) return;

        using var rgba = Mat.FromPixelData(bmp.Height, bmp.Width, MatType.CV_8UC4, bmp.GetPixels(), bmp.RowBytes);
        var bgr = new Mat();
        try
        {
            Cv2.CvtColor(rgba, bgr, ColorConversionCodes.RGBA2BGR);
            if (e.RedEye) RemoveRedEye(bgr);
            if (e.Denoise > 0) Swap(ref bgr, Denoise(bgr, e.Denoise));
            if (e.Clarity > 0) Swap(ref bgr, Clarity(bgr, e.Clarity));
            if (e.Detail > 0) Swap(ref bgr, UnsharpMask(bgr, e.Detail));
            // rgba wraps the bitmap's own memory and already has the right size and type, so this writes in place.
            Cv2.CvtColor(bgr, rgba, ColorConversionCodes.BGR2RGBA);
        }
        finally
        {
            bgr.Dispose();
        }
    }

    private static void Swap(ref Mat current, Mat replacement)
    {
        current.Dispose();
        current = replacement;
    }

    /// <summary>Non-local means denoising. Strength 1..100 maps to filter strength 3..13.</summary>
    private static Mat Denoise(Mat bgr, double strength)
    {
        float h = (float)(3 + Math.Clamp(strength, 0, 100) * 0.10);
        var dst = new Mat();
        Cv2.FastNlMeansDenoisingColored(bgr, dst, h, h, 7, 21);
        return dst;
    }

    /// <summary>Local contrast: CLAHE on the lightness channel, blended in by strength.</summary>
    private static Mat Clarity(Mat bgr, double strength)
    {
        double a = Math.Clamp(strength, 0, 100) / 100.0;
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var ch = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(1.0 + 2.0 * a, new Size(8, 8));
            using var equalized = new Mat();
            clahe.Apply(ch[0], equalized);
            Cv2.AddWeighted(ch[0], 1 - a, equalized, a, 0, ch[0]);
            Cv2.Merge(ch, lab);
        }
        finally
        {
            foreach (var c in ch) c.Dispose();
        }
        var dst = new Mat();
        Cv2.CvtColor(lab, dst, ColorConversionCodes.Lab2BGR);
        return dst;
    }

    /// <summary>Unsharp mask. The blur radius follows the image size, so preview and full-size export look alike.</summary>
    private static Mat UnsharpMask(Mat bgr, double strength)
    {
        double a = Math.Clamp(strength, 0, 100) / 100.0 * 1.5;
        double sigma = Math.Max(0.8, Math.Max(bgr.Width, bgr.Height) / 1600.0);
        using var blur = new Mat();
        Cv2.GaussianBlur(bgr, blur, new Size(0, 0), sigma);
        var dst = new Mat();
        Cv2.AddWeighted(bgr, 1 + a, blur, -a, 0, dst);
        return dst;
    }

    /// <summary>
    /// Finds small, round, strongly red blobs (flash-lit pupils) and replaces their red channel with the
    /// average of green and blue. There is no face detection: other small round bright-red objects can be
    /// affected too. Returns the number of spots corrected.
    /// </summary>
    public static int RemoveRedEye(Mat bgr)
    {
        var ch = Cv2.Split(bgr);
        try
        {
            Mat b = ch[0], g = ch[1], r = ch[2];
            using var maxGb = new Mat();
            Cv2.Max(g, b, maxGb);

            // Strict test seeds the blobs; the loose test limits how far the fix may spread.
            using var strict = RedMask(r, maxGb, 1.8, 95);
            using var loose = RedMask(r, maxGb, 1.3, 60);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.MorphologyEx(strict, strict, MorphTypes.Close, kernel);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(strict, labels, stats, centroids);

            double imageArea = (double)bgr.Width * bgr.Height;
            double minArea = Math.Max(6, imageArea * 0.000004), maxArea = imageArea * 0.002;
            using var mask = new Mat(bgr.Rows, bgr.Cols, MatType.CV_8UC1, Scalar.All(0));
            int found = 0;
            for (int i = 1; i < n; i++)
            {
                int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), area = stats.At<int>(i, 4);
                if (area < minArea || area > maxArea || w < 2 || h < 2) continue;
                double aspect = (double)w / h;
                if (aspect < 0.6 || aspect > 1.7) continue;
                if (area < 0.45 * w * h) continue; // not round enough
                Cv2.Ellipse(mask, new Point(x + w / 2, y + h / 2), new Size((int)(w * 0.75) + 1, (int)(h * 0.75) + 1), 0, 0, 360, Scalar.All(255), -1);
                found++;
            }
            if (found == 0) return 0;

            Cv2.BitwiseAnd(mask, loose, mask);
            Cv2.GaussianBlur(mask, mask, new Size(0, 0), Math.Max(0.6, Math.Max(bgr.Width, bgr.Height) / 2500.0));

            using var m = new Mat();
            mask.ConvertTo(m, MatType.CV_32F, 1.0 / 255);
            using var r32 = new Mat();
            r.ConvertTo(r32, MatType.CV_32F);
            using var avg = new Mat();
            Cv2.AddWeighted(g, 0.5, b, 0.5, 0, avg, MatType.CV_32F);
            using var diff = new Mat();
            Cv2.Subtract(avg, r32, diff);
            Cv2.Multiply(diff, m, diff);
            Cv2.Add(r32, diff, r32);
            r32.ConvertTo(r, MatType.CV_8U);
            Cv2.Merge(ch, bgr);
            return found;
        }
        finally
        {
            foreach (var c in ch) c.Dispose();
        }
    }

    /// <summary>255 where red &gt; factor * max(green, blue) and red &gt; minRed.</summary>
    private static Mat RedMask(Mat r, Mat maxGb, double factor, double minRed)
    {
        using var scaled = new Mat();
        maxGb.ConvertTo(scaled, MatType.CV_8U, factor);
        var mask = new Mat();
        Cv2.Compare(r, scaled, mask, CmpTypes.GT);
        using var bright = new Mat();
        Cv2.Threshold(r, bright, minRed, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(mask, bright, mask);
        return mask;
    }

    // ------------------------------------------------------------------ ImageMagick: extra file formats

    /// <summary>Decodes HEIC/AVIF/RAW (and anything else ImageMagick knows) upright, on white. Null on failure.</summary>
    public static SKBitmap? DecodeWithMagick(string path)
    {
        try
        {
            var ping = new MagickImageInfo(path);
            if (ping.Width == 0 || ping.Height == 0 ||
                ping.Width > ImageOps.MaxImageDimension || ping.Height > ImageOps.MaxImageDimension ||
                (long)ping.Width * ping.Height > ImageOps.MaxImagePixels)
                return null;

            using var img = new MagickImage(path);
            img.AutoOrient();
            if (img.HasAlpha)
            {
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaAction.Remove);
            }
            int w = (int)img.Width, h = (int)img.Height;
            if (w <= 0 || h <= 0 || w > ImageOps.MaxImageDimension || h > ImageOps.MaxImageDimension || (long)w * h > ImageOps.MaxImagePixels)
                return null;

            using var pixels = img.GetPixels();
            var data = pixels.ToByteArray(PixelMapping.RGBA);
            if (data is null || data.Length < (long)w * h * 4) return null;

            var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
            var dst = bmp.GetPixels();
            int stride = bmp.RowBytes, row = w * 4;
            for (int y = 0; y < h; y++)
                Marshal.Copy(data, y * row, dst + y * stride, row);
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Upright pixel size without decoding the pixels, or (0, 0).</summary>
    public static (int Width, int Height) PingDimensions(string path)
    {
        try
        {
            var info = new MagickImageInfo(path);
            bool swap = info.Orientation is ImageMagick.Orientation.LeftTop or ImageMagick.Orientation.RightTop or ImageMagick.Orientation.RightBottom or ImageMagick.Orientation.LeftBottom;
            return swap ? ((int)info.Height, (int)info.Width) : ((int)info.Width, (int)info.Height);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }
}
