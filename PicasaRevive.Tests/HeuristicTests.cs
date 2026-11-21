using PicasaRevive.Core;

namespace PicasaRevive.Tests;

public class HeuristicTests
{
    private static void AssertInRange(Dictionary<string, int> fixes)
    {
        foreach (var (key, value) in fixes)
        {
            Assert.True(LunaHeuristics.Ranges.ContainsKey(key), $"unexpected key {key}");
            var (min, max) = LunaHeuristics.Ranges[key];
            Assert.InRange(value, min, max);
        }
    }

    [Theory]
    [InlineData("golden sunset", "warmify")]
    [InlineData("Golden hour warm sunset glow", "warmify")]
    [InlineData("vintage 35mm film", "sepia")]
    [InlineData("Vintage 35mm retro film aesthetic", "sepia")]
    [InlineData("moody black and white", "bw")]
    [InlineData("High contrast moody dramatic black and white", "bw")]
    [InlineData("brighten and pop", "sharpen")]
    [InlineData("Brighten vibrant colors and clarity pop", "sharpen")]
    [InlineData("cinematic vignette", "vignette")]
    [InlineData("Cinematic shadows with subtle dark vignette", "vignette")]
    [InlineData("make it sharp and crisp", "sharpen")]
    [InlineData("just make it nicer", "none")]
    public void PromptTune_PicksTheExpectedEffect(string prompt, string expectedEffect)
    {
        var r = LunaHeuristics.PromptTune(prompt);

        Assert.Equal(expectedEffect, r.Effect);
        Assert.Equal(LunaHeuristics.ModelName, r.Model);
        Assert.False(string.IsNullOrWhiteSpace(r.Explanation));
        AssertInRange(r.Fixes);
    }

    [Fact]
    public void PromptTune_GoldenSunset_WarmsAndSaturates()
    {
        var r = LunaHeuristics.PromptTune("golden sunset");
        Assert.True(r.Fixes["warmth"] > 15);
        Assert.True(r.Fixes["saturation"] > 10);
        Assert.True(r.Fixes["shadows"] > 0);
    }

    [Fact]
    public void PromptTune_BlackAndWhite_RaisesContrastAndKeepsColourNeutral()
    {
        var r = LunaHeuristics.PromptTune("moody black and white");
        Assert.True(r.Fixes["contrast"] >= 30);
        Assert.Equal(0, r.Fixes["warmth"]);
    }

    [Fact]
    public void PromptTune_Vintage_LowersSaturationAndAddsVignette()
    {
        var r = LunaHeuristics.PromptTune("vintage 35mm film");
        Assert.True(r.Fixes["saturation"] < 0);
        Assert.True(r.Fixes["vignette"] > 0);
    }

    [Fact]
    public void PromptTune_Brighten_RaisesBrightnessAndFillLight()
    {
        var r = LunaHeuristics.PromptTune("brighten and pop");
        Assert.True(r.Fixes["brightness"] > 10);
        Assert.True(r.Fixes["fill_light"] > 20);
        Assert.True(r.Fixes["saturation"] > 20);
    }

    [Fact]
    public void PromptTune_Dramatic_DeepensShadowsAndTamesHighlights()
    {
        var r = LunaHeuristics.PromptTune("dramatic");
        Assert.True(r.Fixes["contrast"] > 20);
        Assert.True(r.Fixes["highlights"] < 0);
        Assert.True(r.Fixes["shadows"] < 10);
    }

    [Fact]
    public void PromptTune_IsCaseInsensitive()
    {
        var a = LunaHeuristics.PromptTune("GOLDEN SUNSET");
        var b = LunaHeuristics.PromptTune("golden sunset");
        Assert.Equal(b.Effect, a.Effect);
        Assert.Equal(b.Fixes, a.Fixes);
    }

    [Fact]
    public void PromptTune_EveryKeywordAtOnce_StaysClamped()
    {
        var r = LunaHeuristics.PromptTune("warm golden sunset bright underexposure dramatic pop sharp vignette vintage film cinematic vivid");
        AssertInRange(r.Fixes);
        Assert.Contains(r.Effect, EditRecipe.Effects);
    }

    [Fact]
    public void ClampFixes_ClampsDropsUnknownAndRounds()
    {
        var fixes = LunaHeuristics.ClampFixes(new Dictionary<string, double>
        {
            ["brightness"] = 999,
            ["contrast"] = -999,
            ["fill_light"] = -20,
            ["warmth"] = 12.6,
            ["vignette"] = 250,
            ["rm -rf"] = 1,
        }, allowVignette: true);

        Assert.Equal(50, fixes["brightness"]);
        Assert.Equal(-50, fixes["contrast"]);
        Assert.Equal(0, fixes["fill_light"]);
        Assert.Equal(13, fixes["warmth"]);
        Assert.Equal(100, fixes["vignette"]);
        Assert.False(fixes.ContainsKey("rm -rf"));
    }

    [Fact]
    public void ClampFixes_AnalysisSchemaHasNoVignette()
    {
        var fixes = LunaHeuristics.ClampFixes(new Dictionary<string, double> { ["vignette"] = 40, ["shadows"] = 10 }, allowVignette: false);
        Assert.False(fixes.ContainsKey("vignette"));
        Assert.Equal(10, fixes["shadows"]);
    }

    [Theory]
    [InlineData("B&W", "bw")]
    [InlineData("Sepia", "sepia")]
    [InlineData("something-else", "none")]
    [InlineData(null, "none")]
    public void NormalizeEffect_OnlyReturnsKnownEffects(string? input, string expected) =>
        Assert.Equal(expected, LunaHeuristics.NormalizeEffect(input));

    [Fact]
    public void Analyze_WithoutPixels_UsesFilenameHints()
    {
        var photo = new Photo { Filename = "golden_hour_beach.jpg", FolderPath = @"C:\Pics\2024-08 Summer Vacation", Year = 2024, Month = 8 };
        var r = LunaHeuristics.Analyze(photo, null);

        Assert.Contains("sunset", r.Tags);
        Assert.False(string.IsNullOrWhiteSpace(r.Caption));
        Assert.False(string.IsNullOrWhiteSpace(r.Critique));
        Assert.Equal(LunaHeuristics.ModelName, r.Model);
        AssertInRange(r.Fixes);
        Assert.False(r.Fixes.ContainsKey("vignette"));
    }

    [Fact]
    public void Analyze_DarkPhoto_GetsBrightened()
    {
        var photo = new Photo { Filename = "IMG_0001.jpg", FolderPath = @"C:\Pics", Year = 2024, Month = 1 };
        var dark = new ImageStats(MeanLuma: 0.12, StdLuma: 0.08, MeanSaturation: 0.2, MeanR: 0.12, MeanG: 0.12, MeanB: 0.13, DarkFraction: 0.8, BrightFraction: 0, Width: 4000, Height: 3000);
        var r = LunaHeuristics.Analyze(photo, dark);

        Assert.True(r.Fixes["brightness"] > 15);
        Assert.True(r.Fixes["fill_light"] > 20);
        Assert.Contains("dark", r.Tags);
        Assert.Contains("underexposed", r.Critique);
        AssertInRange(r.Fixes);
    }
}
