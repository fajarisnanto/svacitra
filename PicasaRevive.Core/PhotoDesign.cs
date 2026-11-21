using System.Globalization;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace PicasaRevive.Core;

internal static class PhotoDesign
{
    private static string Text(JsonObject settings, string name, string fallback = "")
    {
        if (settings[name] is not JsonValue value || !value.TryGetValue<string>(out var text)) return fallback;
        return text.Length <= 160 ? text.Trim() : text[..160].Trim();
    }

    public static void Draw(SKBitmap bmp, JsonObject design, string source)
    {
        using var canvas = new SKCanvas(bmp);
        float w = bmp.Width, h = bmp.Height, unit = Math.Min(w, h);
        float border = DrawFrame(canvas, w, h, unit, Text(design, "frame"));
        float pad = border + unit * .035f;
        float available = w - 2 * pad;
        if (available <= 0) return;
        using var regular = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var serif = SKTypeface.FromFamilyName("Georgia", SKFontStyle.BoldItalic);
        using var mono = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold);
        var title = Text(design, "title");
        if (title.Length > 0)
            DrawLine(canvas, title, regular, unit * .055f, pad, pad + unit * .06f, available, "caption", SKColors.White, false);
        var art = Text(design, "art");
        if (art.Length > 0)
        {
            var style = Text(design, "style");
            var face = Text(design, "font") switch { "serif" => serif, "mono" => mono, _ => regular };
            float size = unit * (Text(design, "size") switch { "small" => .065f, "large" => .155f, _ => .105f });
            float baseline = Text(design, "position") switch {
                "upper" => pad + unit * .21f, "lower" => h - pad - unit * .10f, _ => h * .50f + size * .32f };
            var tint = SKColor.TryParse(Text(design, "color"), out var parsed) ? parsed.WithAlpha(255) : SKColors.White;
            DrawLine(canvas, art, face, size, w / 2, baseline, available, style, tint, true);
        }
        var stamp = Text(design, "stamp");
        if (stamp is "date" or "datetime" && ExifInfo.CaptureTime(source) is { } taken)
        {
            var label = taken.ToString(stamp == "date" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            DrawLine(canvas, label, mono, unit * .037f, pad, h - pad, available, "caption", SKColors.White, false);
        }
    }

    private static float DrawFrame(SKCanvas canvas, float w, float h, float unit, string frame)
    {
        if (frame is not ("mat" or "film" or "gilt" or "ink")) return 0;
        float width = unit * (frame == "ink" ? .018f : frame == "gilt" ? .032f : .055f);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };
        paint.Color = frame == "mat" ? new SKColor(249,246,236) : new SKColor(27,26,29);
        canvas.DrawRect(SKRect.Create(width / 2, width / 2, w - width, h - width), paint);
        if (frame == "gilt")
        {
            paint.Color = new SKColor(235,194,108);
            paint.StrokeWidth = width / 8;
            foreach (var inset in new[] { width * .22f, width * .75f })
                canvas.DrawRect(SKRect.Create(inset, inset, w - 2 * inset, h - 2 * inset), paint);
        }
        if (frame == "film")
        {
            paint.Style = SKPaintStyle.Fill;
            paint.Color = new SKColor(222,213,189);
            for (float x = width; x < w - width; x += width * 1.6f)
                foreach (float y in new[] { width * .25f, h - width * .70f })
                    canvas.DrawRoundRect(SKRect.Create(x, y, width * .72f, width * .44f), width * .1f, width * .1f, paint);
        }
        return width;
    }

    private static void DrawLine(SKCanvas canvas, string text, SKTypeface face, float size, float x,
                                 float baseline, float available, string style, SKColor tint, bool centered)
    {
        using var paint = new SKPaint { IsAntialias = true, Typeface = face, TextSize = size,
            TextAlign = centered ? SKTextAlign.Center : SKTextAlign.Left, StrokeJoin = SKStrokeJoin.Round };
        paint.TextSize *= Math.Min(1, available / Math.Max(1, paint.MeasureText(text) + size * .12f));
        size = paint.TextSize;
        paint.Color = new SKColor(12,12,18,210);
        if (style == "retro")
        {
            paint.Color = new SKColor(141,48,65);
            for (int i = 5; i >= 1; i--) canvas.DrawText(text, x + i * size * .019f, baseline + i * size * .019f, paint);
        }
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = size * (style == "outline" ? .105f : .045f);
        using var glow = style == "neon" ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(.5f, size * .08f)) : null;
        if (glow is not null)
        {
            paint.Color = tint; paint.StrokeWidth = size * .15f; paint.MaskFilter = glow;
            canvas.DrawText(text, x, baseline, paint);
            paint.MaskFilter = null; paint.StrokeWidth = size * .045f;
        }
        canvas.DrawText(text, x, baseline, paint);
        paint.Style = SKPaintStyle.Fill;
        paint.Color = style == "neon" ? SKColors.White : tint;
        using var gradient = style == "gold" ? SKShader.CreateLinearGradient(
            new SKPoint(x, baseline - size * .75f), new SKPoint(x, baseline),
            new[] { new SKColor(255,245,181), new SKColor(188,122,39), new SKColor(255,220,119) },
            null, SKShaderTileMode.Clamp) : null;
        paint.Shader = gradient;
        canvas.DrawText(text, x, baseline, paint);
    }
}
