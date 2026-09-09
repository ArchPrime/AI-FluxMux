using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public sealed record LocalReloadOffer(
    JsonObject? Candidate,
    int TurnTokens,
    bool NearLimit,
    bool Overflows,
    bool DestinationTight,
    bool CompactRecommended,
    bool BetterChance = false);

public static class LocalReloadRouting
{
    public static (bool NeedVision, bool NeedThinking) EvaluateCapabilityNeeds(
        JsonObject payload,
        bool hotVision,
        string hotReasoning)
    {
        var wantsImage = LocalChatPayloadSignals.LatestUserTurnHasImage(payload);
        var wantsThinking = LocalChatPayloadSignals.PayloadWantsThinking(payload);
        var needVision = wantsImage && !hotVision;
        var needThinking = wantsThinking && !hotReasoning.Equals("On", StringComparison.OrdinalIgnoreCase);
        return (needVision, needThinking);
    }

    public static LocalReloadOffer? DecideOffer(
        JsonObject state,
        bool needVision,
        bool needThinking,
        int hotContext,
        int turnTokens,
        bool promptExceeds,
        bool compactAlreadyOn,
        string hotModel,
        string hotVariant,
        bool payloadHasImage = false)
    {
        var nearLimit = !promptExceeds && LocalHistoryCompaction.IsNearLimit(turnTokens, hotContext);
        if (!needVision && !needThinking && !nearLimit && !promptExceeds)
        {
            return null;
        }

        var requireVision = needVision || payloadHasImage;
        JsonObject? candidate = null;
        var betterChance = false;
        if (promptExceeds)
        {
            candidate = PickCandidate(state, requireVision, needThinking, turnTokens, hotModel, hotVariant);
        }
        else if (nearLimit)
        {
            candidate = PickCandidate(
                state,
                requireVision,
                needThinking,
                hotContext + 1,
                hotModel,
                hotVariant,
                sameFileOnly: true);
        }

        if (candidate is null && (needVision || needThinking))
        {
            candidate = PickCandidate(
                state,
                requireVision,
                needThinking,
                promptExceeds ? turnTokens : 0,
                hotModel,
                hotVariant,
                requireContextCapacity: false);
        }

        if (candidate is null && (nearLimit || promptExceeds))
        {
            candidate = PickBetterChanceCandidate(
                state,
                requireVision,
                needThinking,
                turnTokens,
                hotModel,
                hotVariant,
                hotContext);
            betterChance = candidate is not null;
        }

        var destContext = candidate is null ? 0 : ParseInt(Str(candidate, "context"), 0);
        var destinationTight = candidate is not null
            && LocalHistoryCompaction.IsDestinationTight(destContext, turnTokens);
        var compactRecommended = !compactAlreadyOn && (destinationTight || nearLimit || promptExceeds);
        if (candidate is null && !compactRecommended)
        {
            return null;
        }

        return new LocalReloadOffer(
            candidate,
            turnTokens,
            nearLimit,
            promptExceeds,
            destinationTight,
            compactRecommended,
            betterChance);
    }

    public static bool LocalCannotCoverTurn(LocalReloadOffer? offer, bool needVision, bool promptExceeds)
    {
        if (needVision)
        {
            return offer?.Candidate is null
                || !Str(offer.Candidate, "vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }

        return promptExceeds && offer?.Candidate is null;
    }

    public static JsonObject? PickCandidate(
        JsonObject state,
        bool needVision,
        bool needThinking,
        int neededContext,
        string hotModel,
        string hotVariant,
        bool sameFileOnly = false,
        bool requireContextCapacity = true)
    {
        if (state["local_reload_pool"] is not JsonArray pool)
        {
            return null;
        }

        if (!needVision && !needThinking && neededContext <= 0)
        {
            return null;
        }

        JsonObject? sameFile = null;
        JsonObject? otherFile = null;
        foreach (var node in pool.OfType<JsonObject>())
        {
            var model = Str(node, "model");
            var variant = Str(node, "variant");
            if (model.Equals(hotModel, StringComparison.OrdinalIgnoreCase)
                && variant.Equals(hotVariant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Covers(node, needVision, needThinking, neededContext, requireContextCapacity))
            {
                continue;
            }

            if (IsSameGguf(node, hotModel))
            {
                sameFile ??= node;
            }
            else if (!sameFileOnly)
            {
                otherFile ??= node;
            }
        }

        return sameFile ?? otherFile;
    }

    public static JsonObject? PickBetterChanceCandidate(
        JsonObject state,
        bool needVision,
        bool needThinking,
        int turnTokens,
        string hotModel,
        string hotVariant,
        int hotContext = 0)
    {
        if (state["local_reload_pool"] is not JsonArray pool || turnTokens <= 0)
        {
            return null;
        }

        var hotScore = ScoreChance(
            hotModel,
            Str(state, "local_gpu_offload"),
            ParseInt(Str(state, "local_ttft_ms"), 0));
        JsonObject? best = null;
        var bestScore = hotScore;
        foreach (var node in pool.OfType<JsonObject>())
        {
            var model = Str(node, "model");
            var variant = Str(node, "variant");
            if (model.Equals(hotModel, StringComparison.OrdinalIgnoreCase)
                && variant.Equals(hotVariant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destContext = ParseInt(Str(node, "context"), 0);
            if (hotContext > 0 && destContext <= hotContext)
            {
                continue;
            }

            if (!Covers(node, needVision, needThinking, turnTokens, requireContextCapacity: true))
            {
                continue;
            }

            var score = ScoreChance(model, Str(node, "gpuOffload"), ParseInt(Str(node, "ttftMs"), 0));
            if (IsSameGguf(node, hotModel))
            {
                score += 15;
            }

            if (score > bestScore)
            {
                best = node;
                bestScore = score;
            }
        }

        return best;
    }

    public static string BuildOfferReason(
        string hotModel,
        string hotVariant,
        bool hotVision,
        string hotReasoning,
        int hotContext,
        bool needVision,
        bool needThinking,
        int neededContext,
        JsonObject? candidate,
        bool nearLimit = false,
        bool destinationTight = false,
        bool compactAlreadyOn = false,
        bool betterChance = false,
        bool harnessNeedsRelaunch = false,
        string? lastServedKind = null,
        string? lastServedCloudLabel = null)
    {
        _ = hotModel;
        _ = hotVariant;
        _ = hotVision;
        _ = hotReasoning;
        var lead = BuildLeadReason(
            needVision,
            needThinking,
            nearLimit,
            neededContext,
            hotContext,
            lastServedKind,
            lastServedCloudLabel);
        if (candidate is null)
        {
            var compact = compactAlreadyOn
                ? " AI-FluxMux is already shortening older turns it forwards."
                : " Compact can shorten older turns AI-FluxMux forwards.";
            return lead + compact;
        }

        var candName = FormatPackLabel(Str(candidate, "model"), Str(candidate, "variant"));
        var candContext = ParseInt(Str(candidate, "context"), 0);
        var gain = DescribeCandidateGain(needVision, needThinking, betterChance, candContext);
        var text = lead
            + " You have an alternative model with "
            + gain
            + " ("
            + candName
            + "). Do you want to switch to this model?";
        if (destinationTight)
        {
            text += " That profile's Context is smaller than this chat, so Compact can shorten older turns first.";
        }

        _ = harnessNeedsRelaunch;
        text += " " + ClineSwitchSurvivalPolicy.HarnessIfAlsoOnPortAdvice;

        return text;
    }

    public static string FormatCloudContextLead(string? cloudLabel)
    {
        var named = string.IsNullOrWhiteSpace(cloudLabel)
            ? "the ready cloud model"
            : cloudLabel.Trim();
        return "This turn cannot continue: the last reply came from "
            + named
            + ". That cloud model's Context cannot hold this turn.";
    }

    private static string BuildLeadReason(
        bool needVision,
        bool needThinking,
        bool nearLimit,
        int neededContext,
        int hotContext,
        string? lastServedKind = null,
        string? lastServedCloudLabel = null)
    {
        if (needVision)
        {
            return "This turn cannot continue: the request included a picture, and Images is off on the loaded model profile.";
        }

        if (needThinking)
        {
            return "This turn cannot continue: Reasoning is off on the loaded model profile.";
        }

        if (nearLimit || (neededContext > 0 && neededContext > hotContext))
        {
            if (ProxyRuntimeStateSecrets.LastServedWasCloud(lastServedKind))
            {
                return FormatCloudContextLead(lastServedCloudLabel);
            }

            return "This turn cannot continue: the loaded model profile's Context is too small.";
        }

        return "This turn cannot continue.";
    }

    private static string DescribeCandidateGain(
        bool needVision,
        bool needThinking,
        bool betterChance,
        int candContext)
    {
        if (needVision)
        {
            return "Images on";
        }

        if (needThinking)
        {
            return "Reasoning on";
        }

        if (betterChance)
        {
            return "a better chance to finish this turn";
        }

        if (candContext > 0)
        {
            return "a larger Context (" + FormatCount(candContext) + ")";
        }

        return "the needed settings";
    }

    public static string FormatPackLabel(string model, string variant)
    {
        var file = Path.GetFileName((model ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(file))
        {
            file = "local model";
        }

        if (string.IsNullOrWhiteSpace(variant)
            || variant.Equals("(defaults)", StringComparison.OrdinalIgnoreCase)
            || variant.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return file + " (default)";
        }

        return variant.Trim() + " \u00b7 " + file;
    }

    private static int ScoreChance(string model, string gpuOffload, int ttftMs)
    {
        var score = QuantSpeedBonus(model);
        var gpu = (gpuOffload ?? string.Empty).Trim();
        if (gpu.Contains("GPU", StringComparison.OrdinalIgnoreCase)
            && !gpu.Contains("CPU", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        else if (gpu.Contains("CPU", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (ttftMs > 0)
        {
            score += Math.Max(0, 8000 - ttftMs) / 40;
        }

        return score;
    }

    private static int QuantSpeedBonus(string model)
    {
        var name = Path.GetFileName((model ?? string.Empty).Trim()).ToUpperInvariant();
        if (name.Contains("Q2"))
        {
            return 50;
        }

        if (name.Contains("Q3"))
        {
            return 45;
        }

        if (name.Contains("Q4"))
        {
            return 40;
        }

        if (name.Contains("Q5"))
        {
            return 30;
        }

        if (name.Contains("Q6"))
        {
            return 20;
        }

        if (name.Contains("IQ"))
        {
            return 35;
        }

        if (name.Contains("Q8"))
        {
            return 10;
        }

        return 0;
    }

    private static bool Covers(
        JsonObject node,
        bool needVision,
        bool needThinking,
        int neededContext,
        bool requireContextCapacity)
    {
        if (needVision && !Str(node, "vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (needThinking && !Str(node, "reasoning").Equals("On", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requireContextCapacity && neededContext > 0 && ParseInt(Str(node, "context"), 0) < neededContext)
        {
            return false;
        }

        return true;
    }

    private static bool IsSameGguf(JsonObject node, string hotModel)
        => ParseBool(Str(node, "sameGguf"))
           || Str(node, "model").Equals(hotModel, StringComparison.OrdinalIgnoreCase);

    private static string FormatCount(int value)
        => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Str(JsonObject obj, string key)
        => obj[key]?.ToString() ?? string.Empty;

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool ParseBool(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("1", StringComparison.OrdinalIgnoreCase)
           || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
