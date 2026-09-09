using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace FluxMux.Avalonia.Services;

public static class LocalChatImageNormalizer
{
    private const int MaxLocalImageBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = FetchTimeout
    };

    private static readonly Regex MarkdownImageRegex = new(
        @"!\[[^\]]*\]\(\s*<?(https?://[^)\s>]+)>?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlImageRegex = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""'](https?://[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static int Normalize(JsonObject payload, HttpClient? http = null)
        => NormalizeAsync(payload, http).GetAwaiter().GetResult();

    public static async Task<int> NormalizeAsync(
        JsonObject payload,
        HttpClient? http = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null)
        {
            return 0;
        }

        var client = http ?? DefaultHttp;
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var converted = 0;

        if (payload["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                PromoteInlineImageUrls(message);
                converted += await NormalizeMessageAsync(message, client, cache, log, cancellationToken).ConfigureAwait(false);
            }
        }

        if (payload["images"] is JsonArray images)
        {
            converted += await NormalizeImageListAsync(images, client, cache, log, cancellationToken).ConfigureAwait(false);
        }

        return converted;
    }

    private static async Task<int> NormalizeMessageAsync(
        JsonObject message,
        HttpClient http,
        Dictionary<string, string> cache,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var content = message["content"];
        if (content is JsonArray parts)
        {
            return await NormalizeImageListAsync(parts, http, cache, log, cancellationToken).ConfigureAwait(false);
        }

        if (content is JsonObject part && LooksLikeImagePart(part))
        {
            var normalized = await NormalizeImagePartAsync(part, http, cache, log, cancellationToken).ConfigureAwait(false);
            message["content"] = new JsonArray { normalized.Part };
            return normalized.Embedded ? 1 : 0;
        }

        return 0;
    }

    private static async Task<int> NormalizeImageListAsync(
        JsonArray parts,
        HttpClient http,
        Dictionary<string, string> cache,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var converted = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not JsonObject part || !LooksLikeImagePart(part))
            {
                continue;
            }

            var normalized = await NormalizeImagePartAsync(part, http, cache, log, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(normalized.Part, part))
            {
                parts[i] = normalized.Part;
            }

            if (normalized.Embedded)
            {
                converted++;
            }
        }

        return converted;
    }

    private static void PromoteInlineImageUrls(JsonObject message)
    {
        var content = message["content"];
        if (content is JsonValue textValue)
        {
            var text = textValue.ToString() ?? string.Empty;
            var urls = ExtractInlineImageUrls(text);
            if (urls.Count == 0)
            {
                return;
            }

            var parts = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            };
            foreach (var url in urls)
            {
                parts.Add(CreateImageUrlPart(url));
            }

            message["content"] = parts;
            return;
        }

        if (content is not JsonArray partsArray)
        {
            return;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in partsArray.OfType<JsonObject>())
        {
            var url = ResolveImageUrl(part);
            if (!string.IsNullOrWhiteSpace(url))
            {
                existing.Add(url);
            }
        }

        var extras = new List<string>();
        foreach (var part in partsArray.OfType<JsonObject>())
        {
            var text = part["text"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var url in ExtractInlineImageUrls(text))
            {
                if (existing.Add(url))
                {
                    extras.Add(url);
                }
            }
        }

        foreach (var url in extras)
        {
            partsArray.Add(CreateImageUrlPart(url));
        }
    }

    private static List<string> ExtractInlineImageUrls(string text)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MarkdownImageRegex.Matches(text))
        {
            AddInlineUrl(urls, seen, match.Groups[1].Value);
        }

        foreach (Match match in HtmlImageRegex.Matches(text))
        {
            AddInlineUrl(urls, seen, match.Groups[1].Value);
        }

        return urls;
    }

    private static void AddInlineUrl(List<string> urls, HashSet<string> seen, string raw)
    {
        var url = WebUtility.HtmlDecode((raw ?? string.Empty).Trim().TrimEnd('"', '\'', ',', '.'));
        if (string.IsNullOrWhiteSpace(url)
            || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || !seen.Add(url))
        {
            return;
        }

        urls.Add(url);
    }

    private static bool LooksLikeImagePart(JsonObject part)
    {
        var type = (part["type"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
        return type.Contains("image", StringComparison.Ordinal)
            || part["image_url"] is not null
            || part["image"] is not null
            || part["input_image"] is not null
            || part["source"] is JsonObject;
    }

    private static JsonObject CreateImageUrlPart(string url)
        => new()
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject
            {
                ["url"] = url
            }
        };

    private static async Task<(JsonObject Part, bool Embedded)> NormalizeImagePartAsync(
        JsonObject part,
        HttpClient http,
        Dictionary<string, string> cache,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var url = ResolveImageUrl(part);
        if (string.IsNullOrWhiteSpace(url))
        {
            return (part, false);
        }

        var dataUri = await ToDataUriAsync(url, http, cache, log, cancellationToken).ConfigureAwait(false);
        var embedded = dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || dataUri.StartsWith("data:application/octet-stream;base64,", StringComparison.OrdinalIgnoreCase);
        return (CreateImageUrlPart(dataUri), embedded);
    }

    private static string ResolveImageUrl(JsonObject part)
    {
        if (part["image_url"] is JsonValue imageUrlValue)
        {
            return imageUrlValue.ToString() ?? string.Empty;
        }

        if (part["image_url"] is JsonObject imageUrlObject)
        {
            return FirstNonEmpty(
                imageUrlObject["url"]?.ToString(),
                imageUrlObject["uri"]?.ToString());
        }

        if (part["input_image"] is JsonValue inputImageValue)
        {
            return inputImageValue.ToString() ?? string.Empty;
        }

        if (part["input_image"] is JsonObject inputImageObject)
        {
            return FirstNonEmpty(
                inputImageObject["url"]?.ToString(),
                inputImageObject["data"] is JsonValue data
                    ? ToDataUriFromRaw(data.ToString(), inputImageObject["media_type"]?.ToString() ?? inputImageObject["format"]?.ToString())
                    : null);
        }

        if (part["image"] is JsonValue imageValue)
        {
            return imageValue.ToString() ?? string.Empty;
        }

        if (part["source"] is JsonObject source)
        {
            var data = source["data"]?.ToString();
            if (!string.IsNullOrWhiteSpace(data))
            {
                return ToDataUriFromRaw(data, source["media_type"]?.ToString());
            }

            return FirstNonEmpty(source["url"]?.ToString(), source["path"]?.ToString());
        }

        return FirstNonEmpty(part["url"]?.ToString(), part["path"]?.ToString());
    }

    private static async Task<string> ToDataUriAsync(
        string url,
        HttpClient http,
        Dictionary<string, string> cache,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var trimmed = (url ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        if (cache.TryGetValue(trimmed, out var cached))
        {
            return cached;
        }

        string result;
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            result = NormalizeExistingDataUri(trimmed);
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            result = await FetchRemoteToDataUriAsync(trimmed, http, log, cancellationToken).ConfigureAwait(false)
                     ?? trimmed;
        }
        else
        {
            result = ReadLocalFileToDataUri(trimmed);
        }

        cache[trimmed] = result;
        return result;
    }

    private static async Task<string?> FetchRemoteToDataUriAsync(
        string url,
        HttpClient http,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FetchTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "image/jpeg,image/png,image/webp,image/gif,image/bmp,image/*;q=0.8,*/*;q=0.5");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke("local chat: could not fetch image " + url + " (HTTP " + (int)response.StatusCode + ")");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxLocalImageBytes)
            {
                log?.Invoke("local chat: skipped oversized image " + url);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxLocalImageBytes)
            {
                log?.Invoke("local chat: skipped empty or oversized image " + url);
                return null;
            }

            var hinted = response.Content.Headers.ContentType?.MediaType;
            var dataUri = ToLlamaDataUri(bytes, hinted);
            log?.Invoke("local chat: embedded image from " + url + " as " + DescribeDataUri(dataUri) + " (" + bytes.Length + " bytes)");
            return dataUri;
        }
        catch (Exception ex)
        {
            log?.Invoke("local chat: could not fetch image " + url + " (" + ex.GetType().Name + ")");
            return null;
        }
    }

    private static string ReadLocalFileToDataUri(string url)
    {
        var path = TryMapLocalPath(url);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return url;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxLocalImageBytes)
            {
                return url;
            }

            var bytes = File.ReadAllBytes(path);
            return ToLlamaDataUri(bytes, DetectMime(bytes, path));
        }
        catch
        {
            return url;
        }
    }

    private static string NormalizeExistingDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        if (comma < 0 || comma >= dataUri.Length - 1)
        {
            return dataUri;
        }

        var header = dataUri[..comma];
        var payload = dataUri[(comma + 1)..].Replace("\r", string.Empty).Replace("\n", string.Empty).Replace(" ", string.Empty);
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeExistingDataUri(payload);
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            var hinted = TryMimeFromDataHeader(header);
            return ToLlamaDataUri(bytes, hinted);
        }
        catch
        {
            return header + "," + payload;
        }
    }

    private static string ToDataUriFromRaw(string? data, string? mediaType)
    {
        var payload = (data ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeExistingDataUri(payload);
        }

        var mime = string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType.Trim();
        if (!mime.Contains('/', StringComparison.Ordinal))
        {
            mime = "image/" + mime.TrimStart('.');
        }

        return NormalizeExistingDataUri("data:" + mime + ";base64," + payload.Replace("\r", string.Empty).Replace("\n", string.Empty));
    }

    private static string ToLlamaDataUri(byte[] bytes, string? hintedMime)
    {
        if (TryMakeLlamaFriendly(bytes, hintedMime, out var output, out var mime))
        {
            return "data:" + mime + ";base64," + Convert.ToBase64String(output);
        }

        var fallback = string.IsNullOrWhiteSpace(hintedMime) ? DetectMime(bytes, null) : hintedMime.Trim();
        if (string.IsNullOrWhiteSpace(fallback))
        {
            fallback = "image/png";
        }

        return "data:" + fallback + ";base64," + Convert.ToBase64String(bytes);
    }

    private static bool TryMakeLlamaFriendly(byte[] bytes, string? hintedMime, out byte[] output, out string mime)
    {
        mime = DetectMime(bytes, null);
        if (string.IsNullOrWhiteSpace(mime) && !string.IsNullOrWhiteSpace(hintedMime))
        {
            mime = hintedMime.Trim();
        }

        if (mime is "image/jpeg" or "image/png" or "image/gif" or "image/bmp")
        {
            output = bytes;
            return true;
        }

        if (TryEncodePng(bytes, out output))
        {
            mime = "image/png";
            return true;
        }

        output = bytes;
        return false;
    }

    private static bool TryEncodePng(byte[] bytes, out byte[] png)
    {
        png = [];
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
            {
                return false;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            if (encoded is null)
            {
                return false;
            }

            png = encoded.ToArray();
            return png.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryMapLocalPath(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            text = Uri.UnescapeDataString(text["file:".Length..].TrimStart('/'));
            if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
            {
                return text.Replace('/', '\\');
            }

            if (text.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase))
            {
                text = text["localhost/".Length..];
            }
        }

        if (text.StartsWith("vscode-file://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("vscode-resource:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = text.IndexOf(":/", StringComparison.Ordinal);
            if (marker >= 0)
            {
                text = Uri.UnescapeDataString(text[(marker + 1)..].TrimStart('/'));
            }
        }

        try
        {
            if (Path.IsPathRooted(text) && File.Exists(text))
            {
                return Path.GetFullPath(text);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string DetectMime(byte[] bytes, string? path)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            return "image/gif";
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return "image/bmp";
        }

        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        var ext = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".png" => "image/png",
            _ => string.Empty
        };
    }

    private static string? TryMimeFromDataHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mime = header["data:".Length..];
        var slash = mime.IndexOf(';');
        if (slash >= 0)
        {
            mime = mime[..slash];
        }

        mime = mime.Trim();
        return string.IsNullOrWhiteSpace(mime) ? null : mime;
    }

    private static string DescribeDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        var header = comma < 0 ? dataUri : dataUri[..comma];
        return header.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? header["data:".Length..].Split(';')[0]
            : "image";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}
