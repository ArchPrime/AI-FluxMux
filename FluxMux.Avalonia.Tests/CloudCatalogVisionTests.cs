using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudCatalogVisionTests
{
    [Fact]
    public void Catalog_entry_traits_come_from_json_fields_not_the_model_id()
    {
        var entry = new JsonObject
        {
            ["id"] = "any-id",
            ["capabilities"] = new JsonObject
            {
                ["vision"] = true,
                ["reasoning"] = true
            },
            ["context_window"] = 8192,
            ["max_output_tokens"] = 1024
        };

        var traits = CloudCatalogVision.TryRead(entry);
        Assert.True(traits.Vision);
        Assert.True(traits.Reasoning);
        Assert.Equal(8192, traits.ContextTokens);
        Assert.Equal(1024, traits.MaxTokens);
    }

    [Fact]
    public void Catalog_can_advertise_no_vision_without_using_the_model_name()
    {
        var payload = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "text-only-id",
                    ["vision"] = false,
                    ["max_model_len"] = 4096
                }
            }
        };

        var snapshot = CloudCatalogVision.Collect(payload, filterChatLike: false);
        Assert.Contains("text-only-id", snapshot.Ids);
        Assert.False(snapshot.TraitsById["text-only-id"].Vision);
        Assert.Equal(4096, snapshot.TraitsById["text-only-id"].ContextTokens);
    }

    [Fact]
    public void Missing_catalog_traits_are_unknown_not_guessed_from_the_id()
    {
        var traits = CloudCatalogVision.TryRead(new JsonObject { ["id"] = "gemini-flash" });
        Assert.Null(traits.Vision);
        Assert.Null(traits.ContextTokens);
        Assert.Null(traits.MaxTokens);
        Assert.Null(traits.Reasoning);
        Assert.True(CloudCatalogVision.FlagAllowsImages(null));
        Assert.True(CloudCatalogVision.FlagAllowsImages("Enabled"));
        Assert.False(CloudCatalogVision.FlagAllowsImages("Disabled"));
    }
}
