using System;
using System.Globalization;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Reasoning Low/Medium caps how long llama-server may think. When that
/// budget (or the stream) ends while still inside thought, Harness sits
/// mid-thought unless AI-FluxMux closes the think and says why.
/// </summary>
public static class LocalThinkBudgetNotice
{
    public const string Type = "local_think_cutoff";
    public const string ReasoningLabel = "Reasoning";

    public static string FormatClientBody(string? reasoningMode, int maxTokens)
    {
        var level = LocalReasoningRequestPolicy.NormalizeLevel(reasoningMode);
        var budget = LocalReasoningRequestPolicy.ThinkingBudget(reasoningMode, maxTokens);
        if (budget is { } tokens)
        {
            return PortRulesPostMortem.ChatTurnCannotContinue
                + "llama-server was still thinking. "
                + PortRulesPostMortem.MarkRule(ReasoningLabel)
                + " is "
                + level
                + " (think budget "
                + tokens.ToString("N0", CultureInfo.InvariantCulture)
                + " tokens). On this Quick Select row, set "
                + PortRulesPostMortem.MarkRule(ReasoningLabel)
                + " to Medium or XHigh for a longer think, or Off for a tool loop. "
                + PortRulesPostMortem.RaiseMaxTokensAdvice
                + " "
                + PortRulesPostMortem.NarrowerChatAdvice;
        }

        return PortRulesPostMortem.ChatTurnCannotContinue
            + "llama-server was still thinking. "
            + PortRulesPostMortem.MarkRule(ReasoningLabel)
            + " is "
            + level
            + " (no think-token cap). On this Quick Select row, set "
            + PortRulesPostMortem.MarkRule(ReasoningLabel)
            + " to Off for a tool loop. "
            + PortRulesPostMortem.NarrowerChatAdvice;
    }

    public static string FormatClientMessage(string? reasoningMode, int maxTokens, string? endpointApp = null)
        => FluxMuxGatewayRouting.FormatNewSlotEndpointMessage(
            FormatClientBody(reasoningMode, maxTokens),
            endpointApp);

    public static string FormatDiagnostics(string? reasoningMode, int maxTokens, bool compactApplied)
    {
        var level = LocalReasoningRequestPolicy.NormalizeLevel(reasoningMode);
        var budget = LocalReasoningRequestPolicy.ThinkingBudget(reasoningMode, maxTokens);
        var budgetBit = budget is { } tokens
            ? PortRulesPostMortem.MarkRule(ReasoningLabel)
                + " is "
                + level
                + "; the think budget is "
                + tokens.ToString("N0", CultureInfo.InvariantCulture)
                + " tokens."
            : PortRulesPostMortem.MarkRule(ReasoningLabel)
                + " is "
                + level
                + " (no think-token cap).";
        var compactBit = compactApplied
            ? " Compact already shortened older turns forwarded to llama-server."
            : string.Empty;
        var changeBit = budget is null
            ? " On this Quick Select row, set "
                + PortRulesPostMortem.MarkRule(ReasoningLabel)
                + " to Off for a tool loop."
            : " On this Quick Select row, set "
                + PortRulesPostMortem.MarkRule(ReasoningLabel)
                + " to Medium or XHigh for a longer think, or Off for a tool loop. "
                + PortRulesPostMortem.RaiseMaxTokensAdvice;
        var causeBit = budget is null
            ? " " + PortRulesPostMortem.ClientAppFaultLead
            : " " + PortRulesPostMortem.ClientAppFaultLead
                + " This is the Quick Select "
                + PortRulesPostMortem.MarkRule(ReasoningLabel)
                + " cap, not a Port rule.";
        return PortRulesPostMortem.ChatTurnCannotContinue
            + "llama-server was still thinking when this turn ended. "
            + budgetBit
            + causeBit
            + compactBit
            + changeBit
            + " "
            + PortRulesPostMortem.NarrowerChatAdvice;
    }
}
