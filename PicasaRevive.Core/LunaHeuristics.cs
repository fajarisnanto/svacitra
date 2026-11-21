using System.Text.RegularExpressions;

namespace PicasaRevive.Core;

/// <summary>
/// "Luna Smart Heuristic Engine": the fully offline stand-in for the cloud model.
/// Prompt tuning is keyword driven; photo analysis uses real image statistics plus filename hints.
/// </summary>
public static class LunaHeuristics
{
    public const string ModelName = "Luna Smart Heuristic Engine (Offline)";

    /// <summary>Allowed range of every AI-controlled slider.</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Ranges = new Dictionary<string, (int, int)>
    {
        ["brightness"] = (-50, 50),
        ["contrast"] = (-50, 50),
        ["fill_light"] = (0, 60),
        ["highlights"] = (-50, 50),
        ["shadows"] = (-50, 50),
        ["warmth"] = (-50, 50),
        ["saturation"] = (-50, 50),
        ["vignette"] = (0, 100),
    };

    /// <summary>Drops unknown keys and clamps every value into its documented range.</summary>
    public static Dictionary<string, int> ClampFixes(IEnumerable<KeyValuePair<string, double>>? fixes, bool allowVignette)
    {
        var result = new Dictionary<string, int>();
        if (fixes is null) return result;
        foreach (var (key, value) in fixes)
        {
            if (!Ranges.TryGetValue(key, out var range)) continue;
            if (key == "vignette" && !allowVignette) continue;
            if (double.IsNaN(value)) continue;
            result[key] = (int)Math.Clamp(Math.Round(value), range.Min, range.Max);
        }
        return result;
    }

    public static string NormalizeEffect(string? effect)
    {
        var e = (effect ?? "").Trim().ToLowerInvariant();
        return e switch
        {
            "b&w" or "black_and_white" or "grayscale" or "monochrome" => "bw",
            "warm" => "warmify",
            _ => Array.IndexOf(EditRecipe.Effects, e) >= 0 ? e : "none",
        };
    }

    // ------------------------------------------------------------------ prompt tuning

    private static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));
    private static readonly Regex BwWord = new(@"\b(bw|b&w|b/w|b\s*and\s*w)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Maps a natural-language editing prompt to slider values and an effect. Matching styles are combined
    /// (e.g. "moody black and white" = dramatic + B&amp;W) and every value is clamped at the end.
    /// </summary>
    public static AiPromptEditResult PromptTune(string prompt)
    {
        var p = (prompt ?? "").ToLowerInvariant();
        var f = new Dictionary<string, double>
        {
            ["brightness"] = 0, ["contrast"] = 10, ["fill_light"] = 5, ["highlights"] = 0,
            ["shadows"] = 10, ["warmth"] = 0, ["saturation"] = 10,
        };
        string effect = "none";
        var notes = new List<string>();
        void Add(string key, double delta) => f[key] = f.GetValueOrDefault(key) + delta;

        bool mono = Has(p, "black and white", "black & white", "monochrome", "noir", "grayscale", "greyscale") || BwWord.IsMatch(p);
        bool vintage = Has(p, "vintage", "retro", "film", "35mm", "analog", "sepia", "nostalg");
        bool golden = Has(p, "warm", "golden", "sunset", "sunrise", "cozy");
        bool cool = Has(p, "cool", "cold", "icy", "winter", "blue tone");
        bool bright = Has(p, "bright", "underexpos", "lighten", "lighter", "too dark", "fill light", "expos");
        bool dramatic = Has(p, "dramatic", "moody", "cinematic", "contrast", "punch", "bold", "dark and");
        bool pop = Has(p, "pop", "vibrant", "vivid", "colorful", "colourful", "saturat");
        bool vignette = Has(p, "vignette", "cinematic", "spotlight", "focus on");
        bool sharp = Has(p, "sharp", "crisp", "clarity", "clear", "detail");
        bool soft = Has(p, "soft", "dreamy", "pastel", "muted", "faded", "matte");

        if (golden)
        {
            Add("warmth", 28); Add("saturation", 8); Add("contrast", 4); Add("shadows", 5);
            effect = "warmify";
            notes.Add("infused golden-hour warmth");
        }
        if (cool)
        {
            Add("warmth", -22); Add("contrast", 4);
            notes.Add("cooled the colour temperature");
        }
        if (bright)
        {
            Add("brightness", 22); Add("fill_light", 30); Add("shadows", 15); Add("contrast", 2);
            notes.Add("lifted exposure and opened the shadows with fill light");
        }
        if (dramatic)
        {
            Add("contrast", 18); Add("highlights", -12); Add("shadows", -18); Add("saturation", 4);
            notes.Add("deepened contrast and shadows for a dramatic mood");
        }
        if (pop)
        {
            Add("saturation", 20); Add("contrast", 8);
            effect = "sharpen";
            notes.Add("boosted colour saturation and clarity");
        }
        if (sharp)
        {
            Add("contrast", 8); Add("fill_light", 5);
            effect = "sharpen";
            notes.Add("enhanced edge clarity");
        }
        if (soft)
        {
            Add("contrast", -18); Add("saturation", -12); Add("shadows", 12); Add("highlights", -6);
            notes.Add("softened contrast for a gentle, faded look");
        }
        if (vintage)
        {
            Add("contrast", -18); Add("warmth", 18); Add("shadows", 12); Add("saturation", -22);
            f["vignette"] = Math.Max(f.GetValueOrDefault("vignette"), 35);
            effect = "sepia";
            notes.Add("emulated vintage 35mm film with faded blacks, warm sepia tones and a soft vignette");
        }
        if (vignette)
        {
            f["vignette"] = Math.Max(f.GetValueOrDefault("vignette"), 60);
            Add("contrast", 4);
            if (effect == "none") effect = "vignette";
            notes.Add("framed the edges with a vignette");
        }
        if (mono)
        {
            Add("contrast", 20); Add("brightness", -5); Add("highlights", -10);
            f["saturation"] = 0; f["warmth"] = 0;
            effect = "bw";
            notes.Add("converted to high-contrast black and white");
        }

        string explanation;
        if (notes.Count == 0)
            explanation = $"Applied smart tone balance and colour grading tailored to \"{prompt?.Trim()}\".";
        else
        {
            var joined = notes.Count == 1 ? notes[0] : string.Join(", ", notes.Take(notes.Count - 1)) + " and " + notes[^1];
            explanation = char.ToUpperInvariant(joined[0]) + joined[1..] + ".";
        }

        return new AiPromptEditResult
        {
            Explanation = explanation,
            Fixes = ClampFixes(f, allowVignette: true),
            Effect = effect,
            Model = ModelName,
        };
    }

    // ------------------------------------------------------------------ photo analysis

    /// <summary>
    /// Offline photo analysis. With <paramref name="stats"/> the slider fixes come from the actual pixels
    /// (exposure, contrast, saturation, colour cast); filename and folder supply the scene hints.
    /// </summary>
    public static AiAnalysisResult Analyze(Photo photo, ImageStats? stats)
    {
        var text = (photo.Filename + " " + photo.FolderPath).ToLowerInvariant();
        var tags = new List<string>();
        string? caption = null;
        var f = new Dictionary<string, double>
        {
            ["brightness"] = 5, ["contrast"] = 12, ["fill_light"] = 10, ["highlights"] = -10,
            ["shadows"] = 15, ["warmth"] = 8, ["saturation"] = 14,
        };
        var critique = "Well-balanced frame. Lifting shadows and enhancing warmth brings out optimal colour depth.";

        if (Has(text, "beach", "sunset", "sunrise", "summer", "coast", "ocean", "sea"))
        {
            caption = "A glowing golden hour coastal landscape with sunset tones reflecting across the horizon.";
            tags.AddRange(new[] { "sunset", "beach", "ocean", "golden hour", "summer", "nature", "scenic" });
            f["warmth"] = 14; f["contrast"] = 16;
        }
        else if (Has(text, "mountain", "lake", "trail", "hike", "forest", "cliff"))
        {
            caption = "A serene alpine landscape with clear skies and silhouetted mountain ridges.";
            tags.AddRange(new[] { "mountains", "landscape", "lake", "wilderness", "adventure", "outdoor" });
            f["contrast"] = 15; f["fill_light"] = 12;
        }
        else if (Has(text, "portrait", "friends", "family", "selfie", "wedding", "birthday", "party"))
        {
            caption = "A warm portrait with soft natural lighting and background bokeh.";
            tags.AddRange(new[] { "portrait", "people", "smile", "family", "warm", "outdoor" });
            f["shadows"] = 18; f["warmth"] = 6;
        }
        else if (Has(text, "snow", "winter", "frost", "ski", "holiday", "christmas", "xmas"))
        {
            caption = "A crisp winter scene with snow-draped evergreens and cool ambient light.";
            tags.AddRange(new[] { "winter", "snow", "frost", "cold", "holiday", "evergreen" });
            f["highlights"] = -15; f["contrast"] = 10; f["warmth"] = -4;
        }
        else if (Has(text, "night", "campfire", "lights", "city", "concert"))
        {
            caption = "A low-light night scene with glowing highlights against deep shadows.";
            tags.AddRange(new[] { "night", "lights", "low light", "glow", "evening" });
            f["fill_light"] = 20; f["highlights"] = -18;
        }
        else if (Has(text, "flower", "garden", "macro", "food", "cake"))
        {
            caption = "A colourful close-up with rich detail and vivid tones.";
            tags.AddRange(new[] { "close-up", "colourful", "detail", "garden", "still life" });
            f["saturation"] = 18;
        }

        if (stats is { } s && s.Width > 0)
        {
            // Exposure: steer mean luminance toward mid-grey.
            f["brightness"] = (0.48 - s.MeanLuma) * 70;
            f["fill_light"] = s.DarkFraction > 0.25 ? 10 + s.DarkFraction * 50 : s.MeanLuma < 0.4 ? 10 : 0;
            f["shadows"] = s.DarkFraction > 0.2 ? 10 + s.DarkFraction * 25 : 5;
            f["highlights"] = s.BrightFraction > 0.15 ? -8 - s.BrightFraction * 30 : 0;
            f["contrast"] = s.StdLuma < 0.16 ? 22 : s.StdLuma < 0.24 ? 12 : s.StdLuma > 0.32 ? -4 : 6;
            f["saturation"] = s.MeanSaturation < 0.18 ? 22 : s.MeanSaturation < 0.35 ? 12 : s.MeanSaturation > 0.6 ? -6 : 5;
            double cast = s.MeanR - s.MeanB; // > 0 warm, < 0 cool
            f["warmth"] = Math.Clamp(Math.Max(f["warmth"] - 8, 0) + (cast < -0.04 ? 12 : cast > 0.15 ? -6 : 4), -20, 20);

            tags.Add(s.Width > s.Height * 1.15 ? "landscape orientation" : s.Height > s.Width * 1.15 ? "portrait orientation" : "square");
            if (s.MeanLuma < 0.3) tags.Add("dark");
            else if (s.MeanLuma > 0.68) tags.Add("bright");
            if (s.MeanSaturation > 0.5) tags.Add("vivid");
            else if (s.MeanSaturation < 0.12) tags.Add("monochrome");
            tags.Add(cast > 0.08 ? "warm tones" : cast < -0.05 ? "cool tones" : "neutral tones");

            var issues = new List<string>();
            if (s.MeanLuma < 0.35) issues.Add("underexposed, so brightness and fill light are raised");
            else if (s.MeanLuma > 0.7) issues.Add("bright, so exposure is pulled back slightly");
            if (s.StdLuma < 0.16) issues.Add("low in contrast, so contrast is boosted");
            if (s.BrightFraction > 0.15) issues.Add("heavy in highlights, so they are recovered");
            if (s.MeanSaturation < 0.18 && s.MeanSaturation >= 0.03) issues.Add("muted in colour, so saturation is lifted");
            critique = issues.Count == 0
                ? "Well-exposed frame with balanced tones. A gentle contrast and colour lift adds depth."
                : "The frame is " + string.Join("; ", issues) + ".";
        }

        if (caption is null)
        {
            caption = $"Photograph \"{Path.GetFileNameWithoutExtension(photo.Filename)}\"" +
                      (photo.Year > 1970 ? $" from {photo.Year}-{photo.Month:00}." : ".");
            tags.InsertRange(0, new[] { "photography", "composition", "details" });
        }
        if (photo.Year > 1970) tags.Add(photo.Year.ToString());

        return new AiAnalysisResult
        {
            Caption = caption,
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            Fixes = ClampFixes(f, allowVignette: false),
            Critique = critique,
            Model = ModelName,
        };
    }
}
