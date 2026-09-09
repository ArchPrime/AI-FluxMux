using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class LocalProfileAttributeSummary
{
    public static string FormatLocal(JsonObject? settings)
    {
        if (settings is null)
        {
            return string.Empty;
        }

        var images = EnabledLabel(settings["LocalVisionEnabled"]?.ToString(), "Images");
        var thinking = OnOffLabel(settings["LocalReasoning"]?.ToString(), "thinking");
        var context = CompactInt(settings["OverrideContext"]?.ToString(), "ctx");
        var gpu = settings["LocalGpuOffloadMode"]?.ToString();
        if (string.IsNullOrWhiteSpace(gpu))
        {
            gpu = "GPU Auto";
        }

        var temperature = CompactDecimal(settings["LocalTemperature"]?.ToString(), "0.3");
        var maxTokens = CompactInt(settings["OverrideMaxTokens"]?.ToString(), "max");
        return $"{images} \u00b7 {thinking} \u00b7 {context} \u00b7 {gpu} \u00b7 temp {temperature} \u00b7 {maxTokens}";
    }

    public static string FormatLocalSuitability(JsonObject? settings, IReadOnlyList<string>? runtimeHints = null)
    {
        var summary = FormatLocal(settings);
        if (string.IsNullOrWhiteSpace(summary) || runtimeHints is null || runtimeHints.Count == 0)
        {
            return summary;
        }

        return summary + " \u00b7 " + string.Join(" \u00b7 ", runtimeHints);
    }

    public static string FormatCloudSuitability(JsonObject? settings, IReadOnlyList<string>? runtimeHints = null)
    {
        var summary = FormatCloud(settings);
        if (string.IsNullOrWhiteSpace(summary) || runtimeHints is null || runtimeHints.Count == 0)
        {
            return summary;
        }

        return summary + " \u00b7 " + string.Join(" \u00b7 ", runtimeHints);
    }

    public static string FormatCloud(JsonObject? settings)
    {
        if (settings is null)
        {
            return string.Empty;
        }

        var images = EnabledLabel(settings["CloudVisionEnabled"]?.ToString(), "Images");
        var reasoning = settings["CloudReasoningMode"]?.ToString();
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            reasoning = "Balanced";
        }

        var context = settings["CloudContextWindow"]?.ToString();
        if (string.IsNullOrWhiteSpace(context))
        {
            context = "Auto";
        }

        var temperature = CompactDecimal(settings["CloudTemperature"]?.ToString(), "0.7");
        var maxTokens = CompactInt(settings["CloudMaxTokens"]?.ToString(), "max");
        return $"Cloud \u00b7 {images} \u00b7 {reasoning} \u00b7 ctx {context} \u00b7 temp {temperature} \u00b7 {maxTokens}";
    }

    private static string EnabledLabel(string? value, string noun)
        => (value ?? string.Empty).Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            ? $"{noun} on"
            : $"{noun} off";

    private static string OnOffLabel(string? value, string noun)
        => (value ?? string.Empty).Equals("On", StringComparison.OrdinalIgnoreCase)
            ? $"{noun} on"
            : $"{noun} off";

    private static string CompactInt(string? value, string prefix)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return $"{prefix} {parsed.ToString("N0", CultureInfo.InvariantCulture)}";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return prefix;
        }

        return $"{prefix} {value}";
    }

    private static string CompactDecimal(string? value, string fallback)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return fallback;
    }
}
