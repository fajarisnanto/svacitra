namespace PicasaRevive.Core;

/// <summary>
/// Pure colour math for the edit recipe. It mirrors the live preview in wwwroot/js/editor.js
/// (CSS filter functions plus one SVG tone-curve filter), so what you see is what gets exported.
/// </summary>
public static class ToneMath
{
    /// <summary>Shadows/highlights curve on a 0..1 channel value. Inputs are slider values (-100..100).</summary>
    public static double ToneCurve(double v, double shadows, double highlights)
    {
        const double k = 27.0 / 4.0;                 // normalizes each bump to a peak of 1
        double fs = v * (1 - v) * (1 - v) * k;       // peaks at v = 1/3 (shadows)
        double fh = v * v * (1 - v) * k;             // peaks at v = 2/3 (highlights)
        return Math.Clamp(v + 0.25 * (shadows / 100.0) * fs + 0.25 * (highlights / 100.0) * fh, 0, 1);
    }

    public static double BrightnessFactor(EditRecipe e) => Math.Max(0.1, 1.0 + (e.Brightness + e.FillLight * 0.5) / 100.0);
    public static double ContrastFactor(EditRecipe e) => Math.Max(0.1, 1.0 + e.Contrast / 100.0);
    public static double SaturationFactor(EditRecipe e) => Math.Max(0, 1.0 + e.Saturation / 100.0);

    /// <summary>Per-channel lookup table: tone curve, then brightness, then contrast.</summary>
    public static byte[] BuildLut(EditRecipe e)
    {
        double k = BrightnessFactor(e), c = ContrastFactor(e);
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double v = ToneCurve(i / 255.0, e.Shadows, e.Highlights);
            v = Math.Clamp(v * k, 0, 1);
            v = Math.Clamp((v - 0.5) * c + 0.5, 0, 1);
            lut[i] = (byte)Math.Round(v * 255.0);
        }
        return lut;
    }

    public static bool IsIdentity(byte[] lut)
    {
        for (int i = 0; i < 256; i++) if (lut[i] != i) return false;
        return true;
    }

    /// <summary>
    /// 3x4 row-major colour matrix (offsets in 0..1 units) applied after the LUT:
    /// saturate, then the creative effect, then warmth.
    /// </summary>
    public static double[] BuildMatrix(EditRecipe e)
    {
        var m = Saturate(SaturationFactor(e));
        switch (e.Effect)
        {
            case "sepia":
                m = Mul(Sepia(0.90), m);
                m = Mul(HueRotate(-20), m);
                break;
            case "bw":
                m = Mul(Grayscale(), m);
                m = Mul(Contrast(1.15), m);
                break;
            case "warmify":
                m = Mul(Sepia(0.25), m);
                m = Mul(Saturate(1.30), m);
                break;
        }
        if (e.Warmth > 0) m = Mul(Sepia(Math.Min(1, e.Warmth * 0.3 / 100.0)), m);
        else if (e.Warmth < 0) m = Mul(Cool(e.Warmth), m);
        return m;
    }

    public static bool IsIdentity(double[] m)
    {
        var id = Identity();
        for (int i = 0; i < 12; i++) if (Math.Abs(m[i] - id[i]) > 1e-6) return false;
        return true;
    }

    /// <summary>Vignette multiplier for a normalized distance d (0 = centre, 1 = corner).</summary>
    public static double VignetteFactor(double d, double strength)
    {
        double t = Math.Clamp((d - 0.35) / 0.65, 0, 1);
        return 1.0 - 0.75 * strength * t;
    }

    public static (double R, double G, double B) Apply(double[] m, double r, double g, double b) => (
        Math.Clamp(m[0] * r + m[1] * g + m[2] * b + m[3], 0, 1),
        Math.Clamp(m[4] * r + m[5] * g + m[6] * b + m[7], 0, 1),
        Math.Clamp(m[8] * r + m[9] * g + m[10] * b + m[11], 0, 1));

    // ---- CSS filter function matrices (Filter Effects Module Level 1) ----

    public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };

    public static double[] Saturate(double s) => new[]
    {
        0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s, 0,
        0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s, 0,
        0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s, 0,
    };

    public static double[] Sepia(double amount)
    {
        double a = 1 - Math.Clamp(amount, 0, 1);
        return new[]
        {
            0.393 + 0.607 * a, 0.769 - 0.769 * a, 0.189 - 0.189 * a, 0,
            0.349 - 0.349 * a, 0.686 + 0.314 * a, 0.168 - 0.168 * a, 0,
            0.272 - 0.272 * a, 0.534 - 0.534 * a, 0.131 + 0.869 * a, 0,
        };
    }

    public static double[] Grayscale() => new[]
    {
        0.2126, 0.7152, 0.0722, 0,
        0.2126, 0.7152, 0.0722, 0,
        0.2126, 0.7152, 0.0722, 0,
    };

    public static double[] Contrast(double c) => new[]
    {
        c, 0, 0, 0.5 - 0.5 * c,
        0, c, 0, 0.5 - 0.5 * c,
        0, 0, c, 0.5 - 0.5 * c,
    };

    public static double[] HueRotate(double degrees)
    {
        double rad = degrees * Math.PI / 180.0, cos = Math.Cos(rad), sin = Math.Sin(rad);
        return new[]
        {
            0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928, 0,
            0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283, 0,
            0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072, 0,
        };
    }

    /// <summary>Negative warmth: pull red down and push blue up (warmth is -100..0).</summary>
    public static double[] Cool(double warmth)
    {
        double w = Math.Clamp(warmth, -100, 0) * 0.002;
        return new[] { 1 + w, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 - w, 0 };
    }

    /// <summary>Returns a·b: apply b first, then a.</summary>
    public static double[] Mul(double[] a, double[] b)
    {
        var r = new double[12];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
                r[row * 4 + col] = a[row * 4] * b[col] + a[row * 4 + 1] * b[4 + col] + a[row * 4 + 2] * b[8 + col];
            r[row * 4 + 3] = a[row * 4] * b[3] + a[row * 4 + 1] * b[7] + a[row * 4 + 2] * b[11] + a[row * 4 + 3];
        }
        return r;
    }
}
