using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PicasaRevive.Core;

/// <summary>A failed cloud AI call with a message that is safe and readable to show to the user.</summary>
public sealed class AiException : Exception
{
    public AiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// OpenAI-compatible chat/completions client for "GPT-6 Luna", with the offline heuristic engine as the
/// no-key path. The API key is only ever sent in the Authorization header and is never logged.
/// </summary>
public sealed class AiClient : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly Catalog _catalog;
    private readonly HttpClient _http;

    public AiClient(Catalog catalog, HttpMessageHandler? handler = null)
    {
        _catalog = catalog;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // each request sets its own deadline
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------ routing helpers

    /// <summary>Appends /chat/completions unless the endpoint already ends with it.</summary>
    public static string BuildEndpoint(string? endpoint)
    {
        var e = (string.IsNullOrWhiteSpace(endpoint) ? AiSettings.DefaultEndpoint : endpoint.Trim()).TrimEnd('/');
        return e.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? e : e + "/chat/completions";
    }

    public static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new AiException($"Invalid AI endpoint URL: {endpoint}");
        bool allowed = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!allowed)
            throw new AiException("Security error: non-local AI endpoints must use HTTPS.");
    }

    private static string SanitizeText(string s) => (s ?? "").Replace("<", "").Replace(">", "").Trim();

    /// <summary>
    /// "gpt-6-luna" does not exist on OpenAI's official endpoint (it returned 404), so on api.openai.com
    /// that name, or an empty one, is routed to gpt-4o.
    /// </summary>
    public static string ResolveModel(string? endpoint, string? model)
    {
        var m = (model ?? "").Trim();
        bool official = (endpoint ?? "").Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
        if (official && (m.Length == 0 || m.Equals(AiSettings.LegacyLunaModel, StringComparison.OrdinalIgnoreCase))) return "gpt-4o";
        return m.Length == 0 ? AiSettings.DefaultModel : m;
    }

    /// <summary>Removes ```json ... ``` wrappers and any chatter around the outermost JSON object.</summary>
    public static string StripCodeFences(string content)
    {
        var s = (content ?? "").Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            s = nl >= 0 ? s[(nl + 1)..] : s.TrimStart('`');
            if (s.TrimEnd().EndsWith("```", StringComparison.Ordinal)) s = s.TrimEnd()[..^3];
            s = s.Trim();
        }
        if (!s.StartsWith('{'))
        {
            int start = s.IndexOf('{'), end = s.LastIndexOf('}');
            if (start >= 0 && end > start) s = s[start..(end + 1)];
        }
        return s.Trim();
    }

    // ------------------------------------------------------------------ public API

    public async Task<AiAnalysisResult> AnalyzePhotoAsync(Photo photo, bool forceHeuristic, CancellationToken ct = default)
    {
        var settings = _catalog.GetAiSettings();
        // Image models (GPT-Image-2.5) produce pictures, not text, so analysis and tagging stay on the offline engine.
        if (forceHeuristic || string.IsNullOrWhiteSpace(settings.ApiKey) || IsImageModel(settings.Model))
            return LunaHeuristics.Analyze(photo, TryStats(photo));

        const string system = """
            You are GPT-6 Luna, an expert vision AI and digital photography assistant integrated into svacitra.
            Analyze this image and respond STRICTLY in JSON format with no markdown wrappers or backticks.
            Schema:
            {
              "caption": "A concise, evocative 1-sentence description of the scene",
              "tags": ["tag1", "tag2", "tag3", "tag4", "tag5", "tag6"],
              "fixes": {
                "brightness": integer from -50 to 50,
                "contrast": integer from -50 to 50,
                "fill_light": integer from 0 to 60,
                "highlights": integer from -50 to 50,
                "shadows": integer from -50 to 50,
                "warmth": integer from -50 to 50,
                "saturation": integer from -50 to 50
              },
              "critique": "Brief 1-sentence photography critique on lighting and composition"
            }
            """;
        var user = $"Analyze this photo ({photo.Filename}) taken in {photo.Year}-{photo.Month:00}. Provide semantic tags, scene caption, and optimal Picasa non-destructive lighting slider fixes.";

        var (content, model) = await ChatAsync(settings, system, user, photo, 0.2, 800, ct).ConfigureAwait(false);

        var result = new AiAnalysisResult { Model = model };
        if (TryParseObject(content, out var o))
        {
            result.Caption = SanitizeText(Str(o["caption"]));
            result.Critique = SanitizeText(Str(o["critique"]));
            if (o["tags"] is JsonArray tags)
                result.Tags = tags.Select(t => SanitizeText(Str(t))).Where(t => t.Length > 0).Take(12).ToList();
            result.Fixes = LunaHeuristics.ClampFixes(ReadNumbers(o["fixes"] as JsonObject), allowVignette: false);
        }
        else
        {
            // The model answered in prose: keep it as the caption instead of failing.
            var safe = SanitizeText(content);
            result.Caption = safe.Length > 400 ? safe[..400] : safe;
            result.Tags = new List<string> { "photo", "ai-analyzed" };
            result.Fixes = new Dictionary<string, int> { ["contrast"] = 10, ["warmth"] = 5 };
        }
        return result;
    }

    public async Task<AiPromptEditResult> PromptTuneAsync(Photo photo, string prompt, bool forceHeuristic, CancellationToken ct = default)
    {
        var settings = _catalog.GetAiSettings();
        if (forceHeuristic || string.IsNullOrWhiteSpace(settings.ApiKey) || IsImageModel(settings.Model))
            return LunaHeuristics.PromptTune(prompt);

        const string system = """
            You are GPT-6 Luna, an expert creative AI digital photo editor integrated into svacitra.
            The user wants to stylize or adjust their photo based on their natural language creative prompt.
            Analyze the photo and convert the user's intent into exact Picasa slider parameters and creative effect.
            Respond STRICTLY in JSON format with no markdown wrappers or backticks.
            Schema:
            {
              "explanation": "A concise 1-sentence description of the edits you calculated to satisfy the user's prompt",
              "fixes": {
                "brightness": integer from -50 to 50,
                "contrast": integer from -50 to 50,
                "fill_light": integer from 0 to 60,
                "highlights": integer from -50 to 50,
                "shadows": integer from -50 to 50,
                "warmth": integer from -50 to 50,
                "saturation": integer from -50 to 50,
                "vignette": integer from 0 to 100
              },
              "effect": "none" | "sepia" | "bw" | "warmify" | "sharpen" | "vignette"
            }
            """;
        var user = $"User editing prompt: \"{prompt}\". Photo filename: {photo.Filename}. Calculate optimal tone sliders and creative effect.";

        var (content, model) = await ChatAsync(settings, system, user, photo, 0.3, 600, ct, useEditedInput: true).ConfigureAwait(false);

        var result = new AiPromptEditResult { Model = model };
        if (TryParseObject(content, out var o))
        {
            result.Explanation = Str(o["explanation"]);
            result.Fixes = LunaHeuristics.ClampFixes(ReadNumbers(o["fixes"] as JsonObject), allowVignette: true);
            result.Effect = LunaHeuristics.NormalizeEffect(Str(o["effect"]));
        }
        else
        {
            result.Explanation = content.Length > 400 ? content[..400] : content;
            result.Fixes = new Dictionary<string, int> { ["contrast"] = 15, ["warmth"] = 10 };
            result.Effect = "none";
        }
        return result;
    }

    /// <summary>Cheap connectivity check for the settings dialog (text-only request, no image).</summary>
    public async Task<string> TestConnectionAsync(AiSettings settings, CancellationToken ct = default)
    {
        settings = settings.Normalized();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return "Offline mode: no API key set, the built-in Luna Smart Heuristic Engine will be used.";
        if (IsImageModel(settings.Model)) return await TestImageModelAsync(settings, ct).ConfigureAwait(false);
        var (content, model) = await ChatAsync(settings, "You are a connectivity check. Reply with the single word OK.", "ping", null, 0, 5, ct).ConfigureAwait(false);
        return $"Connected. Model '{model}' replied: {(content.Length > 60 ? content[..60] : content)}";
    }

    // ------------------------------------------------------------------ image models (GPT-Image-2.5)

    /// <summary>Generative edits can take far longer than a chat reply.</summary>
    public static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// True for OpenAI image generation/editing models such as gpt-image-2.5-flare and gpt-image-2.5-sunburst.
    /// They return a new image instead of text, so they go to the Images API rather than chat/completions.
    /// </summary>
    public static bool IsImageModel(string? model) =>
        (model ?? "").Trim().StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);

    private static string ApiBase(string? endpoint)
    {
        var e = (string.IsNullOrWhiteSpace(endpoint) ? AiSettings.DefaultEndpoint : endpoint.Trim()).TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/images/edits", "/images/generations" })
            if (e.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) e = e[..^suffix.Length];
        return e;
    }

    public static string BuildImagesEditEndpoint(string? endpoint) => ApiBase(endpoint) + "/images/edits";

    /// <summary>
    /// Sends the photo (with its saved recipe, at most 2048px) and the prompt to POST /images/edits and
    /// returns the edited image bytes (JPEG). The original file is never touched.
    /// </summary>
    public async Task<(byte[] Image, string Model)> GenerativeEditAsync(Photo photo, string prompt, CancellationToken ct = default)
    {
        var settings = _catalog.GetAiSettings();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new AiException("An API key is required for AI image editing. Add your OpenAI API key in Luna AI settings.");
        var endpoint = BuildImagesEditEndpoint(settings.Endpoint);
        ValidateEndpoint(endpoint);
        var model = settings.Model;

        byte[] input;
        try { input = ImageOps.RenderJpeg(photo.FilePath, photo.Edits, photo.Rotation, 2048, 90); }
        catch (Exception ex) { throw new AiException("Unable to read image for AI editing: " + ex.Message, ex); }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("jpeg"), "output_format");
        var file = new ByteArrayContent(input);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image[]", "photo.jpg");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        var (status, body) = await SendAsync(req, settings, ImageTimeout, ct).ConfigureAwait(false);
        if (status != 200) throw StatusError(status, body, model);

        try
        {
            var data = (JsonNode.Parse(body) as JsonObject)?["data"] as JsonArray;
            var b64 = data is { Count: > 0 } ? Str(data[0]?["b64_json"]) : "";
            if (b64.Length == 0) throw new AiException($"The image model {model} returned no image.");
            return (Convert.FromBase64String(b64), model);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new AiException("Failed to parse the image response: " + Snippet(body), ex);
        }
    }

    /// <summary>Checks the key and model name without generating (and paying for) an image.</summary>
    private async Task<string> TestImageModelAsync(AiSettings settings, CancellationToken ct)
    {
        var url = ApiBase(settings.Endpoint) + "/models/" + Uri.EscapeDataString(settings.Model);
        ValidateEndpoint(url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        var (status, body) = await SendAsync(req, settings, Timeout, ct).ConfigureAwait(false);
        if (status != 200) throw StatusError(status, body, settings.Model);
        return $"Connected. Your key can use image model '{settings.Model}'. No image was generated.";
    }

    private async Task<(int Status, string Body)> SendAsync(HttpRequestMessage req, AiSettings settings, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var resp = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"Network error connecting to AI endpoint ({settings.Endpoint}): the request timed out after {timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            throw new AiException($"Network error connecting to AI endpoint ({settings.Endpoint}): {ex.Message}", ex);
        }
    }

    private static AiException StatusError(int status, string body, string model)
    {
        string detail = Snippet(body);
        try
        {
            if (JsonNode.Parse(body) is JsonObject eo)
            {
                var msg = eo["error"] is JsonObject err ? Str(err["message"]) : Str(eo["error"]);
                if (msg.Length > 0) detail = Snippet(msg);
            }
        }
        catch (JsonException) { }

        return status switch
        {
            401 => new AiException("Authentication Error (401): Invalid API key. Please check your API key in Luna Settings."),
            403 => new AiException($"Access Denied (403): this API key cannot use '{model}'. Image models may need organization verification. {detail}"),
            404 => new AiException($"Model Not Found (404): '{model}' is not available at this endpoint. {detail}"),
            429 => new AiException($"Rate Limit / Quota Exceeded (429): {detail}"),
            _ => new AiException($"AI Service Error ({status}): {detail}"),
        };
    }

    // ------------------------------------------------------------------ transport

    private ImageStats? TryStats(Photo photo)
    {
        try
        {
            var path = !string.IsNullOrEmpty(photo.ThumbPath) && File.Exists(photo.ThumbPath) ? photo.ThumbPath : photo.FilePath;
            return File.Exists(path) ? ImageOps.ComputeStats(path) : null;
        }
        catch (Exception) { return null; }
    }

    private async Task<(string Content, string Model)> ChatAsync(AiSettings settings, string system, string userText, Photo? photo, double temperature, int maxTokens, CancellationToken ct, bool useEditedInput = false)
    {
        var endpoint = BuildEndpoint(settings.Endpoint);
        ValidateEndpoint(endpoint);
        var model = ResolveModel(endpoint, settings.Model);

        var userContent = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = userText } };
        if (photo is not null)
        {
            string dataUri;
            try
            {
                dataUri = useEditedInput
                    ? "data:image/jpeg;base64," + Convert.ToBase64String(ImageOps.RenderJpeg(photo.FilePath, photo.Edits, photo.Rotation, 1024, 85))
                    : BuildDataUri(photo);
            }
            catch (Exception ex) { throw new AiException("Unable to read image for AI analysis: " + ex.Message, ex); }
            userContent.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUri } });
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        HttpResponseMessage resp;
        string body;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            resp = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"Network error connecting to AI endpoint ({settings.Endpoint}): the request timed out after {Timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            throw new AiException($"Network error connecting to AI endpoint ({settings.Endpoint}): {ex.Message}", ex);
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (status != 200)
            {
                string detail = Snippet(body);
                try
                {
                    if (JsonNode.Parse(body) is JsonObject eo)
                    {
                        var msg = eo["error"] is JsonObject err ? Str(err["message"]) : Str(eo["error"]);
                        if (msg.Length > 0) detail = Snippet(msg);
                    }
                }
                catch (JsonException) { }

                bool official = endpoint.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
                if (official && (status == 404 || detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
                    throw new AiException($"OpenAI Error ({status}): Model '{model}' does not exist on api.openai.com. (Tip: use 'gpt-4o' or 'gpt-4o-mini' for OpenAI, or point the endpoint to your Luna gateway.) Original message: {detail}");
                if (status == 401)
                    throw new AiException("Authentication Error (401): Invalid API key. Please check your API key in Luna Settings.");
                if (status == 429)
                    throw new AiException($"Rate Limit / Quota Exceeded (429): {detail}");
                throw new AiException($"AI Service Error ({status}): {detail}");
            }
        }

        string content;
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            var choices = root?["choices"] as JsonArray;
            if (choices is null || choices.Count == 0) throw new AiException($"Empty response from AI model {model}");
            var message = choices[0]?["message"]?["content"];
            // Some gateways return content as an array of parts.
            content = message is JsonArray parts
                ? string.Concat(parts.Select(part => Str(part?["text"])))
                : Str(message);
        }
        catch (JsonException ex)
        {
            throw new AiException("Failed to parse AI response: " + Snippet(body), ex);
        }

        if (string.IsNullOrWhiteSpace(content)) throw new AiException($"Empty response from AI model {model}");
        return (StripCodeFences(content), model);
    }

    private static string BuildDataUri(Photo photo)
    {
        var path = !string.IsNullOrEmpty(photo.ThumbPath) && File.Exists(photo.ThumbPath) ? photo.ThumbPath : photo.FilePath;
        byte[] bytes;
        string mime;
        if (ReferenceEquals(path, photo.FilePath) && (!ImageOps.BrowserNativeExtensions.Contains(Path.GetExtension(path)) || new FileInfo(path).Length > 4_000_000))
        {
            // Originals that are huge or in an exotic format are re-encoded to a modest JPEG first.
            bytes = ImageOps.RenderJpeg(path, null, 0, 1024, 85, applyEdits: false);
            mime = "image/jpeg";
        }
        else
        {
            bytes = File.ReadAllBytes(path);
            mime = ImageOps.ContentTypeFor(path);
        }
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static bool TryParseObject(string content, out JsonObject obj)
    {
        obj = new JsonObject();
        try
        {
            if (JsonNode.Parse(content) is JsonObject o) { obj = o; return true; }
        }
        catch (JsonException) { }
        return false;
    }

    private static IEnumerable<KeyValuePair<string, double>> ReadNumbers(JsonObject? o)
    {
        if (o is null) yield break;
        foreach (var (key, node) in o)
            if (EditRecipe.TryNum(node, out var v)) yield return new(key, v);
    }

    private static string Str(JsonNode? node)
    {
        if (node is not JsonValue v) return "";
        if (v.TryGetValue<string>(out var s)) return s?.Trim() ?? "";
        return v.ToJsonString().Trim('"');
    }

    private static string Snippet(string s)
    {
        s = (s ?? "").Trim().ReplaceLineEndings(" ");
        return s.Length > 300 ? s[..300] + "…" : s.Length == 0 ? "(empty response body)" : s;
    }
}
