using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace PicasaRevive.Server;

/// <summary>
/// Serves the HTML/CSS/JS frontend that is embedded in this assembly. If a "wwwroot" folder sits next to
/// the exe it wins, which makes frontend hacking possible without rebuilding.
/// </summary>
internal sealed class EmbeddedFrontend
{
    private readonly Assembly _assembly = typeof(EmbeddedFrontend).Assembly;
    private readonly Dictionary<string, string> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _diskRoot;

    public EmbeddedFrontend(string? diskRootOverride)
    {
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (normalized.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
                _resources["/" + normalized["wwwroot/".Length..]] = name;
        }
        string? disk = diskRootOverride;
#if DEBUG
        disk ??= Path.Combine(AppContext.BaseDirectory, "wwwroot");
#endif
        _diskRoot = disk is not null && File.Exists(Path.Combine(disk, "index.html")) ? Path.GetFullPath(disk) : null;
    }

    public bool Has(string path) => _resources.ContainsKey(path);

    public async Task<bool> TryServeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path == "/" || path.Length == 0) path = "/index.html";
        if (path.Contains("..", StringComparison.Ordinal)) return false;

        Stream? stream = null;
        if (_diskRoot is not null)
        {
            var file = Path.GetFullPath(Path.Combine(_diskRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (file.StartsWith(_diskRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        if (stream is null && _resources.TryGetValue(path, out var resource))
            stream = _assembly.GetManifestResourceStream(resource);
        if (stream is null) return false;

        await using (stream)
        {
            ctx.Response.ContentType = ContentType(path);
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; img-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            ctx.Response.ContentLength = stream.Length;
            if (!HttpMethods.IsHead(ctx.Request.Method))
                await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        return true;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
