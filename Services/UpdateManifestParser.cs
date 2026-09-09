using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public sealed class UpdateManifest
{
    public required string Version { get; init; }
    public string Notes { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string HelpUrl { get; init; } = string.Empty;
    public string HelpSha256 { get; init; } = string.Empty;
}

public static class UpdateManifestParser
{
    public static bool TryParse(string json, Uri feedUri, out UpdateManifest? manifest, out string error)
    {
        manifest = null;
        error = string.Empty;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            error = "The update feed is not valid JSON: " + ex.Message;
            return false;
        }

        if (root is null)
        {
            error = "The update feed is not a JSON object.";
            return false;
        }

        var protocol = root["protocol"]?.ToString()?.Trim() ?? string.Empty;
        if (!protocol.Equals("FluxMux", StringComparison.Ordinal))
        {
            error = "The update feed is not an AI-FluxMux feed (protocol must be FluxMux).";
            return false;
        }

        var version = root["version"]?.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version))
        {
            error = "The update feed does not name a version.";
            return false;
        }

        manifest = new UpdateManifest
        {
            Version = version,
            Notes = root["notes"]?.ToString()?.Trim() ?? string.Empty,
            DownloadUrl = ResolveUrl(feedUri, root["downloadUrl"]?.ToString()),
            HelpUrl = ResolveUrl(feedUri, root["helpUrl"]?.ToString()),
            HelpSha256 = (root["helpSha256"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant()
        };
        return true;
    }

    private static string ResolveUrl(Uri feedUri, string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
        {
            return absolute.AbsoluteUri;
        }

        if (Uri.TryCreate(feedUri, text, out var relative)
            && (relative.Scheme == Uri.UriSchemeHttps || relative.Scheme == Uri.UriSchemeHttp))
        {
            return relative.AbsoluteUri;
        }

        return string.Empty;
    }
}
