using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PicasaRevive.Core;

/// <summary>Shared System.Text.Json settings: snake_case names, identical to the original REST contract.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonObject ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}

/// <summary>One catalogued photo. Property names serialize to the snake_case fields the frontend expects.</summary>
public sealed class Photo
{
    public long Id { get; set; }
    public string FilePath { get; set; } = "";
    public string Filename { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string DateTaken { get; set; } = "";
    public int Year { get; set; }
    public int Month { get; set; }
    public string FileModified { get; set; } = "";
    public long FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>0 or 1 (kept as an integer for compatibility with the frontend).</summary>
    public int IsStarred { get; set; }
    public int Rotation { get; set; }
    public string ThumbPath { get; set; } = "";
    public JsonObject Edits { get; set; } = new();
    public List<string> AiTags { get; set; } = new();
    public string AiCaption { get; set; } = "";
    public Dictionary<string, int> AiFixes { get; set; } = new();
    public bool Offline { get; set; }
    public bool AiProcessed { get; set; }
    public string AddedAt { get; set; } = "";

    [JsonIgnore]
    public bool HasEdits => Edits.Count > 0;
}

public sealed class AiSettings
{
    public const string DefaultEndpoint = "https://api.openai.com/v1";
    /// <summary>OpenAI GPT-Image-2.5 "Flare": the default image generation/editing model.</summary>
    public const string DefaultModel = "gpt-image-2.5-flare";
    /// <summary>The original chat-model name; on api.openai.com it is routed to gpt-4o.</summary>
    public const string LegacyLunaModel = "gpt-6-luna";
    /// <summary>Sentinel returned to the UI instead of the real secret key.</summary>
    public const string MaskedKey = "••••••••";

    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = DefaultEndpoint;
    public string Model { get; set; } = DefaultModel;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public AiSettings Masked() => new()
    {
        ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? "" : MaskedKey,
        Endpoint = Endpoint,
        Model = Model,
    };

    public AiSettings Normalized() => new()
    {
        ApiKey = (ApiKey ?? "").Trim(),
        Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint : Endpoint.Trim(),
        Model = string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim(),
    };
}

public sealed class ScanStatus
{
    public bool IsScanning { get; set; }
    public string CurrentFolder { get; set; } = "";
    public int ProcessedCount { get; set; }
    public int TotalFound { get; set; }
    public int Errors { get; set; }
    public int Queued { get; set; }
    /// <summary>Increments whenever photos are added to or removed from the catalog.</summary>
    public long LibraryVersion { get; set; }
}

public sealed record FolderSummary(string FolderPath, int Count, string LatestDate, bool Offline = false);
public sealed record TimelineSummary(int Year, int Month, int Count);

public sealed class LibrarySummary
{
    public int TotalPhotos { get; set; }
    public int TotalStarred { get; set; }
    public int AiTaggedCount { get; set; }
    public List<FolderSummary> Folders { get; set; } = new();
    public List<TimelineSummary> Timeline { get; set; } = new();
    public List<string> WatchedFolders { get; set; } = new();
}

public sealed class PhotoQuery
{
    public const int DefaultLimit = 50_000;

    public string? Folder { get; set; }
    public bool StarredOnly { get; set; }
    public bool AiTaggedOnly { get; set; }
    public bool Recent { get; set; }
    public string? Search { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public int Limit { get; set; } = DefaultLimit;
    public int Offset { get; set; }
}

public sealed class AiAnalysisResult
{
    public string Caption { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, int> Fixes { get; set; } = new();
    public string Critique { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AiPromptEditResult
{
    public string Explanation { get; set; } = "";
    public Dictionary<string, int> Fixes { get; set; } = new();
    /// <summary>none | sepia | bw | warmify | sharpen | vignette</summary>
    public string Effect { get; set; } = "none";
    public string Model { get; set; } = "";
}
