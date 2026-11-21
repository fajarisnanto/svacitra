using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PicasaRevive.Core;
using SkiaSharp;

namespace PicasaRevive.Server;

public sealed class ServerOptions
{
    public required AppPaths Paths { get; init; }
    /// <summary>0 picks a free loopback port.</summary>
    public int Port { get; init; }
    /// <summary>Test hook: replaces the HTTP transport used for cloud AI calls.</summary>
    public HttpMessageHandler? AiHandler { get; init; }
    /// <summary>Called (after the reply was sent) when the frontend asks the app to exit.</summary>
    public Action? OnShutdownRequested { get; init; }
    /// <summary>Native folder picker supplied by the desktop shell; returns null when cancelled.</summary>
    public Func<Task<string?>>? PickFolder { get; init; }
    public Func<Task<string?>>? PickProofDestination { get; init; }
    public Func<Task<string?>>? PickCollageDestination { get; init; }
    public Func<int, Task<string?>>? PickVideoDestination { get; init; }
    public bool GenerateSamplesIfEmpty { get; init; } = true;
    /// <summary>Re-scan the watched folders at start: picks up files added, changed or removed while the app
    /// was closed, and redoes thumbnails that were cut off by a partial read.</summary>
    public bool RescanOnStart { get; init; }
    public string? WebRootOverride { get; init; }
    /// <summary>When true, every HTTP request must present the per-launch session cookie or X-Picasa-Token header.</summary>
    public bool RequireAuth { get; init; }
}

/// <summary>
/// The backend: Kestrel bound to 127.0.0.1 only, serving the embedded frontend and the /api/* REST contract.
/// </summary>
public sealed class PicasaServer : IAsyncDisposable
{
    public const string SessionCookieName = "picasa_session";

    private readonly WebApplication _app;
    private readonly ServerOptions _options;
    private readonly BatchState _batch = new();
    private readonly bool _requireAuth;
    private readonly ConcurrentDictionary<string, byte> _approvedExportFolders = new(StringComparer.OrdinalIgnoreCase);
    private int _stopped;
    private int _exportingVideo, _completedSlides;

    public AppPaths Paths { get; }
    public Catalog Catalog { get; }
    public ImageOps Images { get; }
    public Scanner Scanner { get; }
    public AiClient Ai { get; }
    public int Port { get; private set; }
    public string SessionToken { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public void ApproveExportFolder(string? path)
    {
        if (Catalog.IsValidPath(path)) _approvedExportFolders[Catalog.NormalizeDir(path!)] = 0;
    }

    private sealed class BatchState
    {
        public int Running, Total, Done, Failed, Fallbacks;
        public string LastError = "";
    }

    private PicasaServer(ServerOptions options)
    {
        _options = options;
        var envToken = Environment.GetEnvironmentVariable("PICASA_REVIVE_AUTH_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(envToken))
        {
            SessionToken = envToken;
            _requireAuth = true;
        }
        else
        {
            SessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _requireAuth = options.RequireAuth;
        }
        Paths = options.Paths;
        Catalog = new Catalog(Paths.DatabaseFile);
        Images = new ImageOps(Paths);
        Scanner = new Scanner(Catalog, Images);
        Ai = new AiClient(Catalog, options.AiHandler);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = Paths.DataDir });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, options.Port); // loopback only: never reachable from the network
            k.AddServerHeader = false;
        });
        _app = builder.Build();
        Configure(_app, new EmbeddedFrontend(options.WebRootOverride));
    }

    public static async Task<PicasaServer> StartAsync(ServerOptions options)
    {
        var server = new PicasaServer(options);
        await server._app.StartAsync().ConfigureAwait(false);

        var address = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.Port = new Uri(address).Port;

        if (options.GenerateSamplesIfEmpty && server.Catalog.Count() == 0 && server.Catalog.GetWatchedFolders().Count == 0)
        {
            // First run: never start with an empty library.
            _ = Task.Run(() =>
            {
                try { server.Scanner.ScanDirectoryAsync(SampleLibrary.Ensure(server.Paths.SampleLibraryDir)); }
                catch (Exception) { }
            });
        }
        server.Scanner.StartWatching();
        if (options.RescanOnStart)
        {
            try { server.Scanner.RescanAll(); }
            catch (Exception) { /* the library still opens; Tools > Rescan Library can be used later */ }
        }
        return server;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
        Scanner.Dispose();
        Ai.Dispose();
        Catalog.Dispose();
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ====================================================================== pipeline

    private void Configure(WebApplication app, EmbeddedFrontend frontend)
    {
        // Guard: only our own page may call the API (blocks other websites, DNS-rebinding, and unauthenticated local callers).
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var host = ctx.Request.Host;
            bool hostOk = host.Port == Port && (host.Host == "127.0.0.1" || host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
            var origin = ctx.Request.Headers.Origin.ToString();
            bool originOk = origin.Length == 0 || origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}";
            if (!hostOk || !originOk)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "Forbidden: this API only serves the svacitra window." });
                return;
            }

            if (_requireAuth)
            {
                if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path == "/" && TokenMatches(ctx.Request.Query["auth"]))
                {
                    ctx.Response.Cookies.Append(SessionCookieName, SessionToken, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                    });
                    ctx.Response.Redirect("/");
                    return;
                }

                var cookieToken = ctx.Request.Cookies[SessionCookieName];
                var headerToken = ctx.Request.Headers["X-Picasa-Token"].ToString();
                if (!TokenMatches(cookieToken) && !TokenMatches(headerToken))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Forbidden: missing or invalid session token.", success = false });
                    return;
                }
            }

            try
            {
                await next();
            }
            catch (Exception ex) when (!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
            {
                // Errors are always JSON, never an HTML/plain-text error page.
                ctx.Response.Clear();
                if (ex is BadHttpRequestException badReq)
                {
                    ctx.Response.StatusCode = badReq.StatusCode;
                }
                else
                {
                    bool ai = ctx.Request.Path.StartsWithSegments("/api/ai");
                    ctx.Response.StatusCode = ai ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError;
                }
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message });
            }
        });

        var api = app.MapGroup("/api");

        // ---------------------------------------------------------------- library
        api.MapGet("/summary", () =>
        {
            var s = Catalog.GetSummary();
            return J(new
            {
                total_photos = s.TotalPhotos,
                total_starred = s.TotalStarred,
                ai_tagged_count = s.AiTaggedCount,
                folders = s.Folders,
                timeline = s.Timeline,
                watched_folders = s.WatchedFolders,
                scan_status = Scanner.GetStatus(),
                ai_settings = Catalog.GetAiSettings().Masked(),
            });
        });

        api.MapGet("/photos", (HttpRequest req) =>
        {
            var q = req.Query;
            var folder = q["folder"].ToString();
            if (!string.IsNullOrEmpty(folder) && !Catalog.IsValidPath(folder))
                return Error(400, "Invalid folder path");

            var (photos, total) = Catalog.Query(new PhotoQuery
            {
                Folder = folder,
                StarredOnly = Truthy(q["starred"]),
                AiTaggedOnly = Truthy(q["ai_tagged"]),
                Recent = Truthy(q["recent"]),
                Search = q["search"],
                Year = ParseInt(q["year"]),
                Month = ParseInt(q["month"]),
                Limit = ParseInt(q["limit"]),
                Offset = ParseInt(q["offset"]),
            });
            return J(new { photos, count = photos.Count, total });
        });

        api.MapGet("/photo/{id:long}", (long id) =>
            Catalog.GetPhoto(id) is { } p ? J(p) : Error(404, "Photo not found"));

        api.MapPost("/photo/{id:long}/star", (long id) =>
            Catalog.ToggleStar(id) is { } starred ? J(new { id, is_starred = starred }) : Error(404, "Photo not found"));

        api.MapPost("/photo/{id:long}/rotate", async (long id, HttpRequest req) =>
        {
            var body = await ReadBody(req);
            int delta = Int(body, "delta") is { } d && d != 0 ? (int)d : 90;
            if (Catalog.Rotate(id, delta) is not { } rotation) return Error(404, "Photo not found");
            await RefreshThumbAsync(id);
            return J(new { id, rotation });
        });

        api.MapPost("/photos/batch-edit", async (HttpRequest req) =>
        {
            var body = await ReadBody(req);
            if (body["photo_ids"] is not JsonArray { Count: > 0 } items || body["edits"] is not JsonObject { Count: > 0 } patch)
                return Error(400, "Select photos and at least one adjustment.");
            var ids = new HashSet<long>();
            foreach (var item in items)
            {
                if (item is not JsonValue value || !value.TryGetValue<long>(out var id) || id <= 0)
                    return Error(400, "Invalid photo ID.");
                ids.Add(id);
            }
            foreach (var (field, node) in patch)
            {
                if (node is not JsonValue value) return Error(400, "Invalid adjustment.");
                if (field == "effect" && value.TryGetValue<string>(out var name) && EditRecipe.Effects.Contains(name)) continue;
                if (field is "feeling_lucky" or "redeye" && value.TryGetValue<bool>(out _)) continue;
                if (value.TryGetValue<double>(out var number) && double.IsFinite(number))
                {
                    if (field is "brightness" or "contrast" or "saturation" or "warmth" or "fill_light" or "highlights" or "shadows"
                        && number is >= -100 and <= 100) continue;
                    if (field is "denoise" or "clarity" or "detail" or "vignette" && number is >= 0 and <= 100) continue;
                }
                return Error(400, "Invalid batch adjustment.");
            }
            return BatchResponse(Catalog.ApplyBatchEdits(ids, patch));
        });

        IResult BatchResponse(Catalog.BatchResult batch)
        {
            foreach (var photo in batch.Changes) Images.InvalidateEditedThumbnail(photo.Path);
            return J(new { updated = batch.Changes.Select(p => new { id = p.Id, edits = p.Edits }),
                undo_token = batch.UndoToken, skipped = batch.Skipped });
        }
        api.MapGet("/photos/batch-undo", () => J(new { undo_token = Catalog.LastBatchToken() }));
        api.MapPost("/photos/batch-undo", async (HttpRequest req) =>
        {
            var token = Str(await ReadBody(req), "token");
            var result = Catalog.UndoBatch(token);
            return result is null ? Error(409, "The last batch has changed. Refresh and try again.") : BatchResponse(result);
        });

        api.MapPost("/photo/{id:long}/pick", (long id) =>
        {
            return Catalog.MarkPicked(id) ? J(new { id, is_starred = 1 }) : Error(404, "Photo unavailable.");
        });

        api.MapPost("/photo/{id:long}/save-edit", async (long id, HttpRequest req) =>
        {
            var body = await ReadBody(req);
            var edits = body["edits"] as JsonObject;
            if (!Catalog.SaveEdits(id, edits)) return Error(404, "Photo not found");
            await RefreshThumbAsync(id);
            return J(new { success = true, id });
        });

        api.MapGet("/photo/{id:long}/exif", async (long id) =>
        {
            var photo = Catalog.GetPhoto(id);
            if (photo is null) return Error(404, "Photo not found");
            var exists = File.Exists(photo.FilePath);
            var sections = exists ? await Task.Run(() => ExifInfo.Read(photo.FilePath)) : new List<ExifSection>();
            var file = new List<ExifItem>
            {
                new("Name", photo.Filename),
                new("Folder", photo.FolderPath),
                new("Size", FormatSize(photo.FileSize)),
                new("Dimensions", $"{photo.Width} x {photo.Height} px"),
                new("Date", photo.DateTaken.Replace('T', ' ')),
                new("Modified", photo.FileModified.Replace('T', ' ')),
            };
            sections.Insert(0, new ExifSection("File", file));
            return J(new { id, filename = photo.Filename, file_path = photo.FilePath, has_exif = sections.Any(s => s.Title.StartsWith("All: Exif") || s.Title.StartsWith("All: GPS")), sections });
        });

        // ---------------------------------------------------------------- pixels
        api.MapGet("/thumb/{id:long}", async (long id) =>
        {
            var photo = Catalog.GetPhoto(id);
            if (photo is null) return Error(404, "Photo not found");

            var thumbData = await Task.Run(() => Images.ReadThumbBytes(photo));
            if (thumbData is { } t)
            {
                if (!string.IsNullOrEmpty(t.ResolvedThumbPath) && t.ResolvedThumbPath != photo.ThumbPath && !t.ResolvedThumbPath.EndsWith("_e.jpg", StringComparison.Ordinal))
                    Catalog.SetThumbPath(id, t.ResolvedThumbPath);
                return Results.Bytes(t.Bytes, t.ContentType, lastModified: t.LastModified);
            }
            return Error(404, "Thumbnail unavailable");
        });

        api.MapGet("/photo/{id:long}/capture-time", async (long id) =>
        {
            var photo = Catalog.GetPhoto(id);
            if (photo is null) return Error(404, "Photo not found");
            var taken = await Task.Run(() => ExifInfo.CaptureTime(photo.FilePath));
            return J(new { captured = taken?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) });
        });

        api.MapPost("/photo/{id:long}/prompt-preview", async (long id, HttpRequest req) =>
        {
            var photo = Catalog.GetPhoto(id);
            if (photo is null || !File.Exists(photo.FilePath)) return Error(404, "Photo unavailable");
            var body = await ReadBodyOrNull(req);
            if (body?["edits"] is not JsonObject recipe) return Error(400, "Missing edit recipe");
            var bytes = await Task.Run(() => ImageOps.RenderJpeg(photo.FilePath, recipe, photo.Rotation, 1280, 88));
            return Results.Bytes(bytes, "image/jpeg");
        });

        api.MapGet("/image/{id:long}", async (long id, HttpRequest req, HttpResponse res) =>
        {
            var photo = Catalog.GetPhoto(id);
            if (photo is null || !File.Exists(photo.FilePath)) return Error(404, "Photo not found");

            // raw=1 is the editor's base image: no recipe (the live preview applies it), only 90-degree
            // rotation: the catalog rotation plus the recipe rotation the editor passes as "rot".
            bool raw = Truthy(req.Query["raw"]);
            int rotation = EditRecipe.NormalizeRotation(photo.Rotation + (raw ? ParseInt(req.Query["rot"]) : 0));
            bool native = ImageOps.BrowserNativeExtensions.Contains(Path.GetExtension(photo.FilePath));
            // The editor also passes the OpenCV sliders (they have no CSS equivalent), so the base image shows them.
            EditRecipe? engine = null;
            if (raw)
            {
                engine = new EditRecipe
                {
                    RedEye = Truthy(req.Query["redeye"]),
                    Denoise = Math.Clamp(ParseInt(req.Query["denoise"]), 0, 100),
                    Clarity = Math.Clamp(ParseInt(req.Query["clarity"]), 0, 100),
                    Detail = Math.Clamp(ParseInt(req.Query["detail"]), 0, 100),
                };
                if (!engine.HasEngineOps) engine = null;
            }
            int maxDim = Math.Clamp(ParseInt(req.Query["max_dim"]), 0, 16000);

            // Screen-size requests (Edit Studio, viewer, slideshow) work from a cached local 1920px copy, so a
            // large original on a slow or cloud-synced drive is read once, not on every request. Saved edits
            // are still rendered from the original (crop needs its resolution).
            string source = photo.FilePath;
            if (maxDim is > 0 and <= ImageOps.PreviewSize && (raw || !photo.HasEdits))
            {
                try { source = await Task.Run(() => Images.EnsurePreview(photo)); }
                catch (Exception) { source = photo.FilePath; }
            }
            bool sourceNative = native || !ReferenceEquals(source, photo.FilePath);
            bool needsRender = rotation != 0 || (!raw && photo.HasEdits) || !sourceNative || engine is not null;

            // FAST PATH: untouched photos are streamed straight from disk, no decode and no re-encode.
            if (!needsRender)
            {
                res.Headers.CacheControl = "private, max-age=3600";
                return Results.File(source, ImageOps.ContentTypeFor(source),
                    lastModified: File.GetLastWriteTimeUtc(source), enableRangeProcessing: true);
            }

            try
            {
                var bytes = await Task.Run(() => ImageOps.RenderJpeg(source, photo.Edits, raw ? rotation : photo.Rotation, maxDim, 90, applyEdits: !raw, engineOnly: engine));
                res.Headers.CacheControl = "no-cache";
                return Results.File(bytes, "image/jpeg");
            }
            catch (Exception) when (native)
            {
                return Results.File(photo.FilePath, ImageOps.ContentTypeFor(photo.FilePath));
            }
        });

        api.MapPost("/export", async (HttpRequest req) =>
        {
            var body = await ReadBody(req);
            var ids = new List<long>();
            if (body["photo_ids"] is JsonArray arr)
                ids.AddRange(arr.Select(n => EditRecipe.TryNum(n, out var v) ? (long)v : 0).Where(v => v > 0));
            if (Int(body, "photo_id") is { } single && single > 0 && !ids.Contains(single)) ids.Insert(0, single);
            if (ids.Count == 0) return Error(400, "No photo selected for export");

            var rawDest = Str(body, "dest_dir").Trim();
            string destDir;
            if (string.IsNullOrWhiteSpace(rawDest))
            {
                destDir = DefaultExportFolder();
            }
            else
            {
                if (!Catalog.IsValidPath(rawDest) || !Path.IsPathFullyQualified(rawDest))
                    return Error(400, "Invalid export destination directory");
                destDir = Catalog.NormalizeDir(rawDest);
                if (IsForbiddenSystemOrAppPath(destDir))
                    return Error(400, "Exporting to system or application folders is not allowed");
                if (_requireAuth && !IsApprovedExportFolder(destDir))
                    return Error(403, "Export destination must be the default Exports folder or chosen via the folder picker.");
            }

            var paths = new List<string>();
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var id in ids)
                {
                    var p = Catalog.GetPhoto(id);
                    if (p is null || !File.Exists(p.FilePath)) { errors.Add($"Photo {id} not found"); continue; }
                    try { paths.Add(ImageOps.Export(p, destDir)); }
                    catch (Exception ex) { errors.Add($"{p.Filename}: {ex.Message}"); }
                }
            });

            if (paths.Count == 0) return Error(ids.Count == 1 && errors[0].EndsWith("not found") ? 404 : 500, string.Join("; ", errors));
            return J(new { exported = true, path = paths.Count == 1 ? paths[0] : destDir, paths, count = paths.Count, dest_dir = destDir, errors });
        });

        api.MapPost("/collage/generate", async (HttpRequest req) =>
        {
            var body = await ReadBody(req);
            var files = new List<Photo>();
            if (body["photo_ids"] is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    if (!EditRecipe.TryNum(n, out var v)) continue;
                    var p = Catalog.GetPhoto((long)v);
                    if (p is not null && File.Exists(p.FilePath)) files.Add(p);
                }
            }
            int width = Int(body, "width") is { } w && w > 0 ? (int)w : 1920;
            int height = Int(body, "height") is { } h && h > 0 ? (int)h : 1080;
            var bg = SKColor.TryParse(Str(body, "bg_color"), out var parsed) ? parsed : new SKColor(24, 24, 27);
            var type = Str(body, "type");

            var outPath = await Task.Run(() => Images.RenderCollage(files, width, height, type, bg));
            return J(new { success = true, collage_path = outPath, width, height });
        });

        api.MapPost("/proofs/export", async (HttpRequest req) =>
        {
            var body = await ReadBody(req);
            if (body["photo_ids"] is not JsonArray { Count: > 0 } items) return Error(400, "Select photos first.");
            var photos = new List<Photo>();
            var seen = new HashSet<long>();
            foreach (var item in items)
            {
                if (item is not JsonValue value || !value.TryGetValue<long>(out var id)) return Error(400, "Invalid photo ID.");
                if (!seen.Add(id)) continue;
                var photo = Catalog.GetPhoto(id);
                if (photo is null || !File.Exists(photo.FilePath))
                    return Error(404, $"Original unavailable: {photo?.Filename ?? id.ToString()}");
                photos.Add(photo);
            }
            if (_options.PickProofDestination is null) return Error(400, "Open the desktop app to save a PDF.");
            var path = await _options.PickProofDestination();
            if (path is null) return J(new { cancelled = true });
            string code = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{code}.tmp");
            try
            {
                await Task.Run(() => ProofSheet.Write(temp, code, photos, req.HttpContext.RequestAborted));
                Catalog.SetSetting("proof:" + code, Json.Serialize(photos.Select(p => p.FilePath)));
                File.Move(temp, path, true);
                return J(new { code, path, count = photos.Count });
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        });

        api.MapPost("/proofs/resolve", async (HttpRequest req) =>
        {
            var body = await ReadBody(req);
            var code = Str(body, "code").Trim().ToUpperInvariant();
            if (code.Length != 12 || !code.All(Uri.IsHexDigit)) return Error(400, "Enter the sheet code printed on the PDF.");
            var manifest = Catalog.GetSetting("proof:" + code);
            if (manifest is null) return Error(404, "This sheet was not found in this library.");
            var paths = System.Text.Json.JsonSerializer.Deserialize<string[]>(manifest)!;
            if (body["numbers"] is not JsonArray { Count: > 0 } numbers) return Error(400, "Enter at least one proof number.");
            var photos = new List<Photo>();
            var missing = new List<int>();
            var seen = new HashSet<int>();
            foreach (var n in numbers)
            {
                if (n is not JsonValue value || !value.TryGetValue<int>(out var number) || number < 1 || number > paths.Length)
                    return Error(400, $"Use numbers from 1 to {paths.Length}.");
                if (!seen.Add(number)) continue;
                var photo = Catalog.GetPhotoByPath(paths[number - 1]);
                if (photo is null) missing.Add(number); else photos.Add(photo);
            }
            return J(new { photos, missing });
        });

        api.MapGet("/slideshow/export-status", () => J(new { completed = Volatile.Read(ref _completedSlides) }));
        api.MapPost("/slideshow/export", async (HttpRequest req) =>
        {
            var body = await ReadBodyOrNull(req);
            if (body?["photo_ids"] is not JsonArray items || items.Count == 0)
                return Error(400, "Select at least one photo.");
            var ids = new List<long>(items.Count);
            foreach (var item in items)
            {
                if (item is not JsonValue value || !value.TryGetValue<long>(out var id) || id < 1)
                    return Error(400, "Invalid slideshow photo.");
                ids.Add(id);
            }
            if (_options.PickVideoDestination is null) return Error(400, "Open the desktop app to save an MP4.");
            if (Interlocked.CompareExchange(ref _exportingVideo, 1, 0) != 0)
                return Error(409, "Another slideshow export is in progress.");
            try
            {
                Volatile.Write(ref _completedSlides, 0);
                var path = await _options.PickVideoDestination(ids.Count);
                if (path is null) return J(new { cancelled = true });
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(req.HttpContext.RequestAborted, app.Lifetime.ApplicationStopping);
                await Task.Run(() => SlideshowVideo.Export(path, ids, Catalog.GetPhoto,
                    n => Volatile.Write(ref _completedSlides, n), stop.Token), stop.Token);
                return J(new { path });
            }
            finally { Volatile.Write(ref _exportingVideo, 0); }
        });

        api.MapPost("/collage/export", async (HttpRequest req) =>
        {
            var name = Str(await ReadBody(req), "name");
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\Acollage_(grid|picture_pile)_[0-9a-f]{8}\.jpg\z"))
                return Error(400, "Invalid collage name");
            var source = Path.Combine(Paths.CollageDir, name);
            if (!File.Exists(source)) return Error(404, "Collage preview is unavailable");
            if (_options.PickCollageDestination is null) return Error(400, "Save dialog unavailable");
            var path = await _options.PickCollageDestination();
            if (path is not null)
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(source, temp); File.Move(temp, path, true); }
                finally { File.Delete(temp); }
            }
            return J(new { path });
        });

        app.MapGet("/.cache/collages/{name}", (string name) =>
        {
            var file = Path.Combine(Paths.CollageDir, Path.GetFileName(name));
            return File.Exists(file) ? Results.File(file, "image/jpeg") : Error(404, "Collage not found");
        });

        // ---------------------------------------------------------------- folders and scanning
        IResult StartScan(JsonObject body)
        {
            var rawPath = Str(body, "path").Trim().Trim('"');
            string target;
            if (rawPath.Length == 0)
            {
                target = AppPaths.DefaultPicturesFolder();
            }
            else if (rawPath.Equals("sample_library", StringComparison.OrdinalIgnoreCase))
            {
                target = SampleLibrary.Ensure(Paths.SampleLibraryDir);
            }
            else
            {
                if (!Catalog.IsValidPath(rawPath) || !Path.IsPathFullyQualified(rawPath))
                    return J(new { started = false, path = rawPath, error = "Invalid folder path" }, 400);
                target = Catalog.NormalizeDir(rawPath);
            }
            if (IsForbiddenSystemOrAppPath(target))
                return J(new { started = false, path = target, error = "Scanning system or application folders is not allowed" }, 400);
            if (!Directory.Exists(target))
                return J(new { started = false, path = target, error = $"Folder not found: {target}" }, 400);
            Scanner.ScanDirectoryAsync(target);
            return J(new { started = true, path = target });
        }

        api.MapPost("/scan", async (HttpRequest req) => StartScan(await ReadBody(req)));
        api.MapPost("/folders/add", async (HttpRequest req) => StartScan(await ReadBody(req)));
        api.MapGet("/scan/status", () => J(Scanner.GetStatus()));
        api.MapPost("/rescan", () => J(new { started = true, folders = Scanner.RescanAll() }));
        api.MapGet("/folders", () => J(new { folders = Catalog.GetWatchedFolders() }));

        api.MapPost("/folders/remove", async (HttpRequest req) =>
        {
            var path = Str(await ReadBody(req), "path");
            if (!Catalog.IsValidPath(path)) return Error(400, "Invalid folder path");
            Scanner.StopWatching(path);
            int removed = Catalog.RemoveFolder(path);
            return J(new { removed = true, photos_removed = removed, path });
        });

        api.MapGet("/default-folder", () => J(new { path = AppPaths.DefaultPicturesFolder() }));

        api.MapPost("/pick-folder", async () =>
        {
            if (_options.PickFolder is null) return J(new { supported = false, path = (string?)null });
            var picked = await _options.PickFolder();
            if (picked is not null) ApproveExportFolder(picked);
            return J(new { supported = true, path = picked, cancelled = picked is null });
        });

        api.MapPost("/reveal", async (HttpRequest req) =>
        {
            var path = Str(await ReadBody(req), "path");
            // Only files that are in the catalog may be revealed.
            if (!Catalog.IsValidPath(path) || !File.Exists(path) || Catalog.GetPhotoByPath(Path.GetFullPath(path)) is null)
                return Error(400, "Invalid file path");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = false });
            return J(new { revealed = true });
        });

        // ---------------------------------------------------------------- AI (always JSON, always 200)
        api.MapGet("/ai/settings", () => J(Catalog.GetAiSettings().Masked()));

        api.MapPost("/ai/settings", async (HttpRequest req) =>
        {
            var body = await ReadBodyOrNull(req);
            if (body is null) return J(new { success = false, error = "Invalid JSON body" }, 400);
            try
            {
                var saved = Catalog.SaveAiSettings(SettingsFrom(body));
                return J(new { success = true, ai_settings = saved.Masked() });
            }
            catch (ArgumentException ex) { return Error(400, ex.Message); }
        });

        api.MapPost("/ai/test", async (HttpRequest req) =>
        {
            var body = await ReadBodyOrNull(req);
            var settings = body is null || body.Count == 0 ? Catalog.GetAiSettings() : SettingsFrom(body);
            try { return J(new { success = true, message = await Ai.TestConnectionAsync(Catalog.ResolveAiSettings(settings)) }); }
            catch (ArgumentException ex) { return J(new { success = false, error = ex.Message }, 400); }
            catch (AiException ex) { return J(new { success = false, error = ex.Message }); }
        });

        api.MapMethods("/ai/analyze", AnyMethod, async (HttpRequest req) =>
        {
            if (!HttpMethods.IsPost(req.Method)) return J(new { success = false, error = "Method not allowed" });
            var body = await ReadBodyOrNull(req);
            if (body is null) return J(new { success = false, error = "Invalid JSON body" });

            var photo = Catalog.GetPhoto(Int(body, "photo_id") ?? 0);
            if (photo is null) return J(new { success = false, error = "Photo not found in catalog" });

            AiAnalysisResult result;
            try
            {
                result = await Ai.AnalyzePhotoAsync(photo, Bool(body, "fallback_heuristic"));
            }
            catch (AiException ex)
            {
                // Hand the offline result back too, so the UI can offer a one-click fallback.
                var fallback = await Ai.AnalyzePhotoAsync(photo, forceHeuristic: true);
                return J(new { success = false, error = ex.Message, fallback });
            }

            Catalog.UpdatePhotoAi(photo.Id, result.Caption, result.Tags, result.Fixes);
            return J(new { success = true, photo = Catalog.GetPhoto(photo.Id), result });
        });

        api.MapMethods("/ai/prompt-tune", AnyMethod, async (HttpRequest req) =>
        {
            if (!HttpMethods.IsPost(req.Method)) return J(new { success = false, error = "Method not allowed" });
            var body = await ReadBodyOrNull(req);
            if (body is null) return J(new { success = false, error = "Invalid JSON body" });

            var prompt = Str(body, "prompt").Trim();
            if (prompt.Length == 0) return J(new { success = false, error = "Prompt cannot be empty" });

            var photo = Catalog.GetPhoto(Int(body, "photo_id") ?? 0);
            if (photo is null) return J(new { success = false, error = "Photo not found in catalog" });

            // The editor may have unsaved changes. Render the prompt input from this in-memory snapshot.
            if (body["edits"] is JsonObject inputEdits) photo.Edits = inputEdits;
            try
            {
                var settings = Catalog.GetAiSettings();
                if (!Bool(body, "fallback_heuristic") && !string.IsNullOrWhiteSpace(settings.ApiKey) && AiClient.IsImageModel(settings.Model))
                {
                    // Image model (GPT-Image-2.5): the prompt produces a new picture, saved as a copy.
                    var (bytes, model) = await Ai.GenerativeEditAsync(photo, prompt);
                    var created = await Task.Run(() => SaveGeneratedCopy(photo, bytes));
                    return J(new
                    {
                        success = true,
                        photo = created,
                        result = new
                        {
                            explanation = $"Created an AI-edited copy: {created.Filename}. Your original photo is unchanged.",
                            fixes = new Dictionary<string, int>(),
                            effect = "none",
                            model,
                            generated = true,
                        },
                    });
                }

                var result = await Ai.PromptTuneAsync(photo, prompt, Bool(body, "fallback_heuristic"));
                return J(new { success = true, result });
            }
            catch (AiException ex)
            {
                var fallback = await Ai.PromptTuneAsync(photo, prompt, forceHeuristic: true);
                return J(new { success = false, error = ex.Message, fallback });
            }
        });

        api.MapMethods("/ai/batch-analyze", AnyMethod, async (HttpRequest req) =>
        {
            if (!HttpMethods.IsPost(req.Method)) return J(new { success = false, error = "Method not allowed" });
            var body = await ReadBodyOrNull(req) ?? new JsonObject();

            var ids = new List<long>();
            if (body["photo_ids"] is JsonArray arr)
                ids.AddRange(arr.Select(n => EditRecipe.TryNum(n, out var v) ? (long)v : 0).Where(v => v > 0));
            else if (Bool(body, "all"))
                ids = Catalog.GetAllIds(onlyUntagged: Bool(body, "only_untagged"));

            if (Interlocked.CompareExchange(ref _batch.Running, 1, 0) != 0)
                return J(new { started = false, success = false, error = "A batch analysis is already running.", count = 0 });

            bool fallbackOnly = Bool(body, "fallback_heuristic");
            _batch.Total = ids.Count; _batch.Done = 0; _batch.Failed = 0; _batch.Fallbacks = 0; _batch.LastError = "";
            _ = Task.Run(() => RunBatchAsync(ids, fallbackOnly));
            return J(new { started = true, count = ids.Count });
        });

        api.MapGet("/ai/batch-status", () => J(new
        {
            running = Volatile.Read(ref _batch.Running) == 1,
            total = _batch.Total,
            done = _batch.Done,
            failed = _batch.Failed,
            fallbacks = _batch.Fallbacks,
            last_error = _batch.LastError,
        }));

        // ---------------------------------------------------------------- lifecycle
        api.MapGet("/app-info", () => J(new
        {
            name = "svacitra",
            version = typeof(PicasaServer).Assembly.GetName().Version?.ToString(3) ?? "2.0.0",
            data_dir = Paths.DataDir,
            native_folder_picker = _options.PickFolder is not null,
            engines = new { opencv = PhotoEngine.OpenCvVersion, imagemagick = PhotoEngine.MagickVersion },
        }));

        api.MapPost("/shutdown", () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(350); // let the reply reach the page first
                if (_options.OnShutdownRequested is not null) _options.OnShutdownRequested();
                else await StopAsync();
            });
            return J(new { status = "shutting_down", message = "svacitra is closing..." });
        });

        api.MapFallback(() => Error(404, "Unknown API route"));

        // ---------------------------------------------------------------- frontend
        // Explicit catch-all: the default fallback pattern skips paths that look like files (css/js).
        app.MapFallback("{*path}", async ctx =>
        {
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) && await frontend.TryServeAsync(ctx)) return;
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "Not found" });
        });
    }

    // ====================================================================== helpers

    private static readonly string[] AnyMethod = { "GET", "POST", "PUT", "DELETE", "PATCH" };

    private async Task RunBatchAsync(List<long> ids, bool fallbackOnly)
    {
        try
        {
            foreach (var id in ids)
            {
                if (Volatile.Read(ref _stopped) == 1) break;
                var photo = Catalog.GetPhoto(id);
                if (photo is null) { Interlocked.Increment(ref _batch.Failed); Interlocked.Increment(ref _batch.Done); continue; }
                try
                {
                    AiAnalysisResult result;
                    try
                    {
                        result = await Ai.AnalyzePhotoAsync(photo, fallbackOnly);
                    }
                    catch (AiException ex) when (!fallbackOnly)
                    {
                        // Cloud failed: fall back to the offline engine so the photo still gets tagged.
                        _batch.LastError = ex.Message;
                        Interlocked.Increment(ref _batch.Fallbacks);
                        result = await Ai.AnalyzePhotoAsync(photo, forceHeuristic: true);
                    }
                    Catalog.UpdatePhotoAi(photo.Id, result.Caption, result.Tags, result.Fixes);
                }
                catch (Exception ex)
                {
                    _batch.LastError = ex.Message;
                    Interlocked.Increment(ref _batch.Failed);
                }
                Interlocked.Increment(ref _batch.Done);
                await Task.Delay(200); // gentle rate limit
            }
        }
        finally
        {
            Volatile.Write(ref _batch.Running, 0);
        }
    }

    /// <summary>
    /// Writes an AI-generated image as "name_luna.jpg" next to the original (never overwriting anything),
    /// or into the app's own "ai_edits" folder when that location is read-only, and adds it to the catalog.
    /// </summary>
    private Photo SaveGeneratedCopy(Photo source, byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null)
                throw new AiException("The AI image model returned invalid image data.");
            ImageOps.ValidateDimensions(codec.Info.Width, codec.Info.Height);
        }
        catch (InvalidDataException ex)
        {
            throw new AiException("The AI image model returned an image with unsafe dimensions.", ex);
        }

        var stem = Path.GetFileNameWithoutExtension(source.FilePath);
        bytes = ExifInfo.CopyExif(source.FilePath, bytes); // keep date taken, camera and GPS of the original
        foreach (var dir in new[] { Path.GetDirectoryName(source.FilePath)!, Path.Combine(Paths.DataDir, "ai_edits") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, $"{stem}_luna.jpg");
                for (int n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{stem}_luna_{n}.jpg");
                using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write)) fs.Write(bytes);
                return Scanner.IndexNow(target) ?? Catalog.GetPhotoByPath(Path.GetFullPath(target))
                       ?? throw new AiException("The AI image was saved but could not be added to the library: " + target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next location
            }
        }
        throw new AiException("The AI image could not be saved: no writable folder.");
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB"
        : bytes + " bytes";

    private async Task RefreshThumbAsync(long id)
    {
        var photo = Catalog.GetPhoto(id);
        if (photo is null) return;
        try { await Task.Run(() => Images.RefreshEditedThumbnail(photo)); }
        catch (Exception) { /* the thumbnail is cosmetic; the edit itself is already saved */ }
    }

    private bool TokenMatches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var expected = Encoding.UTF8.GetBytes(SessionToken);
        var actual = Encoding.UTF8.GetBytes(candidate);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string DefaultExportFolder()
    {
        var pictures = Path.Combine(AppPaths.DefaultPicturesFolder(), "svacitra Exports");
        try
        {
            Directory.CreateDirectory(pictures);
            return Catalog.NormalizeDir(pictures);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var fallback = Path.Combine(Paths.DataDir, "Exports");
            Directory.CreateDirectory(fallback);
            return Catalog.NormalizeDir(fallback);
        }
    }

    private bool IsApprovedExportFolder(string fullDir)
    {
        if (_approvedExportFolders.ContainsKey(fullDir)) return true;
        var defaultPictures = Catalog.NormalizeDir(Path.Combine(AppPaths.DefaultPicturesFolder(), "svacitra Exports"));
        var defaultFallback = Catalog.NormalizeDir(Path.Combine(Paths.DataDir, "Exports"));
        return fullDir.Equals(defaultPictures, StringComparison.OrdinalIgnoreCase) ||
               fullDir.Equals(defaultFallback, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsForbiddenSystemOrAppPath(string fullPath)
    {
        static bool IsUnder(string candidate, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            var norm = Catalog.NormalizeDir(root);
            return candidate.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(norm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        if (IsUnder(fullPath, Paths.DataDir) && !IsUnder(fullPath, Paths.SampleLibraryDir))
            return true;

        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };
        return forbidden.Any(dir => IsUnder(fullPath, dir));
    }

    private static AiSettings SettingsFrom(JsonObject body) => new AiSettings
    {
        ApiKey = Str(body, "api_key"),
        Endpoint = Str(body, "endpoint"),
        Model = Str(body, "model"),
    }.Normalized();

    private static IResult J(object value, int status = 200) => Results.Json(value, Json.Options, statusCode: status);
    private static IResult Error(int status, string message) => Results.Json(new { error = message, success = false }, Json.Options, statusCode: status);

    private static async Task<JsonObject> ReadBody(HttpRequest req) => await ReadBodyOrNull(req) ?? new JsonObject();

    /// <summary>Parses the JSON body; an empty body is an empty object, malformed JSON is null.</summary>
    private static async Task<JsonObject?> ReadBodyOrNull(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";

    private static long? Int(JsonObject o, string key) => EditRecipe.TryNum(o[key], out var v) ? (long)v : null;

    private static bool Bool(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        return EditRecipe.TryNum(v, out var n) && n != 0;
    }

    private static bool Truthy(string? s) => s is "1" or "true" or "True" or "yes";
    private static int ParseInt(string? s) => int.TryParse(s, out var v) ? v : 0;
}
