using OpenCvSharp;
using SkiaSharp;

namespace PicasaRevive.Core;

public static class SlideshowVideo
{
    public const int Width = 1280, Height = 720, FramesPerSecond = 24;
    public const int SlideMilliseconds = 3500;
    private const int FramesPerSlide = SlideMilliseconds * FramesPerSecond / 1000;

    public static void Export(string destination, IReadOnlyList<long> ids, Func<long, Photo?> lookup,
                              Action<int> completed, CancellationToken cancellation)
    {
        if (ids.Count == 0) throw new ArgumentException("Select at least one photo.");
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".picasa-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var writer = new VideoWriter(temp, VideoCaptureAPIs.MSMF, FourCC.FromString("H264"),
                       FramesPerSecond, new Size(Width, Height)))
            {
                if (!writer.IsOpened())
                    throw new InvalidOperationException("Windows could not start its H.264 encoder. Check that Windows Media Features are installed.");
                using var fade = new Mat();
                for (int i = 0; i < ids.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var photo = lookup(ids[i]) ?? throw new InvalidDataException($"Slide {i + 1} is missing from the library.");
                    if (!File.Exists(photo.FilePath)) throw new FileNotFoundException($"Slide {i + 1} is unavailable: {photo.Filename}");
                    using var frame = MakeFrame(photo);
                    for (int n = 0; n < FramesPerSlide; n++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        double opacity = Math.Min(1, Math.Min(n / 14.0, (FramesPerSlide - 1 - n) / 4.0));
                        if (opacity >= 1) writer.Write(frame);
                        else
                        {
                            frame.ConvertTo(fade, frame.Type(), opacity);
                            writer.Write(fade);
                        }
                    }
                    completed(i + 1);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            // The native writer can fail silently (for example, on a full disk). Verify the finalized stream.
            using (var video = new VideoCapture(temp))
            {
                var expected = (long)ids.Count * FramesPerSlide;
                if (!video.IsOpened() || Math.Abs(video.FrameCount - expected) > 1)
                    throw new IOException("The MP4 could not be finalized. Check available disk space and Windows Media Features.");
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Mat MakeFrame(Photo photo)
    {
        using var source = ImageOps.Render(photo.FilePath, photo.Edits, photo.Rotation, Width);
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        float scale = Math.Min((float)Width / source.Width, (float)Height / source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        canvas.DrawBitmap(source, SKRect.Create((Width - w) / 2, (Height - h) / 2, w, h), paint);
        using var bgra = Mat.FromPixelData(Height, Width, MatType.CV_8UC4, bitmap.GetPixels(), bitmap.RowBytes);
        return bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
    }
}
