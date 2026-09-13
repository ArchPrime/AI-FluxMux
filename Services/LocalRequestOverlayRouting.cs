using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

public static class LocalRequestOverlayRouting
{
    public static JsonObject? Pick(JsonObject state, JsonObject payload, string requestedModel)
    {
        if (state["local_overlays"] is not JsonArray overlays)
        {
            return null;
        }

        var usable = overlays.OfType<JsonObject>().ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        var requested = (requestedModel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = Str(payload, "model");
        }

        var named = MatchByRequestedName(usable, requested);
        if (named is not null)
        {
            return named;
        }

        if (usable.Count == 1)
        {
            return usable[0];
        }

        if (LocalChatPayloadSignals.PayloadHasToolCalls(payload))
        {
            return usable.MinBy(item => ParseDouble(Str(item, "temperature"), 0.3));
        }

        var user = LocalChatPayloadSignals.LastUserText(payload).ToLowerInvariant();
        if (LocalChatPayloadSignals.PayloadLooksLikePatchOrPlan(payload)
            || user.Contains("refactor") || user.Contains("compile") || user.Contains("function")
            || user.Contains("class ") || user.Contains("stack trace") || user.Contains("bugfix") || user.Contains("unit test"))
        {
            return usable.MinBy(item => ParseDouble(Str(item, "temperature"), 0.3));
        }

        if (user.Length >= 4000)
        {
            return usable.MaxBy(item => ParseInt(Str(item, "max_tokens"), 0));
        }

        return usable[^1];
    }

    public static void Apply(JsonObject payload, JsonObject? overlay)
        => Apply(payload, overlay, PortForwardingRules.Defaults, clientMaxTokens: 0);

    public static void Apply(
        JsonObject payload,
        JsonObject? overlay,
        PortForwardingRules rules,
        int clientMaxTokens)
    {
        if (overlay is null)
        {
            return;
        }

        if (overlay["temperature"] is not null
            && double.TryParse(overlay["temperature"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            payload["temperature"] = temperature;
        }

        var overlayMax = ParseInt(Str(overlay, "max_tokens"), 0);
        var chosen = ChooseMaxTokens(clientMaxTokens, overlayMax, rules?.ClientMaxTokensMode);
        if (chosen > 0)
        {
            payload["max_tokens"] = chosen;
        }

        // Thinking depth follows the live Quick Select slot (local_reasoning),
        // not whichever overlay Pick chose for temperature / max tokens.
    }

    public static int ChooseMaxTokens(int clientMax, int overlayMax, string? mode)
    {
        var normalized = PortForwardingRules.NormalizeClientMaxTokensMode(mode);
        if (normalized == PortForwardingRules.ClientMaxTokensClient)
        {
            return clientMax > 0 ? clientMax : overlayMax;
        }

        if (normalized == PortForwardingRules.ClientMaxTokensSmaller)
        {
            if (clientMax > 0 && overlayMax > 0)
            {
                return Math.Min(clientMax, overlayMax);
            }

            return clientMax > 0 ? clientMax : overlayMax;
        }

        return overlayMax > 0 ? overlayMax : clientMax;
    }

    /// <summary>
    /// Compact and clamp use 4 chars/token. Filling/fail-early uses
    /// <see cref="FillingCharsPerToken"/> so code-heavy Endpoint packs are not
    /// underestimated into a multi-minute llama-server prompt eval.
    /// </summary>
    public const int FillingCharsPerToken = 2;
    public const int ImagePromptTokens = 2048;

    private static readonly Regex EmbeddedImagePayloadRegex = new(
        @"data:(?:image\\*/[A-Za-z0-9.+\-]+|application\\*/octet-stream);base64,[A-Za-z0-9+/=\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LongBase64BlobRegex = new(
        @"[A-Za-z0-9+/]{256,}={0,2}",
        RegexOptions.Compiled);

    public static string RedactImageBytesForEstimate(string blob, out int imageCount, bool payloadHasImage = false)
    {
        var found = 0;
        var text = blob ?? string.Empty;
        var redacted = EmbeddedImagePayloadRegex.Replace(text, _ =>
        {
            found++;
            return "data:image/png;base64,AA==";
        });
        if (payloadHasImage)
        {
            redacted = LongBase64BlobRegex.Replace(redacted, _ =>
            {
                found++;
                return "AA==";
            });
        }

        imageCount = found;
        return redacted;
    }

    public static int EstimatePromptTokens(JsonObject payload, int charsPerToken = 4)
    {
        var textChars = 0;
        var imageCount = 0;
        CollectTextAndImages(payload["messages"] ?? payload["prompt"], ref textChars, ref imageCount);
        if (imageCount == 0 && LocalChatPayloadSignals.PayloadHasImage(payload))
        {
            imageCount = 1;
        }

        var per = Math.Max(1, charsPerToken);
        return Math.Max(0, textChars / per) + (imageCount * ImagePromptTokens);
    }

    private static void CollectTextAndImages(JsonNode? node, ref int textChars, ref int imageCount)
    {
        if (node is null)
        {
            return;
        }

        if (node is JsonValue value)
        {
            var text = value.ToString() ?? string.Empty;
            if (LooksLikeImagePayload(text))
            {
                imageCount++;
                return;
            }

            textChars += text.Length;
            return;
        }

        if (node is JsonObject obj)
        {
            if (LooksLikeImageObject(obj))
            {
                imageCount++;
                return;
            }

            foreach (var property in obj)
            {
                if (IsImageKey(property.Key))
                {
                    imageCount++;
                    continue;
                }

                CollectTextAndImages(property.Value, ref textChars, ref imageCount);
            }

            return;
        }

        if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                CollectTextAndImages(item, ref textChars, ref imageCount);
            }
        }
    }

    private static bool LooksLikeImageObject(JsonObject part)
    {
        var type = (part["type"]?.ToString() ?? string.Empty).Trim();
        return type.Contains("image", StringComparison.OrdinalIgnoreCase)
            || part["image_url"] is not null
            || part["input_image"] is not null
            || part["image"] is not null
            || (part["source"] is JsonObject source
                && (source["data"] is not null || source["url"] is not null));
    }

    private static bool IsImageKey(string key)
        => key.Equals("image_url", StringComparison.OrdinalIgnoreCase)
           || key.Equals("input_image", StringComparison.OrdinalIgnoreCase)
           || key.Equals("image", StringComparison.OrdinalIgnoreCase)
           || key.Equals("b64_json", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeImagePayload(string text)
        => text.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("data:application/octet-stream;base64,", StringComparison.OrdinalIgnoreCase);

    public static bool PromptExceedsContext(JsonObject payload, int hotContext, int headroom = LocalHistoryCompaction.Headroom)
    {
        if (hotContext <= 0)
        {
            return false;
        }

        return EstimatePromptTokens(payload) + 1 + Math.Max(0, headroom) > hotContext;
    }

    public static int ClampMaxTokensToContext(JsonObject payload, int hotContext, int headroom = LocalHistoryCompaction.Headroom)
    {
        var requested = ParseInt(Str(payload, "max_tokens"), 0);
        if (hotContext <= 0)
        {
            return requested;
        }

        var available = hotContext - EstimatePromptTokens(payload) - Math.Max(0, headroom);
        if (available < 1)
        {
            payload["max_tokens"] = 1;
            return 1;
        }

        var used = requested <= 0 ? available : Math.Min(requested, available);
        payload["max_tokens"] = used;
        return used;
    }

    private static JsonObject? MatchByRequestedName(IReadOnlyList<JsonObject> usable, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var lowered = requested.ToLowerInvariant();
        JsonObject? best = null;
        var bestLength = 0;
        foreach (var item in usable)
        {
            var name = Str(item, "variant").Trim();
            if (string.IsNullOrWhiteSpace(name)
                || name.Equals("(defaults)", StringComparison.OrdinalIgnoreCase)
                || name.Equals("base", StringComparison.OrdinalIgnoreCase)
                || name.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lowered.Contains(name.ToLowerInvariant(), StringComparison.Ordinal)
                && name.Length > bestLength)
            {
                best = item;
                bestLength = name.Length;
            }
        }

        return best;
    }

    private static string LastUserText(JsonObject payload)
        => LocalChatPayloadSignals.LastUserText(payload);

    private static string Str(JsonObject obj, string key)
        => obj[key]?.ToString() ?? string.Empty;

    private static double ParseDouble(string value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
