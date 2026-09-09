using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// OpenAI-compatible /v1/models and GET /models (Cline 4.x uses the latter).
/// Port still accepts model id <c>local</c>.
/// Cline's OpenAI Compatible Model ID is the profile label (file plus variant).
/// That id, and <c>local</c>, must stay in this catalog even when llama-server
/// is parked or only cloud is hot — Cline fetches this list when the panel
/// opens, and a missing id can stop the webview from loading.
/// </summary>
public static class FluxMuxGatewayModels
{
    public const string LocalModelId = "local";
    public const string DefaultVariant = "(defaults)";

    public static string FormatProfileId(string? modelLabel, string? variant)
    {
        var trimmed = (modelLabel ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return LocalModelId;
        }

        var variantName = (variant ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(variantName)
            || variantName.Equals(DefaultVariant, StringComparison.OrdinalIgnoreCase)
            || variantName.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + " (" + variantName + ")";
    }

    public static JsonObject List(JsonObject state, IEnumerable<string>? extraIds = null)
    {
        var models = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localContext = ParsePositiveInt(Str(state, "local_context"));
        var cloudContext = ParsePositiveInt(Str(state, "context_window"));
        var localMaxTokens = ParsePositiveInt(Str(state, "local_max_tokens"));
        var cloudMaxTokens = ParsePositiveInt(Str(state, "max_tokens"));
        var localImages = ReadImages(Str(state, "local_vision"));
        var cloudImages = ReadImages(Str(state, "cloud_vision"));
        var localFile = FirstNonEmpty(Str(state, "local_preferred_model"), Str(state, "last_local_preferred_model"));
        var localVariant = FirstNonEmpty(Str(state, "local_variant"), Str(state, "last_local_variant"));
        var localLabel = FormatProfileId(localFile, localVariant);
        Add(models, seen, localLabel, localContext, localLabel, localMaxTokens, localImages);
        Add(models, seen, LocalModelId, localContext, localLabel, localMaxTokens, localImages);
        if (!string.IsNullOrWhiteSpace(localFile))
        {
            Add(models, seen, localFile, localContext, localLabel, localMaxTokens, localImages);
        }

        if (FluxMuxGatewayRouting.CloudTargetReady(state))
        {
            var cloudModel = Str(state, "cloud_preferred_model");
            if (!string.IsNullOrWhiteSpace(cloudModel))
            {
                var provider = Str(state, "last_cloud_provider");
                if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
                {
                    provider = string.Empty;
                }

                var cloudLabel = string.IsNullOrWhiteSpace(provider)
                    ? cloudModel
                    : provider + " / " + cloudModel;
                var cloudId = FormatProfileId(cloudLabel, Str(state, "cloud_variant"));
                Add(models, seen, cloudId, cloudContext, cloudId, cloudMaxTokens, cloudImages);
                Add(models, seen, cloudModel, cloudContext, cloudId, cloudMaxTokens, cloudImages);
            }
        }

        if (extraIds is not null)
        {
            foreach (var extraId in extraIds)
            {
                Add(models, seen, extraId, localContext, localLabel, localMaxTokens, localImages);
            }
        }

        return new JsonObject { ["object"] = "list", ["data"] = models };
    }

    private static void Add(
        JsonArray models,
        HashSet<string> seen,
        string id,
        int contextLength,
        string? displayName = null,
        int maxOutputTokens = 0,
        bool? images = null)
    {
        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
        {
            return;
        }

        var entry = new JsonObject
        {
            ["id"] = id,
            ["object"] = "model",
            ["owned_by"] = "FluxMux"
        };
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.Equals(id, StringComparison.Ordinal))
        {
            entry["name"] = displayName.Trim();
        }
        var meta = new JsonObject();
        if (contextLength > 0)
        {
            entry["context_length"] = contextLength;
            entry["max_model_len"] = contextLength;
            meta["n_ctx"] = contextLength;
        }

        // The reply budget llama-server was launched with, under the names clients
        // actually read. A client that reads none of them is no worse off than before.
        if (maxOutputTokens > 0)
        {
            entry["max_tokens"] = maxOutputTokens;
            entry["max_output_tokens"] = maxOutputTokens;
            meta["n_predict"] = maxOutputTokens;
        }

        // Only stated when the loaded profile says so either way. Silence means unknown,
        // which leaves a picture turn to the routing offer rather than a client refusing
        // to attach one on our say-so.
        if (images.HasValue)
        {
            entry["supports_images"] = images.Value;
            entry["supports_vision"] = images.Value;
            meta["vision"] = images.Value;
        }

        if (meta.Count > 0)
        {
            entry["meta"] = meta;
        }

        models.Add(entry);
    }

    private static string FirstNonEmpty(string first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : first;

    private static string Str(JsonObject? obj, string key)
    {
        if (obj is null)
        {
            return string.Empty;
        }

        var node = obj[key];
        if (node is null)
        {
            return string.Empty;
        }

        var text = node.ToString()?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }

    /// <summary>
    /// Images on the loaded profile: Enabled / Disabled as the runtime writes it, with
    /// the usual spellings accepted. An unset or unrecognised value stays unknown.
    /// </summary>
    private static bool? ReadImages(string value) => value.Trim().ToLowerInvariant() switch
    {
        "enabled" or "on" or "true" or "yes" => true,
        "disabled" or "off" or "false" or "no" => false,
        _ => null
    };

    private static int ParsePositiveInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
}
