using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudProviderDisplayNamesTests
{
    [Theory]
    [InlineData("Gemini", "Google/Gemini")]
    [InlineData("gemini", "Google/Gemini")]
    [InlineData("Anthropic", "Anthropic")]
    [InlineData("OpenAI", "OpenAI")]
    [InlineData("Copilot GitHub", "Copilot GitHub")]
    [InlineData("Custom OpenAI-Compatible", "Custom OpenAI-Compatible")]
    [InlineData("", "")]
    public void Display_name_keeps_stored_ids_except_google_gemini(string provider, string expected)
    {
        Assert.Equal(expected, CloudProviderDisplayNames.ToDisplayName(provider));
    }
}
