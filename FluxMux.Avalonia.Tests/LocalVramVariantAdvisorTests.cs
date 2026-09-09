using System.Globalization;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalVramVariantAdvisorTests
{
    [Fact]
    public void PickValidatedAlternative_prefers_lower_measured_footprint_on_same_gguf()
    {
        var profiles = new JsonObject
        {
            ["big.gguf::heavy"] = Profile(
                validated: true,
                context: 131072,
                imagesOn: true,
                measuredGiB: 22d),
            ["big.gguf::light"] = Profile(
                validated: true,
                context: 65536,
                imagesOn: false,
                measuredGiB: 14d)
        };

        var suggestion = LocalVramVariantAdvisor.PickValidatedAlternative(
            profiles,
            "big.gguf",
            "heavy",
            freeGiB: 16d,
            currentFootprintGiB: 22d,
            modelFileGiB: 15d);

        Assert.NotNull(suggestion);
        Assert.Equal("light", suggestion.Value.VariantLabel);
        Assert.Equal(65536, suggestion.Value.Context);
        Assert.False(suggestion.Value.ImagesOn);
        Assert.True(suggestion.Value.Measured);
    }

    [Fact]
    public void PickValidatedAlternative_skips_unvalidated_copies()
    {
        var profiles = new JsonObject
        {
            ["big.gguf::(defaults)"] = Profile(validated: true, context: 32768, imagesOn: false, measuredGiB: 20d),
            ["big.gguf::draft"] = Profile(validated: false, context: 8192, imagesOn: false, measuredGiB: 10d)
        };

        var suggestion = LocalVramVariantAdvisor.PickValidatedAlternative(
            profiles,
            "big.gguf",
            "(defaults)",
            freeGiB: 16d,
            currentFootprintGiB: 20d,
            modelFileGiB: 15d);

        Assert.Null(suggestion);
    }

    [Fact]
    public void FormatSuggestion_appends_actionable_copy_label()
    {
        var suggestion = new LocalVramVariantSuggestion("light", 65536, false, 14d, true);
        var text = LocalVramVariantAdvisor.FormatSuggestion(suggestion);
        Assert.Contains("validated copy 'light'", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("measured", text, System.StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject Profile(bool validated, int context, bool imagesOn, double measuredGiB)
    {
        var profile = new JsonObject
        {
            ["OverrideContext"] = context,
            ["LocalVisionEnabled"] = imagesOn ? "Enabled" : "Disabled",
            ["MeasuredVramGiB"] = measuredGiB.ToString("0.##", CultureInfo.InvariantCulture)
        };
        if (validated)
        {
            profile["EndpointValidatedUtc"] = "2026-01-01T00:00:00Z";
        }

        return profile;
    }
}
