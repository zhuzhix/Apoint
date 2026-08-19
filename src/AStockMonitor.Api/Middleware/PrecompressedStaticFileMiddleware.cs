using System.Globalization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace AStockMonitor.Api.Middleware;

/// <summary>
/// Serves Vite's pre-compressed hash assets without recompressing them on every request.
/// Only /assets is eligible; HTML keeps the normal no-cache/static-file path.
/// </summary>
public sealed class PrecompressedStaticFileMiddleware(
    RequestDelegate next,
    IWebHostEnvironment environment)
{
    private readonly IFileProvider _files = environment.WebRootFileProvider;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) ||
            !context.Request.Path.StartsWithSegments("/assets") ||
            context.Request.Headers.ContainsKey(HeaderNames.Range))
        {
            await next(context);
            return;
        }

        var relativePath = context.Request.Path.Value?.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var encoding = Accepts(context, "br") ? (Name: "br", Extension: ".br")
            : Accepts(context, "gzip") ? (Name: "gzip", Extension: ".gz")
            : ((string Name, string Extension)?)null;
        if (encoding is null)
        {
            await next(context);
            return;
        }

        var compressed = _files.GetFileInfo(relativePath + encoding.Value.Extension);
        if (!compressed.Exists)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType(relativePath);
        context.Response.ContentLength = compressed.Length;
        context.Response.Headers.ContentEncoding = encoding.Value.Name;
        context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        context.Response.Headers.AppendCommaSeparatedValues(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        if (HttpMethods.IsHead(context.Request.Method))
            return;

        await using var stream = compressed.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool Accepts(HttpContext context, string expected)
    {
        foreach (var item in context.Request.Headers.AcceptEncoding.ToString()
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!parts[0].Equals(expected, StringComparison.OrdinalIgnoreCase) && parts[0] != "*")
                continue;
            var quality = 1m;
            foreach (var parameter in parts.Skip(1))
            {
                if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                    decimal.TryParse(parameter.AsSpan(2), NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var parsed))
                    quality = parsed;
            }
            return quality > 0m;
        }
        return false;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".woff2" => "font/woff2",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
