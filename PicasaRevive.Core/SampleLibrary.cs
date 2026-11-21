using SkiaSharp;

namespace PicasaRevive.Core;

/// <summary>Generates a small demo library on first run so the app never starts empty.</summary>
public static class SampleLibrary
{
    private sealed record Spec(string Folder, string File, SKColor Top, SKColor Bottom, string Kind, DateTime Date);

    private static readonly Spec[] Specs =
    {
        new("2024-08 Summer Vacation", "golden_hour_beach.jpg", new(255, 140, 50), new(40, 50, 110), "sunset", new(2024, 8, 15, 18, 40, 0)),
        new("2024-08 Summer Vacation", "mountain_lake.jpg", new(100, 190, 240), new(30, 90, 60), "mountain", new(2024, 8, 14, 10, 5, 0)),
        new("2024-08 Summer Vacation", "coastal_cliffs.jpg", new(70, 160, 220), new(160, 120, 80), "cliffs", new(2024, 8, 12, 15, 30, 0)),
        new("2024-08 Summer Vacation", "campfire_night.jpg", new(12, 14, 40), new(10, 10, 25), "campfire", new(2024, 8, 11, 21, 45, 0)),
        new("2024-05 Family & Friends", "portrait_park.jpg", new(170, 215, 150), new(90, 140, 85), "portrait", new(2024, 5, 19, 13, 10, 0)),
        new("2024-05 Family & Friends", "birthday_cake.jpg", new(250, 210, 120), new(180, 50, 80), "cake", new(2024, 5, 18, 19, 20, 0)),
        new("2024-05 Family & Friends", "garden_flowers.jpg", new(120, 180, 90), new(50, 110, 50), "flowers", new(2024, 5, 12, 9, 15, 0)),
        new("2023-12 Winter Holiday", "snowy_cabin.jpg", new(205, 220, 245), new(120, 145, 185), "cabin", new(2023, 12, 26, 11, 0, 0)),
        new("2023-12 Winter Holiday", "holiday_lights.jpg", new(30, 26, 60), new(12, 12, 30), "lights", new(2023, 12, 24, 20, 30, 0)),
        new("2023-12 Winter Holiday", "pine_frost.jpg", new(200, 225, 245), new(150, 180, 200), "pines", new(2023, 12, 22, 8, 45, 0)),
    };

    /// <summary>Creates the sample folder tree if it does not exist yet and returns its path.</summary>
    public static string Ensure(string sampleDir)
    {
        if (Directory.Exists(sampleDir) && Directory.EnumerateFiles(sampleDir, "*.jpg", SearchOption.AllDirectories).Any())
            return sampleDir;

        const int w = 1280, h = 850;
        foreach (var s in Specs)
        {
            var folder = Path.Combine(sampleDir, s.Folder);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, s.File);

            using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(bmp))
            {
                using (var bg = new SKPaint())
                {
                    bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), new[] { s.Top, s.Bottom }, null, SKShaderTileMode.Clamp);
                    canvas.DrawRect(0, 0, w, h, bg);
                }
                DrawScene(canvas, s.Kind, w, h);
            }
            File.WriteAllBytes(file, ImageOps.EncodeJpeg(bmp, 90));
            File.SetLastWriteTime(file, s.Date);
            File.SetCreationTime(file, s.Date);
        }
        return sampleDir;
    }

    private static void DrawScene(SKCanvas c, string kind, int w, int h)
    {
        var rnd = new Random(StableSeed(kind));
        using var p = new SKPaint { IsAntialias = true };

        void Glow(float x, float y, float radius, SKColor color)
        {
            using var g = new SKPaint { IsAntialias = true };
            g.Shader = SKShader.CreateRadialGradient(new SKPoint(x, y), radius, new[] { color, color.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
            c.DrawCircle(x, y, radius, g);
        }

        void Ridge(float baseY, float amp, float freq, float phase, SKColor color)
        {
            using var path = new SKPath();
            path.MoveTo(0, h);
            for (int x = 0; x <= w; x += 8)
                path.LineTo(x, baseY + MathF.Sin(x * freq + phase) * amp + MathF.Cos(x * freq * 2.7f + phase) * amp * 0.4f);
            path.LineTo(w, h);
            path.Close();
            p.Color = color;
            c.DrawPath(path, p);
        }

        void Pine(float x, float baseY, float size, SKColor color)
        {
            p.Color = color;
            for (int i = 0; i < 3; i++)
            {
                using var tri = new SKPath();
                float top = baseY - size * (1.0f - i * 0.22f), half = size * (0.18f + i * 0.09f), bottom = baseY - size * (0.55f - i * 0.27f);
                tri.MoveTo(x, top); tri.LineTo(x - half, bottom); tri.LineTo(x + half, bottom); tri.Close();
                c.DrawPath(tri, p);
            }
            p.Color = new SKColor(70, 50, 40);
            c.DrawRect(x - size * 0.03f, baseY - size * 0.05f, size * 0.06f, size * 0.07f, p);
        }

        switch (kind)
        {
            case "sunset":
                Glow(w / 2f, h * 0.55f, 330, new SKColor(255, 225, 150, 210));
                p.Color = new SKColor(255, 244, 200);
                c.DrawCircle(w / 2f, h * 0.55f, 92, p);
                p.Color = new SKColor(28, 34, 84);
                c.DrawRect(0, h * 0.62f, w, h * 0.38f, p);
                for (int i = 0; i < 16; i++)
                {
                    p.Color = new SKColor(255, 190, 110, (byte)(150 - i * 8));
                    float y = h * 0.64f + i * 18, half = 150 - i * 7 + rnd.Next(-18, 18);
                    c.DrawRoundRect(w / 2f - half, y, half * 2, 6, 3, 3, p);
                }
                Ridge(h * 0.93f, 14, 0.006f, 1.2f, new SKColor(60, 44, 40));
                break;
            case "mountain":
                Ridge(h * 0.44f, 85, 0.0052f, 0.4f, new SKColor(96, 132, 150));
                Ridge(h * 0.55f, 60, 0.008f, 2.1f, new SKColor(46, 86, 78));
                p.Color = new SKColor(64, 132, 170);
                c.DrawRect(0, h * 0.72f, w, h * 0.28f, p);
                p.Color = new SKColor(255, 255, 255, 46);
                for (int i = 0; i < 9; i++) c.DrawRoundRect(rnd.Next(40, w - 320), h * 0.75f + i * 20, rnd.Next(140, 300), 4, 2, 2, p);
                for (int i = 0; i < 7; i++) Pine(60 + i * 190 + rnd.Next(-30, 30), h * 0.74f, 120 + rnd.Next(0, 50), new SKColor(22, 52, 40));
                break;
            case "cliffs":
                p.Color = new SKColor(40, 110, 170);
                c.DrawRect(0, h * 0.58f, w, h * 0.42f, p);
                using (var cliff = new SKPath())
                {
                    cliff.MoveTo(w * 0.52f, h); cliff.LineTo(w * 0.58f, h * 0.36f); cliff.LineTo(w * 0.76f, h * 0.30f);
                    cliff.LineTo(w, h * 0.26f); cliff.LineTo(w, h); cliff.Close();
                    p.Color = new SKColor(150, 112, 78);
                    c.DrawPath(cliff, p);
                }
                p.Color = new SKColor(86, 136, 70);
                c.DrawRect(w * 0.58f, h * 0.27f, w * 0.42f, 34, p);
                p.Color = new SKColor(255, 255, 255, 120);
                for (int i = 0; i < 6; i++) c.DrawRoundRect(rnd.Next(20, (int)(w * 0.45f)), h * 0.62f + i * 46, rnd.Next(120, 240), 5, 3, 3, p);
                Glow(w * 0.2f, h * 0.18f, 150, new SKColor(255, 255, 235, 190));
                break;
            case "campfire":
                p.Color = new SKColor(255, 255, 255, 200);
                for (int i = 0; i < 90; i++) c.DrawCircle(rnd.Next(w), rnd.Next((int)(h * 0.55f)), rnd.Next(1, 3), p);
                Ridge(h * 0.62f, 40, 0.007f, 0.8f, new SKColor(14, 20, 30));
                Glow(w / 2f, h * 0.78f, 360, new SKColor(255, 120, 30, 200));
                using (var flame = new SKPath())
                {
                    flame.MoveTo(w / 2f - 70, h * 0.86f); flame.QuadTo(w / 2f - 90, h * 0.70f, w / 2f - 10, h * 0.58f);
                    flame.QuadTo(w / 2f + 10, h * 0.68f, w / 2f + 40, h * 0.62f); flame.QuadTo(w / 2f + 95, h * 0.74f, w / 2f + 70, h * 0.86f); flame.Close();
                    p.Color = new SKColor(255, 150, 40);
                    c.DrawPath(flame, p);
                }
                p.Color = new SKColor(255, 230, 140);
                c.DrawOval(w / 2f, h * 0.80f, 34, 52, p);
                p.Color = new SKColor(50, 30, 20);
                c.DrawRoundRect(w / 2f - 120, h * 0.855f, 240, 22, 10, 10, p);
                break;
            case "portrait":
                for (int i = 0; i < 26; i++) Glow(rnd.Next(w), rnd.Next(h), rnd.Next(50, 130), new SKColor(235, 250, 200, 70));
                p.Color = new SKColor(60, 90, 150);
                c.DrawRoundRect(w / 2f - 190, h * 0.62f, 380, h * 0.5f, 120, 120, p);
                p.Color = new SKColor(240, 200, 170);
                c.DrawRect(w / 2f - 38, h * 0.52f, 76, 90, p);
                c.DrawOval(w / 2f, h * 0.40f, 105, 128, p);
                p.Color = new SKColor(74, 48, 34);
                c.DrawArc(SKRect.Create(w / 2f - 112, h * 0.40f - 140, 224, 230), 180, 180, true, p);
                p.Color = new SKColor(60, 40, 36);
                c.DrawCircle(w / 2f - 38, h * 0.40f, 8, p);
                c.DrawCircle(w / 2f + 38, h * 0.40f, 8, p);
                using (var smile = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, Color = new SKColor(170, 80, 80), StrokeCap = SKStrokeCap.Round })
                    c.DrawArc(SKRect.Create(w / 2f - 42, h * 0.40f + 10, 84, 60), 20, 140, false, smile);
                break;
            case "cake":
                Glow(w / 2f, h * 0.3f, 420, new SKColor(255, 240, 190, 150));
                p.Color = new SKColor(120, 70, 50);
                c.DrawRect(0, h * 0.82f, w, h * 0.18f, p);
                p.Color = new SKColor(250, 236, 220);
                c.DrawRoundRect(w / 2f - 230, h * 0.56f, 460, 230, 24, 24, p);
                p.Color = new SKColor(236, 110, 140);
                c.DrawRoundRect(w / 2f - 240, h * 0.54f, 480, 60, 30, 30, p);
                for (int i = 0; i < 7; i++)
                {
                    float x = w / 2f - 180 + i * 60;
                    p.Color = i % 2 == 0 ? new SKColor(90, 170, 230) : new SKColor(250, 220, 90);
                    c.DrawRoundRect(x - 7, h * 0.42f, 14, 110, 5, 5, p);
                    Glow(x, h * 0.40f, 34, new SKColor(255, 210, 90, 230));
                    p.Color = new SKColor(255, 245, 190);
                    c.DrawOval(x, h * 0.40f, 8, 15, p);
                }
                break;
            case "flowers":
                for (int i = 0; i < 16; i++)
                {
                    float x = rnd.Next(80, w - 80), y = rnd.Next(120, h - 100), r = rnd.Next(34, 70);
                    p.Color = new SKColor(50, 120, 50);
                    c.DrawRect(x - 4, y, 8, h - y, p);
                    var petal = i % 3 == 0 ? new SKColor(240, 90, 150) : i % 3 == 1 ? new SKColor(250, 190, 60) : new SKColor(235, 80, 80);
                    p.Color = petal;
                    for (int k = 0; k < 6; k++)
                    {
                        double a = k * Math.PI / 3;
                        c.DrawCircle(x + (float)Math.Cos(a) * r * 0.62f, y + (float)Math.Sin(a) * r * 0.62f, r * 0.46f, p);
                    }
                    p.Color = new SKColor(255, 225, 120);
                    c.DrawCircle(x, y, r * 0.34f, p);
                }
                break;
            case "cabin":
                Ridge(h * 0.52f, 50, 0.006f, 0.3f, new SKColor(176, 196, 224));
                p.Color = new SKColor(244, 248, 255);
                c.DrawRect(0, h * 0.70f, w, h * 0.30f, p);
                for (int i = 0; i < 5; i++) Pine(90 + i * 110, h * 0.72f, 170, new SKColor(40, 78, 70));
                p.Color = new SKColor(122, 78, 52);
                c.DrawRect(w * 0.56f, h * 0.50f, 330, 190, p);
                using (var roof = new SKPath())
                {
                    roof.MoveTo(w * 0.56f - 34, h * 0.50f); roof.LineTo(w * 0.56f + 165, h * 0.34f); roof.LineTo(w * 0.56f + 364, h * 0.50f); roof.Close();
                    p.Color = new SKColor(246, 250, 255);
                    c.DrawPath(roof, p);
                }
                p.Color = new SKColor(255, 214, 110);
                c.DrawRect(w * 0.56f + 50, h * 0.56f, 70, 62, p);
                c.DrawRect(w * 0.56f + 210, h * 0.56f, 70, 62, p);
                p.Color = new SKColor(255, 255, 255, 220);
                for (int i = 0; i < 140; i++) c.DrawCircle(rnd.Next(w), rnd.Next(h), rnd.Next(1, 4), p);
                break;
            case "lights":
                var palette = new[] { new SKColor(255, 210, 80), new SKColor(255, 110, 90), new SKColor(110, 200, 255), new SKColor(140, 240, 150), new SKColor(240, 140, 255) };
                for (int i = 0; i < 70; i++)
                {
                    var col = palette[rnd.Next(palette.Length)];
                    Glow(rnd.Next(w), rnd.Next(h), rnd.Next(26, 90), col.WithAlpha((byte)rnd.Next(120, 220)));
                }
                using (var wire = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(20, 20, 26) })
                using (var path = new SKPath())
                {
                    path.MoveTo(0, h * 0.2f); path.QuadTo(w * 0.25f, h * 0.36f, w * 0.5f, h * 0.22f); path.QuadTo(w * 0.75f, h * 0.36f, w, h * 0.2f);
                    c.DrawPath(path, wire);
                }
                break;
            case "pines":
                p.Color = new SKColor(246, 250, 255);
                c.DrawRect(0, h * 0.78f, w, h * 0.22f, p);
                for (int i = 0; i < 9; i++) Pine(70 + i * 145 + rnd.Next(-20, 20), h * 0.80f, 260 + rnd.Next(-60, 120), new SKColor(45, 80, 75));
                p.Color = new SKColor(255, 255, 255, 150);
                for (int i = 0; i < 220; i++) c.DrawCircle(rnd.Next(w), rnd.Next(h), rnd.Next(1, 4), p);
                break;
        }
    }

    private static int StableSeed(string s)
    {
        int seed = 17;
        foreach (char ch in s) seed = unchecked(seed * 31 + ch);
        return seed;
    }
}
