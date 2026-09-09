using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public readonly record struct LocalVramVariantSuggestion(
    string VariantLabel,
    int Context,
    bool ImagesOn,
    double FootprintGiB,
    bool Measured);

/// <summary>
/// Finds a validated local profile variant on the same GGUF that fits available VRAM better than the current choice.
/// </summary>
public static class LocalVramVariantAdvisor
{
    public static LocalVramVariantSuggestion? PickValidatedAlternative(
        JsonObject? localProfiles,
        string modelFileName,
        string currentVariant,
        double freeGiB,
        double currentFootprintGiB,
        double modelFileGiB)
    {
        if (localProfiles is null
            || string.IsNullOrWhiteSpace(modelFileName)
            || freeGiB <= 0
            || currentFootprintGiB <= 0)
        {
            return null;
        }

        var normalizedCurrent = NormalizeVariant(currentVariant);
        var prefix = modelFileName.Trim() + "::";
        LocalVramVariantSuggestion? best = null;
        foreach (var entry in localProfiles)
        {
            if (!entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || entry.Value is not JsonObject settings)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(settings["EndpointValidatedUtc"]?.ToString()))
            {
                continue;
            }

            var variant = entry.Key[prefix.Length..];
            if (NormalizeVariant(variant).Equals(normalizedCurrent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var imagesOn = (settings["LocalVisionEnabled"]?.ToString() ?? string.Empty)
                .Equals("Enabled", StringComparison.OrdinalIgnoreCase);
            var measured = ParseDouble(settings["MeasuredVramGiB"]?.ToString(), 0d);
            var footprint = measured > 0
                ? measured
                : LocalVramFootprintEstimate.EstimateGiB(settings, modelFileGiB, imagesOn);
            if (footprint >= currentFootprintGiB - 0.05d
                || footprint > freeGiB - LocalVramPressureAdvisor.HeadroomGiB)
            {
                continue;
            }

            var context = ParseInt(settings["OverrideContext"]?.ToString(), 0);
            var label = DisplayVariantLabel(variant);
            var candidate = new LocalVramVariantSuggestion(label, context, imagesOn, footprint, measured > 0);
            if (best is null || candidate.FootprintGiB < best.Value.FootprintGiB)
            {
                best = candidate;
            }
        }

        return best;
    }

    public static string FormatSuggestion(LocalVramVariantSuggestion? suggestion)
    {
        if (suggestion is null)
        {
            return string.Empty;
        }

        var value = suggestion.Value;
        var images = value.ImagesOn ? "Images on" : "Images off";
        var source = value.Measured ? "measured" : "estimated";
        return $" Try validated copy '{value.VariantLabel}' (Context {value.Context}, {images}, ~{value.FootprintGiB:0.#} GiB {source}).";
    }

    private static string DisplayVariantLabel(string variant)
        => variant.Equals("(defaults)", StringComparison.OrdinalIgnoreCase) ? "default" : variant;

    private static string NormalizeVariant(string? variant)
    {
        var text = (variant ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? "(defaults)" : text;
    }

    private static double ParseDouble(string? text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
