using SkiaSharp;

namespace PicasaRevive.Core;

public static class ProofSheet
{
    private const int PerPage = 12;
    public static void Write(string path, string code, IReadOnlyList<Photo> photos, CancellationToken stop)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var doc = SKDocument.CreatePdf(output) ?? throw new IOException("Could not create PDF.");
        using var face = SKTypeface.FromFamilyName("Segoe UI");
        using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var ink = new SKPaint { IsAntialias = true, Typeface = face, Color = new SKColor(35, 35, 35) };
        using var imagePaint = new SKPaint { FilterQuality = SKFilterQuality.High };
        int pages = (photos.Count + PerPage - 1) / PerPage;
        for (int page = 0; page < pages; page++)
        {
            var canvas = doc.BeginPage(595, 842);
            canvas.Clear(SKColors.White);
            ink.Typeface = bold;
            ink.TextSize = 19;
            canvas.DrawText("Photo proofs", 29, 40, ink);
            ink.Typeface = face;
            ink.TextSize = 10;
            canvas.DrawText($"Sheet {code}   |   Page {page + 1} of {pages}", 29, 58, ink);
            for (int cell = 0; cell < PerPage && page * PerPage + cell < photos.Count; cell++)
            {
                stop.ThrowIfCancellationRequested();
                int index = page * PerPage + cell;
                var photo = photos[index];
                float x = 29 + cell % 3 * 182, y = 76 + cell / 3 * 181;
                using var bitmap = ImageOps.Render(photo.FilePath, photo.Edits, photo.Rotation, 720);
                using var jpeg = SKImage.FromEncodedData(ImageOps.EncodeJpeg(bitmap, 86));
                float scale = Math.Min(172f / jpeg.Width, 150f / jpeg.Height);
                float w = jpeg.Width * scale, h = jpeg.Height * scale;
                canvas.DrawImage(jpeg, SKRect.Create(x + (172 - w) / 2, y + (150 - h) / 2, w, h), imagePaint);
                ink.Typeface = bold;
                ink.TextSize = 11;
                string number = (index + 1).ToString("D3");
                canvas.DrawText(number, x, y + 165, ink);
                float captionX = x + ink.MeasureText(number) + 6;
                ink.Typeface = face;
                ink.TextSize = 9;
                canvas.Save();
                canvas.ClipRect(SKRect.Create(captionX, y + 151, x + 172 - captionX, 18));
                canvas.DrawText(photo.Filename, captionX, y + 165, ink);
                canvas.Restore();
            }
            ink.Typeface = face;
            ink.TextSize = 9;
            canvas.DrawText("Return the sheet code and the numbers of your chosen photos.", 29, 816, ink);
            doc.EndPage();
        }
        doc.Close();
    }
}
