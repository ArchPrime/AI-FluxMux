using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class LocalVramFootprintEstimate
{
    public const double ProjectorGiB = 0.9;
    public const double RuntimeOverheadGiB = 0.4;
    public const int ReferenceImageEdge = 1344;

    /// <summary>
    /// Extra encoder / activation VRAM at <see cref="ReferenceImageEdge"/>, on top of projector weights.
    /// Combined with leftover GPU memory this keeps Images-on Context below the text-only fit
    /// without hard-coding a token window for one card.
    /// </summary>
    public const double VisionActivationGiBAtReferenceEdge = 1.9;

    /// <summary>
    /// GGUF metadata at or above this is a train window, not a hard rope ceiling.
    /// </summary>
    public const int LongContextMetadataFloor = 131072;

    /// <summary>
    /// Text-only window that already works on a 32 GB card with about
    /// <see cref="TextWorkingLeftoverGiB"/> leftover after weights. Other GPUs scale with leftover.
    /// </summary>
    public const int TextWorkingContext = 213056;

    /// <summary>
    /// Images-on window that already works on the same leftover as
    /// <see cref="TextWorkingContext"/> at <see cref="ReferenceImageEdge"/>.
    /// </summary>
    public const int ImagesWorkingContext = 163840;

    public const double TextWorkingLeftoverGiB = 12.0;

    public static int AdviseModelMaxContext(int reportedCtx)
    {
        if (reportedCtx <= 0)
        {
            return LongContextMetadataFloor;
        }

        if (reportedCtx < LongContextMetadataFloor)
        {
            return Math.Clamp(reportedCtx, 4096, DeepSeekHarnessSetup.MaxLocalContextWindow);
        }

        return DeepSeekHarnessSetup.MaxLocalContextWindow;
    }

    public static int SnapContextTokens(double tokens)
    {
        var clamped = Math.Clamp(tokens, 4096.0, DeepSeekHarnessSetup.MaxLocalContextWindow);
        return (int)(Math.Floor(clamped / 1024.0) * 1024.0);
    }

    public static int SoftenSaturatedContext(int contextTokens, int modelMaxCtx, double leftoverGpuGiB)
    {
        if (modelMaxCtx < DeepSeekHarnessSetup.MaxLocalContextWindow
            || contextTokens < DeepSeekHarnessSetup.MaxLocalContextWindow)
        {
            return contextTokens;
        }

        var leftover = Math.Max(0.5, leftoverGpuGiB);
        var scaled = leftover / TextWorkingLeftoverGiB * TextWorkingContext;
        if (scaled >= TextWorkingContext)
        {
            return TextWorkingContext;
        }

        return SnapContextTokens(scaled);
    }

    public static double VisionReserveGiB(double projectorGiB, int maxImageEdge)
    {
        var projector = Math.Max(0.15, projectorGiB > 0 ? projectorGiB : ProjectorGiB);
        var edge = Math.Clamp(maxImageEdge, 256, 4096);
        var scale = (edge / (double)ReferenceImageEdge) * (edge / (double)ReferenceImageEdge);
        return projector + VisionActivationGiBAtReferenceEdge * scale;
    }

    public static int ApplyVisionContextHaircut(int textContextTokens, double leftoverGpuGiB, double visionReserveGiB)
    {
        if (textContextTokens <= 4096 || visionReserveGiB <= 0)
        {
            return textContextTokens;
        }

        var leftover = Math.Max(0.5, leftoverGpuGiB);
        var keep = Math.Clamp(1.0 - visionReserveGiB / leftover, 0.45, 1.0);
        var raw = textContextTokens * keep;
        return (int)Math.Clamp(
            Math.Round(raw / 1024.0, MidpointRounding.AwayFromZero) * 1024.0,
            4096.0,
            DeepSeekHarnessSetup.MaxLocalContextWindow);
    }

    public static string FormatImagesHeadroomNote(
        bool imagesOn,
        double reserveGiB,
        int maxImageEdge,
        double gpuGb)
    {
        if (!imagesOn)
        {
            return "Images are off. AutoTune uses leftover graphics memory for Context. "
                + "Turning Images on reserves graphics memory for the projector and Max image edge, "
                + "so the wizard suggests a smaller Context. The wizard does not change Images.";
        }

        var edge = Math.Clamp(maxImageEdge, 256, 4096);
        var gpu = gpuGb > 0
            ? $" on this {gpuGb.ToString("0", CultureInfo.InvariantCulture)} GiB GPU"
            : string.Empty;
        return "Images on reserves about "
            + reserveGiB.ToString("0.0", CultureInfo.InvariantCulture)
            + " GiB for the projector and a "
            + edge.ToString(CultureInfo.InvariantCulture)
            + "-pixel Max image edge. AutoTune and Context use leftover graphics memory"
            + gpu
            + ". A larger Max image edge reserves more. The wizard does not change Images.";
    }

    public static double EstimateGiB(JsonObject settings, double modelFileGiB, bool imagesOn)
    {
        var contextTokens = Math.Max(1024d, ParseDouble(settings["OverrideContext"]?.ToString(), 8192d));
        var offloadText = settings["LocalGpuOffloadMode"]?.ToString() ?? "Auto";
        var layersText = settings["GpuLayers"]?.ToString() ?? "Auto";
        var offloadFraction = offloadText switch
        {
            "CPU only" => 0d,
            "GPU only" => 1d,
            "GPU + CPU" => 0.6d,
            _ => OffloadFractionFromLayers(layersText)
        };

        var kvBytesPerToken = ResolveKvBytesPerToken(
            settings["LocalKvCacheTypeK"]?.ToString() ?? "q8_0",
            settings["LocalKvCacheTypeV"]?.ToString() ?? "q8_0");
        var kvGiB = contextTokens * kvBytesPerToken / 1024d / 1024d / 1024d;
        var baseGiB = ParseDouble(settings["BaseModelGiB"]?.ToString(), 0d);
        if (baseGiB <= 0)
        {
            baseGiB = modelFileGiB > 0 ? modelFileGiB : 6d;
        }

        var estimatedGiB = baseGiB * offloadFraction + kvGiB + (offloadFraction > 0 ? RuntimeOverheadGiB : 0d);
        if (imagesOn)
        {
            var edge = (int)Math.Clamp(
                ParseDouble(settings["LocalVisionMaxImageEdge"]?.ToString(), ReferenceImageEdge),
                256,
                4096);
            estimatedGiB += VisionReserveGiB(ProjectorGiB, edge);
        }

        return Math.Max(0.2d, estimatedGiB);
    }

    public static double OffloadFractionFromLayers(string layers)
    {
        if (string.IsNullOrWhiteSpace(layers) || layers.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return 0.85d;
        }

        if (layers.Equals("999", StringComparison.OrdinalIgnoreCase))
        {
            return 1d;
        }

        if (layers.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return 0d;
        }

        return double.TryParse(layers, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? Math.Clamp(parsed / 80d, 0.15d, 1d)
            : 0.85d;
    }

    public static double ResolveKvBytesPerToken(string keyType, string valueType)
    {
        static double Width(string type)
        {
            var text = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (text.Contains("q4", StringComparison.Ordinal))
            {
                return 0.5d;
            }

            if (text.Contains("q8", StringComparison.Ordinal) || text.Contains("f16", StringComparison.Ordinal))
            {
                return 1d;
            }

            if (text.Contains("f32", StringComparison.Ordinal))
            {
                return 4d;
            }

            return 1d;
        }

        return 2d * (Width(keyType) + Width(valueType));
    }

    private static double ParseDouble(string? text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
