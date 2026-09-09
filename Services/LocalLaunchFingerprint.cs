using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class LocalLaunchFingerprint
{
    public static string From(JsonObject? settings)
    {
        if (settings is null)
        {
            return string.Empty;
        }

        var visionOn = ProfileSettingsFingerprint.NormalizeVisionEnabled(settings["LocalVisionEnabled"]?.ToString())
            .Equals("enabled", StringComparison.OrdinalIgnoreCase);

        return string.Join("|",
            settings["OverrideContext"]?.ToString() ?? "8192",
            settings["OverrideThreads"]?.ToString() ?? "Auto",
            settings["LocalThreadsBatch"]?.ToString() ?? "Auto",
            settings["LocalGpuOffloadMode"]?.ToString() ?? "Auto",
            settings["GpuLayers"]?.ToString() ?? "Auto",
            settings["LocalFlashAttention"]?.ToString() ?? "Auto",
            settings["LocalKvCacheTypeK"]?.ToString() ?? "Auto",
            settings["LocalKvCacheTypeV"]?.ToString() ?? "Auto",
            settings["LocalChatTemplate"]?.ToString() ?? "Auto",
            settings["LocalMultiUserMode"]?.ToString() ?? "Auto",
            settings["LocalUnbanTokensMode"]?.ToString() ?? "Auto",
            settings["LocalBatchSize"]?.ToString() ?? string.Empty,
            settings["LocalUbatchSize"]?.ToString() ?? string.Empty,
            settings["LocalSpecType"]?.ToString() ?? string.Empty,
            settings["LocalCacheReuse"]?.ToString() ?? string.Empty,
            settings["LocalCacheRam"]?.ToString() ?? string.Empty,
            settings["LocalFit"]?.ToString() ?? string.Empty,
            settings["LocalSwaFull"]?.ToString() ?? string.Empty,
            settings["LocalReasoning"]?.ToString() ?? string.Empty,
            settings["LocalReasoningEffort"]?.ToString() ?? string.Empty,
            settings["LocalReasoningBudget"]?.ToString() ?? string.Empty,
            settings["LocalMultiGpuMode"]?.ToString() ?? "Auto",
            settings["LocalSplitMode"]?.ToString() ?? string.Empty,
            settings["LocalMainGpu"]?.ToString() ?? string.Empty,
            settings["LocalTensorSplit"]?.ToString() ?? string.Empty,
            settings["LocalChatParser"]?.ToString() ?? string.Empty,
            visionOn ? "enabled" : "disabled",
            visionOn ? (settings["LocalVisionProjectorPath"]?.ToString() ?? string.Empty) : string.Empty,
            visionOn ? (settings["LocalVisionMaxImageEdge"]?.ToString() ?? string.Empty) : string.Empty);
    }
}
