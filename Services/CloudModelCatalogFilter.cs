using System;
using System.Collections.Generic;
using System.Linq;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Narrows provider Refresh Models lists to ids worth showing for chat profile pickers.
/// The live catalog can still include embeddings, agents, and ids not yet on chat/completions.
/// </summary>
public static class CloudModelCatalogFilter
{
    public static List<string> Filter(string provider, IEnumerable<string> models)
    {
        return models
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(IsLikelyChatModelId)
            .Where(id => IsAllowedForProvider(provider, id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsLikelyChatModelId(string id)
    {
        var text = id.Trim().ToLowerInvariant();
        if (text.Contains("embed", StringComparison.Ordinal)
            || text.Contains("whisper", StringComparison.Ordinal)
            || text.Contains("tts", StringComparison.Ordinal)
            || text.Contains("dall-e", StringComparison.Ordinal)
            || text.Contains("dalle", StringComparison.Ordinal)
            || text.Contains("moderation", StringComparison.Ordinal)
            || text.Contains("transcribe", StringComparison.Ordinal)
            || text.StartsWith("text-similarity", StringComparison.Ordinal)
            || text.StartsWith("text-search", StringComparison.Ordinal)
            || text.StartsWith("code-search", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsAllowedForProvider(string provider, string id)
    {
        var text = id.Trim().ToLowerInvariant();
        if (!string.Equals(provider, "Copilot GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.StartsWith("exec-agent", StringComparison.Ordinal)
            || text.EndsWith("-inference", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
