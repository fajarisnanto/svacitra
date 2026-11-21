using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PicasaRevive.Core;
using PicasaRevive.Server;
using SkiaSharp;

namespace PicasaRevive.Tests;

/// <summary>
/// Runs the real Kestrel backend on a free loopback port against a temp library, with the cloud AI
/// endpoint replaced by a stub. Verifies the REST contract the frontend depends on.
/// </summary>
public class ApiContractTests : IAsyncLifetime
{
    private readonly TempDir _tmp = new();
    private readonly StubAiHandler _ai = new();
    private readonly HttpClient _http = new();
    private PicasaServer _server = null!;
    private string _picsDir = "";
    private int _shutdownRequests;
    private long _photoId;

    public async Task InitializeAsync()
    {
        _picsDir = _tmp.Sub("pics");
        TestImages.WriteJpeg(_picsDir, "golden_hour_beach.jpg", 1600, 1200, new SKColor(230, 140, 60));
        TestImages.WriteJpeg(_picsDir, "mountain_lake.jpg", 1200, 900, new SKColor(60, 120, 180));
        TestImages.WriteJpeg(Path.Combine(_picsDir, "Winter"), "snowy_cabin.jpg", 900, 600, new SKColor(220, 230, 245));

        _server = await PicasaServer.StartAsync(new ServerOptions
        {
            Paths = new AppPaths(_tmp.Sub("data")),
            AiHandler = _ai,
            GenerateSamplesIfEmpty = false,
            OnShutdownRequested = () => Interlocked.Increment(ref _shutdownRequests),
        });
        _server.Scanner.ScanDirectory(_picsDir);
        _http.BaseAddress = new Uri(_server.BaseUrl);
        _photoId = _server.Catalog.Query(new PhotoQuery { Search = "golden_hour_beach" }).Photos.Single().Id;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _server.Catalog.SaveAiSettings(new AiSettings());
        await _server.StopAsync();
        _tmp.Dispose();
    }

    private async Task<(HttpResponseMessage Response, JsonObject Json)> PostAsync(string url, object? body)
    {
        var res = await _http.PostAsync(url, new StringContent(body is null ? "" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        return (res, (JsonObject)JsonNode.Parse(text)!); // throws if the body is not JSON: that is the contract
    }

    private async Task<JsonObject> GetJsonAsync(string url)
    {
        var res = await _http.GetAsync(url);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        return (JsonObject)JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    private void UseCloud(string endpoint = "https://api.openai.com/v1", string model = "gpt-6-luna") =>
        _server.Catalog.SaveAiSettings(new AiSettings { ApiKey = "sk-test-key", Endpoint = endpoint, Model = model });

    // ------------------------------------------------------------------ library

    [Fact]
    public async Task Server_ListensOnLoopbackOnly_AndServesTheFrontend()
    {
        Assert.StartsWith("http://127.0.0.1:", _server.BaseUrl);
        Assert.True(_server.Port > 0);

        var html = await _http.GetStringAsync("/");
        Assert.Contains("svacitra", html);
        Assert.Contains("js/native-bridge.js", html);
        Assert.Contains("window.PicasaEditor", await _http.GetStringAsync("/js/editor.js"));
    }

    [Fact]
    public async Task Summary_And_Photos_HaveTheDocumentedShape()
    {
        var summary = await GetJsonAsync("/api/summary");
        Assert.Equal(3, (int)summary["total_photos"]!);
        Assert.Equal(0, (int)summary["total_starred"]!);
        Assert.Equal(0, (int)summary["ai_tagged_count"]!);
        Assert.Equal(2, summary["folders"]!.AsArray().Count);
        Assert.NotNull(summary["scan_status"]!["is_scanning"]);
        Assert.Equal("gpt-image-2.5-flare", (string)summary["ai_settings"]!["model"]!);

        var all = await GetJsonAsync("/api/photos");
        Assert.Equal(3, (int)all["count"]!);
        Assert.Equal(3, (int)all["total"]!);
        var photo = all["photos"]![0]!.AsObject();
        foreach (var field in new[] { "id", "file_path", "filename", "folder_path", "date_taken", "year", "month", "width", "height", "is_starred", "rotation", "edits", "ai_processed", "ai_tags" })
            Assert.True(photo.ContainsKey(field), $"photo.{field} missing");

        var folder = await GetJsonAsync("/api/photos?folder=" + Uri.EscapeDataString(Path.Combine(_picsDir, "Winter")));
        Assert.Equal(1, (int)folder["total"]!);

        var paged = await GetJsonAsync("/api/photos?limit=1&offset=1");
        Assert.Equal(1, (int)paged["count"]!);
        Assert.Equal(3, (int)paged["total"]!);
    }

    [Fact]
    public async Task Star_Rotate_SaveEdit_RoundTrip()
    {
        var (_, star) = await PostAsync($"/api/photo/{_photoId}/star", null);
        Assert.Equal(1, (int)star["is_starred"]!);
        Assert.Equal(1, (int)(await GetJsonAsync("/api/photos?starred=1"))["total"]!);

        var (_, rot) = await PostAsync($"/api/photo/{_photoId}/rotate", new { });     // default delta is 90
        Assert.Equal(90, (int)rot["rotation"]!);
        var (_, rot2) = await PostAsync($"/api/photo/{_photoId}/rotate", new { delta = -90 });
        Assert.Equal(0, (int)rot2["rotation"]!);

        var (_, saved) = await PostAsync($"/api/photo/{_photoId}/save-edit", new { edits = new { brightness = 20, effect = "sepia" } });
        Assert.True((bool)saved["success"]!);
        var photo = await GetJsonAsync($"/api/photo/{_photoId}");
        Assert.Equal(20, (int)photo["edits"]!["brightness"]!);
        Assert.Equal("sepia", (string)photo["edits"]!["effect"]!);

        var (missing, error) = await PostAsync("/api/photo/999999/star", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(string.IsNullOrEmpty((string)error["error"]!));
    }

    [Fact]
    public async Task Image_FastPathStreamsTheOriginalBytes_EditedPhotosAreRendered()
    {
        var file = _server.Catalog.GetPhoto(_photoId)!.FilePath;
        var original = await File.ReadAllBytesAsync(file);

        var untouched = await _http.GetByteArrayAsync($"/api/image/{_photoId}?max_dim=1920");
        Assert.Equal(original, untouched);                       // no decode, no re-encode

        await PostAsync($"/api/photo/{_photoId}/save-edit", new { edits = new { effect = "bw" } });

        var rendered = await _http.GetByteArrayAsync($"/api/image/{_photoId}?max_dim=800");
        Assert.NotEqual(original, rendered);
        using (var bmp = SKBitmap.Decode(rendered))
        {
            Assert.Equal(800, bmp.Width);
            var px = bmp.GetPixel(400, 300);
            Assert.True(Math.Abs(px.Red - px.Blue) <= 3, "B&W recipe should be baked into the rendered image");
        }

        // The editor's base image ignores the recipe (the live preview applies it) and stays on the fast path.
        Assert.Equal(original, await _http.GetByteArrayAsync($"/api/image/{_photoId}?max_dim=1920&raw=1"));

        var thumb = await _http.GetAsync($"/api/thumb/{_photoId}");
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType?.MediaType);
        using var thumbBmp = SKBitmap.Decode(await thumb.Content.ReadAsByteArrayAsync());
        Assert.Equal(380, thumbBmp.Width);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/image/999999")).StatusCode);
    }

    [Fact]
    public async Task Export_WritesFiles_ForSingleAndMultipleSelection()
    {
        var dest = Path.Combine(_tmp.Path, "out");
        var ids = _server.Catalog.GetAllIds();

        var (_, one) = await PostAsync("/api/export", new { photo_id = _photoId, dest_dir = dest });
        Assert.True((bool)one["exported"]!);
        Assert.True(File.Exists((string)one["path"]!));

        var (_, many) = await PostAsync("/api/export", new { photo_ids = ids, dest_dir = dest });
        Assert.Equal(3, (int)many["count"]!);
        Assert.Equal(4, Directory.GetFiles(dest, "*.jpg").Length);
    }

    [Fact]
    public async Task Errors_AreJson_AndForeignOriginsAreRejected()
    {
        var unknown = await _http.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/json", unknown.Content.Headers.ContentType?.MediaType);

        var (bad, scan) = await PostAsync("/api/scan", new { path = @"Z:\definitely\not\here" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False((bool)scan["started"]!);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown");
        req.Headers.Add("Origin", "https://evil.example");
        var forbidden = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, _shutdownRequests);
    }

    [Fact]
    public async Task Shutdown_RepliesThenInvokesTheHostCallback()
    {
        var (_, reply) = await PostAsync("/api/shutdown", null);
        Assert.Equal("shutting_down", (string)reply["status"]!);

        for (int i = 0; i < 40 && Volatile.Read(ref _shutdownRequests) == 0; i++) await Task.Delay(50);
        Assert.Equal(1, _shutdownRequests);
    }

    // ------------------------------------------------------------------ AI: offline

    [Theory]
    [InlineData("golden sunset")]
    [InlineData("vintage 35mm film")]
    [InlineData("moody black and white")]
    [InlineData("brighten and pop")]
    [InlineData("cinematic vignette")]
    public async Task PromptTune_WithoutApiKey_UsesOfflineEngine_AndNeverCallsTheCloud(string prompt)
    {
        var (res, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((bool)json["success"]!);
        var result = json["result"]!.AsObject();
        Assert.Equal(LunaHeuristics.ModelName, (string)result["model"]!);
        Assert.Contains((string)result["effect"]!, EditRecipe.Effects);
        Assert.False(string.IsNullOrWhiteSpace((string)result["explanation"]!));
        foreach (var (key, value) in result["fixes"]!.AsObject())
        {
            var (min, max) = LunaHeuristics.Ranges[key];
            Assert.InRange((int)value!, min, max);
        }
        Assert.Equal(0, _ai.Calls);
    }

    [Fact]
    public async Task PromptTune_Validation_IsJson()
    {
        var (_, empty) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "   " });
        Assert.False((bool)empty["success"]!);
        Assert.Equal("Prompt cannot be empty", (string)empty["error"]!);

        var (_, missing) = await PostAsync("/api/ai/prompt-tune", new { photo_id = 424242, prompt = "warm" });
        Assert.Equal("Photo not found in catalog", (string)missing["error"]!);

        var notPost = await GetJsonAsync("/api/ai/prompt-tune");
        Assert.Equal("Method not allowed", (string)notPost["error"]!);

        var garbage = await _http.PostAsync("/api/ai/prompt-tune", new StringContent("{not json", Encoding.UTF8, "application/json"));
        var parsed = (JsonObject)JsonNode.Parse(await garbage.Content.ReadAsStringAsync())!;
        Assert.False((bool)parsed["success"]!);
    }

    [Fact]
    public async Task Analyze_WithoutApiKey_TagsThePhotoOffline()
    {
        var (_, json) = await PostAsync("/api/ai/analyze", new { photo_id = _photoId });

        Assert.True((bool)json["success"]!);
        Assert.True((bool)json["photo"]!["ai_processed"]!);
        Assert.NotEmpty(json["result"]!["tags"]!.AsArray());
        Assert.False(string.IsNullOrWhiteSpace((string)json["result"]!["critique"]!));
        Assert.Equal(1, (int)(await GetJsonAsync("/api/summary"))["ai_tagged_count"]!);
        Assert.Equal(1, (int)(await GetJsonAsync("/api/photos?ai_tagged=1"))["total"]!);
    }

    // ------------------------------------------------------------------ AI: cloud failures must stay JSON

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "API error (404): The model `gpt-6-luna` does not exist", "text/plain", "does not exist")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided\"}}", "application/json", "401")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"You exceeded your current quota\"}}", "application/json", "429")]
    [InlineData(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>", "text/html", "502")]
    public async Task CloudErrors_ReturnReadableJsonWithOfflineFallback(HttpStatusCode status, string body, string contentType, string expectedInError)
    {
        UseCloud();
        _ai.Status = status;
        _ai.Body = body;
        _ai.ContentType = contentType;

        foreach (var (url, payload) in new (string, object)[]
        {
            ("/api/ai/analyze", new { photo_id = _photoId }),
            ("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "golden sunset" }),
        })
        {
            var (res, json) = await PostAsync(url, payload);   // PostAsync asserts the JSON content type and parses

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.False((bool)json["success"]!);
            Assert.Contains(expectedInError, (string)json["error"]!);
            Assert.DoesNotContain("sk-test-key", json.ToJsonString());
            var fallback = json["fallback"]!.AsObject();
            Assert.Equal(LunaHeuristics.ModelName, (string)fallback["model"]!);
            Assert.NotEmpty(fallback["fixes"]!.AsObject());
        }
        Assert.False(_server.Catalog.GetPhoto(_photoId)!.AiProcessed);   // a failed call must not tag the photo
    }

    [Fact]
    public async Task NetworkFailure_IsReportedClearly_WithFallback()
    {
        UseCloud("https://luna.invalid/v1");
        _ai.Throw = new HttpRequestException("No such host is known.");

        var (_, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "dramatic" });

        Assert.False((bool)json["success"]!);
        Assert.StartsWith("Network error connecting to AI endpoint (https://luna.invalid/v1)", (string)json["error"]!);
        Assert.NotNull(json["fallback"]);
    }

    [Fact]
    public async Task FallbackHeuristicFlag_SkipsTheCloudEvenWithAKey()
    {
        UseCloud();
        var (_, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "warm", fallback_heuristic = true });

        Assert.True((bool)json["success"]!);
        Assert.Equal(LunaHeuristics.ModelName, (string)json["result"]!["model"]!);
        Assert.Equal(0, _ai.Calls);
    }

    // ------------------------------------------------------------------ AI: cloud success

    [Fact]
    public async Task OpenAiEndpoint_RoutesGpt6LunaToGpt4o_AndSendsAnOpenAiCompatibleRequest()
    {
        UseCloud("https://api.openai.com/v1", "GPT-6-Luna");
        _ai.Body = StubAiHandler.Completion("{\"explanation\":\"Warmed it up.\",\"fixes\":{\"warmth\":20},\"effect\":\"warmify\"}");

        var (_, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "golden sunset" });

        Assert.True((bool)json["success"]!);
        Assert.Equal("gpt-4o", (string)json["result"]!["model"]!);
        Assert.Equal("https://api.openai.com/v1/chat/completions", _ai.LastUrl);
        Assert.Equal("Bearer sk-test-key", _ai.LastAuthorization);

        var sent = JsonNode.Parse(_ai.LastRequestBody!)!;
        Assert.Equal("gpt-4o", (string)sent["model"]!);
        Assert.Equal(0.3, (double)sent["temperature"]!, 3);
        Assert.Equal(600, (int)sent["max_tokens"]!);
        Assert.Equal("system", (string)sent["messages"]![0]!["role"]!);
        var parts = sent["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("text", (string)parts[0]!["type"]!);
        Assert.Contains("User editing prompt: \"golden sunset\". Photo filename: golden_hour_beach.jpg.", (string)parts[0]!["text"]!);
        Assert.Equal("image_url", (string)parts[1]!["type"]!);
        Assert.StartsWith("data:image/jpeg;base64,", (string)parts[1]!["image_url"]!["url"]!);
    }

    [Fact]
    public async Task CustomGateway_KeepsTheLunaModelName_AndDoesNotDoubleTheCompletionsPath()
    {
        UseCloud("https://luna.example.com/v1/chat/completions/", "gpt-6-luna");
        _ai.Body = StubAiHandler.Completion("{\"caption\":\"A beach.\",\"tags\":[\"beach\"],\"fixes\":{\"contrast\":5},\"critique\":\"Nice.\"}");

        var (_, json) = await PostAsync("/api/ai/analyze", new { photo_id = _photoId });

        Assert.True((bool)json["success"]!);
        Assert.Equal("https://luna.example.com/v1/chat/completions", _ai.LastUrl);
        var sent = JsonNode.Parse(_ai.LastRequestBody!)!;
        Assert.Equal("gpt-6-luna", (string)sent["model"]!);
        Assert.Equal(0.2, (double)sent["temperature"]!, 3);
        Assert.Equal(800, (int)sent["max_tokens"]!);
        Assert.Contains("Analyze this photo (golden_hour_beach.jpg) taken in ", (string)sent["messages"]![1]!["content"]![0]!["text"]!);
        Assert.Equal("A beach.", (string)json["photo"]!["ai_caption"]!);
    }

    [Fact]
    public async Task ModelOutput_InMarkdownFences_IsParsed_AndValuesAreClamped()
    {
        UseCloud("https://luna.example.com/v1");
        _ai.Body = StubAiHandler.Completion("```json\n{\"explanation\":\"Pushed hard.\",\"fixes\":{\"brightness\":500,\"fill_light\":-9,\"vignette\":140,\"bogus\":7},\"effect\":\"Sepia\"}\n```");

        var (_, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "push it" });

        Assert.True((bool)json["success"]!);
        var result = json["result"]!;
        Assert.Equal("Pushed hard.", (string)result["explanation"]!);
        Assert.Equal("sepia", (string)result["effect"]!);
        Assert.Equal(50, (int)result["fixes"]!["brightness"]!);
        Assert.Equal(0, (int)result["fixes"]!["fill_light"]!);
        Assert.Equal(100, (int)result["fixes"]!["vignette"]!);
        Assert.Null(result["fixes"]!["bogus"]);
    }

    [Fact]
    public async Task ModelOutput_ThatIsNotJson_StillSucceedsWithTheTextAsExplanation()
    {
        UseCloud("https://luna.example.com/v1");
        _ai.Body = StubAiHandler.Completion("I would add some warmth and contrast.");

        var (_, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "nicer" });

        Assert.True((bool)json["success"]!);
        Assert.Equal("I would add some warmth and contrast.", (string)json["result"]!["explanation"]!);
        Assert.Equal("none", (string)json["result"]!["effect"]!);
    }

    [Fact]
    public async Task AiSettings_RoundTrip_AndAppearInSummary()
    {
        var (_, saved) = await PostAsync("/api/ai/settings", new { model = "gpt-4o-mini", endpoint = "https://api.openai.com/v1", api_key = "sk-abc" });
        Assert.True((bool)saved["success"]!);
        Assert.Equal("gpt-4o-mini", (string)saved["ai_settings"]!["model"]!);
        Assert.Equal(AiSettings.MaskedKey, (string)saved["ai_settings"]!["api_key"]!);

        var settings = await GetJsonAsync("/api/ai/settings");
        Assert.Equal(AiSettings.MaskedKey, (string)settings["api_key"]!);
        var summary = await GetJsonAsync("/api/summary");
        Assert.Equal("gpt-4o-mini", (string)summary["ai_settings"]!["model"]!);
        Assert.Equal(AiSettings.MaskedKey, (string)summary["ai_settings"]!["api_key"]!);

        // The real key is decrypted in memory for AI calls, but never stored in plaintext in SQLite.
        Assert.Equal("sk-abc", _server.Catalog.GetAiSettings().ApiKey);
        var needle = Encoding.UTF8.GetBytes("sk-abc");
        static byte[] ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        Assert.False(ReadShared(_server.Paths.DatabaseFile).AsSpan().IndexOf(needle) >= 0);
        var wal = _server.Paths.DatabaseFile + "-wal";
        if (File.Exists(wal))
            Assert.False(ReadShared(wal).AsSpan().IndexOf(needle) >= 0);
    }

    // ------------------------------------------------------------------ AI: image model (GPT-Image-2.5)

    [Fact]
    public async Task ImageModel_PromptCreatesAnEditedCopy_ViaImagesEdits_AndLeavesTheOriginalAlone()
    {
        UseCloud("https://api.openai.com/v1", "gpt-image-2.5-flare");
        var original = _server.Catalog.GetPhoto(_photoId)!;
        var originalBytes = await File.ReadAllBytesAsync(original.FilePath);
        var generated = ImageOps.RenderJpeg(original.FilePath, new JsonObject { ["effect"] = "bw" }, 0, 512);
        _ai.Body = JsonSerializer.Serialize(new { created = 1, data = new[] { new { b64_json = Convert.ToBase64String(generated) } } });

        var (_, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "make it a snowy winter scene" });

        Assert.True((bool)json["success"]!);
        Assert.True((bool)json["result"]!["generated"]!);
        Assert.Equal("gpt-image-2.5-flare", (string)json["result"]!["model"]!);
        Assert.Equal("https://api.openai.com/v1/images/edits", _ai.LastUrl);
        Assert.Equal("Bearer sk-test-key", _ai.LastAuthorization);
        Assert.Contains("gpt-image-2.5-flare", _ai.LastRequestBody);
        Assert.Contains("make it a snowy winter scene", _ai.LastRequestBody);
        Assert.Contains("name=\"image[]\"", _ai.LastRequestBody);

        var copy = json["photo"]!;
        Assert.Equal("golden_hour_beach_luna.jpg", (string)copy["filename"]!);
        Assert.Equal(original.FolderPath, (string)copy["folder_path"]!);
        Assert.Equal(generated, await File.ReadAllBytesAsync((string)copy["file_path"]!));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(original.FilePath));
        Assert.Equal(4, (int)(await GetJsonAsync("/api/summary"))["total_photos"]!);

        // A second run never overwrites the first copy.
        var (_, again) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "again" });
        Assert.Equal("golden_hour_beach_luna_2.jpg", (string)again["photo"]!["filename"]!);
    }

    [Fact]
    public async Task ImageModel_FailureIsJsonWithOfflineFallback_AndWritesNothing()
    {
        UseCloud("https://api.openai.com/v1", "gpt-image-2.5-sunburst");
        _ai.Status = HttpStatusCode.Unauthorized;
        _ai.Body = "{\"error\":{\"message\":\"Incorrect API key provided\"}}";

        var (_, json) = await PostAsync("/api/ai/prompt-tune", new { photo_id = _photoId, prompt = "golden sunset" });

        Assert.False((bool)json["success"]!);
        Assert.Contains("401", (string)json["error"]!);
        Assert.Equal("warmify", (string)json["fallback"]!["effect"]!);
        Assert.Equal(3, _server.Catalog.Count());
    }

    [Fact]
    public async Task ImageModel_AnalysisStaysOffline_AndNeverCallsTheCloud()
    {
        UseCloud("https://api.openai.com/v1", "gpt-image-2.5-flare");

        var (_, json) = await PostAsync("/api/ai/analyze", new { photo_id = _photoId });

        Assert.True((bool)json["success"]!);
        Assert.Equal(LunaHeuristics.ModelName, (string)json["result"]!["model"]!);
        Assert.Equal(0, _ai.Calls);
    }

    // ------------------------------------------------------------------ AI: batch

    [Fact]
    public async Task BatchAnalyze_ReturnsImmediately_RunsInBackground_FallsBackWhenCloudFails()
    {
        UseCloud();
        _ai.Status = HttpStatusCode.NotFound;
        _ai.Body = "API error (404)";
        _ai.ContentType = "text/plain";
        var ids = _server.Catalog.GetAllIds();

        var (_, started) = await PostAsync("/api/ai/batch-analyze", new { photo_ids = ids });
        Assert.True((bool)started["started"]!);
        Assert.Equal(3, (int)started["count"]!);

        JsonObject status = new();
        for (int i = 0; i < 100; i++)
        {
            status = await GetJsonAsync("/api/ai/batch-status");
            if (!(bool)status["running"]!) break;
            await Task.Delay(100);
        }

        Assert.False((bool)status["running"]!);
        Assert.Equal(3, (int)status["done"]!);
        Assert.Equal(3, (int)status["fallbacks"]!);
        Assert.Equal(3, _ai.Calls);
        Assert.Equal(3, (int)(await GetJsonAsync("/api/summary"))["ai_tagged_count"]!);   // "Luna AI Tagged" count
    }

    // ------------------------------------------------------------------ security & validation hardening

    [Fact]
    public async Task RequireAuth_BlocksUnauthenticatedRequests_AndAllowsValidToken()
    {
        using var dir = new TempDir();
        await using var authed = await PicasaServer.StartAsync(new ServerOptions
        {
            Paths = new AppPaths(dir.Sub("data")),
            GenerateSamplesIfEmpty = false,
            RequireAuth = true,
        });

        using var client = new HttpClient { BaseAddress = new Uri(authed.BaseUrl) };
        var anon = await client.GetAsync("/api/summary");
        Assert.Equal(HttpStatusCode.Forbidden, anon.StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/summary");
        req.Headers.Add("X-Picasa-Token", authed.SessionToken);
        var ok = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Shutdown_RejectsGet_AllowsPost()
    {
        var get = await _http.GetAsync("/api/shutdown");
        Assert.NotEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(0, _shutdownRequests);
    }

    [Fact]
    public void ImageOps_ValidateDimensions_RejectsDecompressionBomb()
    {
        Assert.Throws<InvalidDataException>(() => ImageOps.ValidateDimensions(30000, 30000));
        Assert.Throws<InvalidDataException>(() => ImageOps.ValidateDimensions(12000, 12000));
        ImageOps.ValidateDimensions(4000, 3000);
    }

    [Fact]
    public async Task MalformedPaths_Return400InsteadOf500()
    {
        var nullFolder = await _http.GetAsync("/api/photos?folder=%00");
        Assert.Equal(HttpStatusCode.BadRequest, nullFolder.StatusCode);

        var (rmNull, _) = await PostAsync("/api/folders/remove", new { path = "\0" });
        Assert.Equal(HttpStatusCode.BadRequest, rmNull.StatusCode);

        var (rmLong, _) = await PostAsync("/api/folders/remove", new { path = new string('A', 40000) });
        Assert.Equal(HttpStatusCode.BadRequest, rmLong.StatusCode);

        var (expNull, _) = await PostAsync("/api/export", new { photo_id = _photoId, dest_dir = "\0" });
        Assert.Equal(HttpStatusCode.BadRequest, expNull.StatusCode);
    }
}
