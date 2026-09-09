using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Reads advertised traits from a provider catalog entry.
/// Uses the JSON the host returned — not model-id or provider-name guesses.
/// </summary>
public static class CloudCatalogVision
{
    public readonly record struct Traits(
        bool? Vision,
        int? ContextTokens,
        int? MaxTokens,
        bool? Reasoning);

    public readonly record struct Snapshot(
        IReadOnlyList<string> Ids,
        IReadOnlyDictionary<string, Traits> TraitsById);

    public static bool FlagAllowsImages(string? storedFlag)
        => !string.Equals(storedFlag?.Trim(), "Disabled", StringComparison.OrdinalIgnoreCase);

    public static bool? TryReadVision(JsonObject? entry)
        => TryRead(entry).Vision;

    public static Traits TryRead(JsonObject? entry)
    {
        if (entry is null)
        {
            return default;
        }

        return new Traits(
            ReadVision(entry),
            ReadPositiveInt(
                entry["context_window"],
                entry["max_model_len"],
                entry["context_length"],
                entry["n_ctx"],
                entry["max_position_embeddings"]),
            ReadPositiveInt(
                entry["max_tokens"],
                entry["max_output_tokens"],
                entry["max_completion_tokens"],
                entry["max_tokens_to_sample"]),
            ReadReasoning(entry));
    }

    public static Snapshot Collect(JsonNode? payload, bool filterChatLike)
    {
        var ids = new List<string>();
        var traits = new Dictionary<string, Traits>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in EnumerateEntries(payload))
        {
            var id = ReadModelId(entry);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (filterChatLike && !CloudModelCatalogFilter.IsLikelyChatModelId(id))
            {
                continue;
            }

            ids.Add(id);
            if (entry is JsonObject modelObject)
            {
                var read = TryRead(modelObject);
                if (read.Vision.HasValue || read.ContextTokens.HasValue
                    || read.MaxTokens.HasValue || read.Reasoning.HasValue)
                {
                    traits[id] = read;
                }
            }
        }

        return new Snapshot(ids, traits);
    }

    public static void Merge(IDictionary<string, Traits> target, IReadOnlyDictionary<string, Traits> source)
    {
        foreach (var pair in source)
        {
            target[pair.Key] = pair.Value;
        }
    }

    private static bool? ReadVision(JsonObject entry)
    {
        if (TryReadBoolean(entry["vision"], out var vision))
        {
            return vision;
        }

        if (TryReadBoolean(entry["supports_vision"], out var supportsVision))
        {
            return supportsVision;
        }

        if (entry["capabilities"] is JsonObject capabilities)
        {
            if (TryReadBoolean(capabilities["vision"], out var capVision))
            {
                return capVision;
            }

            if (TryReadBoolean(capabilities["image"], out var capImage))
            {
                return capImage;
            }
        }

        if (ModalitiesIncludeImage(entry["modalities"])
            || ModalitiesIncludeImage(entry["input_modalities"])
            || ModalitiesIncludeImage(entry["supported_modalities"]))
        {
            return true;
        }

        if (entry["architecture"] is JsonObject architecture
            && (TokenLooksLikeImage(architecture["modality"]?.ToString())
                || TokenLooksLikeImage(architecture["input_modalities"]?.ToString())))
        {
            return true;
        }

        return null;
    }

    private static bool? ReadReasoning(JsonObject entry)
    {
        if (TryReadBoolean(entry["reasoning"], out var reasoning)
            || TryReadBoolean(entry["thinking"], out reasoning))
        {
            return reasoning;
        }

        if (entry["capabilities"] is JsonObject capabilities
            && (TryReadBoolean(capabilities["reasoning"], out reasoning)
                || TryReadBoolean(capabilities["thinking"], out reasoning)))
        {
            return reasoning;
        }

        return null;
    }

    private static IEnumerable<JsonNode?> EnumerateEntries(JsonNode? payload)
    {
        if (payload is JsonObject root)
        {
            if (root["data"] is JsonArray data)
            {
                foreach (var item in data)
                {
                    yield return item;
                }

                yield break;
            }

            if (root["models"] is JsonArray models)
            {
                foreach (var item in models)
                {
                    yield return item;
                }
            }
        }
        else if (payload is JsonArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }
        }
    }

    private static string ReadModelId(JsonNode? item)
    {
        if (item is JsonObject modelObject)
        {
            var raw = !string.IsNullOrWhiteSpace(modelObject["id"]?.ToString())
                ? modelObject["id"]!.ToString()
                : (modelObject["name"]?.ToString() ?? string.Empty);
            return NormalizeModelId(raw);
        }

        return item is JsonValue jsonValue ? NormalizeModelId(jsonValue.ToString()) : string.Empty;
    }

    private static string NormalizeModelId(string? raw)
    {
        var modelId = (raw ?? string.Empty).Trim();
        if (modelId.Contains('/', StringComparison.Ordinal)
            && modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            modelId = modelId[(modelId.LastIndexOf('/') + 1)..];
        }

        return modelId;
    }

    private static bool ModalitiesIncludeImage(JsonNode? node)
    {
        if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                if (TokenLooksLikeImage(item?.ToString()))
                {
                    return true;
                }
            }

            return false;
        }

        return TokenLooksLikeImage(node?.ToString());
    }

    private static bool TokenLooksLikeImage(string? token)
    {
        var text = (token ?? string.Empty).Trim();
        return text.Equals("image", StringComparison.OrdinalIgnoreCase)
               || text.Equals("vision", StringComparison.OrdinalIgnoreCase)
               || text.Equals("multimodal", StringComparison.OrdinalIgnoreCase)
               || text.Contains("image", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ReadPositiveInt(params JsonNode?[] nodes)
    {
        foreach (var node in nodes)
        {
            if (node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number) && number > 0)
            {
                return number;
            }

            var text = node?.ToString()?.Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryReadBoolean(JsonNode? node, out bool value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out value))
        {
            return true;
        }

        var text = node?.ToString()?.Trim();
        if (text is "true" or "1" or "yes" or "on")
        {
            value = true;
            return true;
        }

        if (text is "false" or "0" or "no" or "off")
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }
}
