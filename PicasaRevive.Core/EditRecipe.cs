using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PicasaRevive.Core;

/// <summary>
/// Strongly typed view over the non-destructive edit recipe JSON:
/// { brightness, contrast, fill_light, highlights, shadows, warmth, saturation, vignette,
///   effect, crop:[x,y,w,h], straighten, rotation, feeling_lucky, redeye, denoise, clarity, detail, autocontrast, autocolor }.
/// </summary>
public sealed class EditRecipe
{
    public static readonly string[] Effects = { "none", "sepia", "bw", "warmify", "sharpen", "vignette" };

    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double FillLight { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Warmth { get; set; }
    public double Saturation { get; set; }
    /// <summary>Null when the recipe does not mention vignette at all.</summary>
    public double? Vignette { get; set; }
    public string Effect { get; set; } = "none";
    /// <summary>Normalized x, y, w, h (0..1) of the kept area, or null.</summary>
    public double[]? Crop { get; set; }
    public double Straighten { get; set; }
    public int Rotation { get; set; }
    public bool FeelingLucky { get; set; }
    public bool RedEye { get; set; }
    /// <summary>OpenCV engine sliders, 0..100: noise reduction, clarity (local contrast), unsharp-mask sharpening.</summary>
    public double Denoise { get; set; }
    public double Clarity { get; set; }
    public double Detail { get; set; }

    /// <summary>True when any OpenCV-based operation (red-eye, denoise, clarity, detail) is switched on.</summary>
    public bool HasEngineOps => RedEye || Denoise > 0 || Clarity > 0 || Detail > 0;

    /// <summary>Vignette strength 0..1 actually applied: the "vignette" effect defaults to 50.</summary>
    public double VignetteStrength
    {
        get
        {
            double v = Vignette ?? (Effect == "vignette" ? 50 : 0);
            if (Effect != "vignette" && v <= 0) return 0;
            return Math.Clamp(v, 0, 100) / 100.0;
        }
    }

    public bool HasCrop => Crop is { Length: 4 } c && c[2] > 0.01 && c[3] > 0.01 && (c[0] > 0.001 || c[1] > 0.001 || c[2] < 0.999 || c[3] < 0.999);

    /// <summary>"I'm Feeling Lucky" is stored as a flag and expanded here, so preview and render agree.</summary>
    public EditRecipe WithLuckyApplied()
    {
        if (!FeelingLucky) return this;
        var e = (EditRecipe)MemberwiseClone();
        e.Contrast += 15;
        e.Saturation += 18;
        e.Brightness += 5;
        e.Warmth += 5;
        return e;
    }

    public static EditRecipe From(JsonObject? o)
    {
        var e = new EditRecipe();
        if (o is null) return e;
        e.Brightness = Num(o, "brightness");
        e.Contrast = Num(o, "contrast");
        e.FillLight = Num(o, "fill_light");
        e.Highlights = Num(o, "highlights");
        e.Shadows = Num(o, "shadows");
        e.Warmth = Num(o, "warmth");
        e.Saturation = Num(o, "saturation");
        e.Vignette = o.ContainsKey("vignette") && o["vignette"] is not null ? Num(o, "vignette") : null;
        e.Straighten = Math.Clamp(Num(o, "straighten"), -45, 45);
        e.Rotation = NormalizeRotation((int)Math.Round(Num(o, "rotation")));
        e.FeelingLucky = Flag(o, "feeling_lucky");
        e.RedEye = Flag(o, "redeye");
        e.Denoise = Math.Clamp(Num(o, "denoise"), 0, 100);
        e.Clarity = Math.Clamp(Num(o, "clarity"), 0, 100);
        e.Detail = Math.Clamp(Num(o, "detail"), 0, 100);

        var effect = (o["effect"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
        e.Effect = effect is not null && Array.IndexOf(Effects, effect) >= 0 ? effect : "none";

        if (o["crop"] is JsonArray { Count: 4 } arr)
        {
            var c = new double[4];
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                if (!TryNum(arr[i], out c[i])) { ok = false; break; }
            }
            if (ok)
            {
                c[0] = Math.Clamp(c[0], 0, 1);
                c[1] = Math.Clamp(c[1], 0, 1);
                c[2] = Math.Clamp(c[2], 0, 1 - c[0]);
                c[3] = Math.Clamp(c[3], 0, 1 - c[1]);
                e.Crop = c;
            }
        }
        return e;
    }

    public static int NormalizeRotation(int degrees)
    {
        int r = (int)(Math.Round(degrees / 90.0) * 90) % 360;
        return r < 0 ? r + 360 : r;
    }

    private static double Num(JsonObject o, string key) => TryNum(o[key], out var v) ? v : 0;

    private static bool Flag(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        return TryNum(v, out var n) && n != 0;
    }

    internal static bool TryNum(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out value)) return !double.IsNaN(value) && !double.IsInfinity(value);
        // Values built in memory (int, long, decimal...) are not convertible via TryGetValue<double>.
        if (v.GetValueKind() == JsonValueKind.Number)
            return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (v.TryGetValue<JsonElement>(out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value)) return true;
            if (el.ValueKind == JsonValueKind.String)
                return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        if (v.TryGetValue<string>(out var s))
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return false;
    }
}
