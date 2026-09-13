using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalThinkBudgetNoticeTests
{
    [Fact]
    public void Client_message_names_Reasoning_Low_and_the_think_budget()
    {
        var text = LocalThinkBudgetNotice.FormatClientMessage("Low", 32768);
        Assert.Contains("llama-server", text, System.StringComparison.Ordinal);
        Assert.Contains("**Reasoning** is Low", text, System.StringComparison.Ordinal);
        Assert.Contains("2,048", text, System.StringComparison.Ordinal);
        Assert.Contains("Medium or XHigh", text, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.RaiseMaxTokensAdvice, text, System.StringComparison.Ordinal);
        Assert.Contains("This Client-app turn is over", text, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("the runner", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Port rules", text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnostics_says_Port_rules_will_not_fix_a_Reasoning_cap()
    {
        var text = LocalThinkBudgetNotice.FormatDiagnostics("Low", 32768, compactApplied: true);
        Assert.Contains("still thinking", text, System.StringComparison.Ordinal);
        Assert.Contains("**Reasoning** is Low", text, System.StringComparison.Ordinal);
        Assert.Contains("think budget is 2,048", text, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.RaiseMaxTokensAdvice, text, System.StringComparison.Ordinal);
        Assert.Contains("Changing Port rules will not fix this", text, System.StringComparison.Ordinal);
        Assert.Contains("Compact already shortened", text, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text);
        Assert.DoesNotContain("the runner", text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XHigh_does_not_invent_a_think_budget()
    {
        var text = LocalThinkBudgetNotice.FormatDiagnostics("XHigh", 32768, compactApplied: false);
        Assert.Contains("no think-token cap", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("think budget is", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Reasoning cap", text, System.StringComparison.Ordinal);
    }
}
