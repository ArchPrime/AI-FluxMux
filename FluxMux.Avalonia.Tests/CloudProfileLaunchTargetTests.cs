using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudProfileLaunchTargetTests
{
    [Fact]
    public void Expanded_profile_supplies_provider_and_model_when_selection_fields_are_empty()
    {
        var item = new CloudVariantTreeItemViewModel(
            "gemini-flash-lite-latest",
            "(defaults)",
            isBase: true,
            "summary",
            provider: "Gemini");

        Assert.True(CloudProfileLaunchTarget.TryResolve(
            item,
            selectedProvider: "",
            selectedModel: "",
            selectedVariant: "",
            defaultVariant: "(defaults)",
            out var provider,
            out var model,
            out var variant));

        Assert.Equal("Gemini", provider);
        Assert.Equal("gemini-flash-lite-latest", model);
        Assert.Equal("(defaults)", variant);
    }

    [Fact]
    public void Empty_tree_fields_do_not_wipe_a_filled_selection()
    {
        var item = new CloudVariantTreeItemViewModel(
            "",
            "",
            isBase: true,
            "summary",
            provider: "");

        Assert.True(CloudProfileLaunchTarget.TryResolve(
            item,
            selectedProvider: "Gemini",
            selectedModel: "gemini-flash-lite-latest",
            selectedVariant: "(defaults)",
            defaultVariant: "(defaults)",
            out var provider,
            out var model,
            out var variant));

        Assert.Equal("Gemini", provider);
        Assert.Equal("gemini-flash-lite-latest", model);
        Assert.Equal("(defaults)", variant);
    }

    [Fact]
    public void Missing_provider_and_model_is_rejected()
    {
        Assert.False(CloudProfileLaunchTarget.TryResolve(
            item: null,
            selectedProvider: "",
            selectedModel: "",
            selectedVariant: "(defaults)",
            defaultVariant: "(defaults)",
            out _,
            out _,
            out _));
    }
}
