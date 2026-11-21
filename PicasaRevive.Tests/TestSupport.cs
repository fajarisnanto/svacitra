using System.Net;
using System.Text;
using PicasaRevive.Core;
using SkiaSharp;

namespace PicasaRevive.Tests;

/// <summary>A throwaway data directory that is deleted when the test finishes.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PicasaReviveTests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
    }
}

public static class TestImages
{
    /// <summary>Writes a solid-colour JPEG and returns its path.</summary>
    public static string WriteJpeg(string folder, string name, int width, int height, SKColor color)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(color);
        File.WriteAllBytes(path, ImageOps.EncodeJpeg(bmp, 92));
        return path;
    }

    public static SKColor CenterPixel(byte[] jpeg)
    {
        using var bmp = SKBitmap.Decode(jpeg);
        return bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
    }
}

/// <summary>Fake cloud AI endpoint: records the last request and answers with a canned response.</summary>
public sealed class StubAiHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "application/json";
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }
    public string? LastRequestBody { get; private set; }
    public string? LastUrl { get; private set; }
    public string? LastAuthorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastUrl = request.RequestUri?.ToString();
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (Throw is not null) throw Throw;
        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, ContentType) };
    }

    /// <summary>An OpenAI-style chat completion whose message content is <paramref name="content"/>.</summary>
    public static string Completion(string content) =>
        System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });
}
