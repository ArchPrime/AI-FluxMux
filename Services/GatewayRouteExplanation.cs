using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public readonly record struct GatewayRouteExplanationSnapshot(
    string Line,
    string RequestId,
    string UpdatedUtc,
    string RouteKind = "",
    string OverlayVariant = "",
    bool CompactApplied = false,
    bool ReloadOffered = false,
    bool CloudConsent = false,
    string Model = "");

public static class GatewayRouteExplanation
{
    public const string CloudSummaryPrefix = "Cloud \u00b7 ";

    public static string FormatStatusDetails(
        bool modelLoaded,
        string? attributeSummary,
        bool hasMatchingLastTurn,
        string? differingOverlayVariant,
        bool compactApplied,
        bool reloadOffered,
        bool cloudConsent)
    {
        if (!modelLoaded)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var summary = StripRouteNamePrefix(attributeSummary);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            parts.Add(summary);
        }

        if (hasMatchingLastTurn)
        {
            if (!string.IsNullOrWhiteSpace(differingOverlayVariant))
            {
                parts.Add("this turn used model profile " + differingOverlayVariant.Trim());
            }

            parts.Add(compactApplied ? "Compact yes" : "Compact no");
            if (reloadOffered)
            {
                parts.Add("Load suggested offered");
            }

            if (cloudConsent)
            {
                parts.Add("Offered cloud");
            }
        }

        return string.Join(" \u00b7 ", parts);
    }

    public static string StripRouteNamePrefix(string? summary)
    {
        var text = (summary ?? string.Empty).Trim();
        if (text.StartsWith(CloudSummaryPrefix, StringComparison.Ordinal))
        {
            return text[CloudSummaryPrefix.Length..].Trim();
        }

        return text;
    }

    public static string FormatLine(
        string routeKind,
        string? overlayVariant,
        bool compactApplied,
        bool reloadOffered,
        bool cloudConsent,
        string requestId)
    {
        _ = routeKind;
        _ = overlayVariant;
        _ = requestId;
        return FormatStatusDetails(
            modelLoaded: true,
            attributeSummary: null,
            hasMatchingLastTurn: true,
            differingOverlayVariant: null,
            compactApplied: compactApplied,
            reloadOffered: reloadOffered,
            cloudConsent: cloudConsent);
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
            ["routeKind"] = FormatRoute(routeKind, cloudConsent),
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

        var routeKind = root["routeKind"]?.ToString() ?? string.Empty;
        var overlayVariant = root["overlayVariant"]?.ToString() ?? string.Empty;
        var compactApplied = root["compactApplied"]?.GetValue<bool>() ?? false;
        var reloadOffered = root["reloadOffered"]?.GetValue<bool>() ?? false;
        var cloudConsent = root["cloudConsent"]?.GetValue<bool>() ?? false;
        var requestId = root["requestId"]?.ToString() ?? string.Empty;
        var model = root["model"]?.ToString() ?? string.Empty;
        var line = FormatLine(routeKind, overlayVariant, compactApplied, reloadOffered, cloudConsent, requestId);
        if (string.IsNullOrWhiteSpace(line) && string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(routeKind))
        {
            return null;
        }

        return new GatewayRouteExplanationSnapshot(
            line,
            requestId,
            root["updatedUtc"]?.ToString() ?? string.Empty,
            routeKind,
            overlayVariant,
            compactApplied,
            reloadOffered,
            cloudConsent,
            model);
    }

    private static string FormatRoute(string routeKind, bool cloudConsent)
    {
        if (cloudConsent)
        {
            return "Offered cloud";
        }

        var raw = (routeKind ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unknown";
        }

        return raw.ToLowerInvariant() switch
        {
            "cloud" => "Cloud",
            "local" => "Local",
            "cloud-consent" or "offered cloud" => "Offered cloud",
            _ => raw
        };
    }
}
