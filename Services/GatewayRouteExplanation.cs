using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public readonly record struct GatewayRouteExplanationSnapshot(
    string Line,
    string RequestId,
    string UpdatedUtc);

public static class GatewayRouteExplanation
{
    public static string FormatLine(
        string routeKind,
        string? overlayVariant,
        bool compactApplied,
        bool reloadOffered,
        bool cloudConsent,
        string requestId)
    {
        var route = NormalizeRoute(routeKind, cloudConsent);
        var overlay = string.IsNullOrWhiteSpace(overlayVariant) ? "none" : overlayVariant.Trim();
        var compact = compactApplied ? "yes" : "no";
        var reload = reloadOffered ? "offered" : "none";
        var id = string.IsNullOrWhiteSpace(requestId) ? "—" : requestId.Trim();
        return $"{route} \u00b7 overlay {overlay} \u00b7 compact {compact} \u00b7 reload {reload} \u00b7 req {id}";
    }

    public static JsonObject ToJson(
        string routeKind,
        string? overlayVariant,
        bool compactApplied,
        bool reloadOffered,
        bool cloudConsent,
        string requestId,
        string provider,
        string model)
    {
        return new JsonObject
        {
            ["line"] = FormatLine(routeKind, overlayVariant, compactApplied, reloadOffered, cloudConsent, requestId),
            ["routeKind"] = NormalizeRoute(routeKind, cloudConsent),
            ["overlayVariant"] = overlayVariant ?? string.Empty,
            ["compactApplied"] = compactApplied,
            ["reloadOffered"] = reloadOffered,
            ["cloudConsent"] = cloudConsent,
            ["requestId"] = requestId ?? string.Empty,
            ["provider"] = provider ?? string.Empty,
            ["model"] = model ?? string.Empty,
            ["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };
    }

    public static GatewayRouteExplanationSnapshot? Parse(JsonObject? root)
    {
        if (root is null)
        {
            return null;
        }

        var line = root["line"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return new GatewayRouteExplanationSnapshot(
            line,
            root["requestId"]?.ToString() ?? string.Empty,
            root["updatedUtc"]?.ToString() ?? string.Empty);
    }

    private static string NormalizeRoute(string routeKind, bool cloudConsent)
    {
        if (cloudConsent)
        {
            return "cloud-consent";
        }

        var text = (routeKind ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "cloud" => "cloud",
            "local" => "local",
            _ => string.IsNullOrWhiteSpace(text) ? "unknown" : text
        };
    }
}
