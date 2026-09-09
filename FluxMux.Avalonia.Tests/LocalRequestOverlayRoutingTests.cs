using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalRequestOverlayRoutingTests
{
    [Fact]
    public void EstimatePromptTokens_does_not_treat_embedded_image_bytes_as_text()
    {
        var image = "data:image/png;base64," + new string('A', 280_000);
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "please describe this image" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = image }
                        }
                    }
                }
            }
        };

        var tokens = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.InRange(tokens, LocalRequestOverlayRouting.ImagePromptTokens, LocalRequestOverlayRouting.ImagePromptTokens + 200);
        Assert.True(tokens < 10_000);
    }

    [Fact]
    public void Picture_on_a_text_only_load_is_not_a_filling_turn()
    {
        var state = new JsonObject
        {
            ["local_context"] = 131072,
            ["local_vision"] = "Disabled"
        };
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "please describe this image" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = "data:image/png;base64," + new string('B', 280_000)
                            }
                        }
                    }
                }
            },
            ["max_tokens"] = 16384
        };

        Assert.False(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.False(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            payload));
    }

    [Fact]
    public void Picture_on_a_vision_load_is_not_a_filling_turn()
    {
        var state = new JsonObject
        {
            ["local_context"] = 131072,
            ["local_vision"] = "Enabled"
        };
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "please describe this image" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = "data:image/jpeg;base64," + new string('C', 280_000)
                            }
                        }
                    }
                }
            },
            ["max_tokens"] = 16384
        };

        var tokens = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.True(tokens < 10_000);
        Assert.False(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.False(FluxMuxGatewayRouting.PromptLooksTooLargeForLocal(state, payload));
        Assert.False(LocalHistoryCompaction.IsNearLimit(tokens, 131072));
    }

    [Fact]
    public void EstimatePromptTokens_redacts_escaped_data_uri_slashes()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = "data:image\\/png;base64," + new string('D', 280_000)
                            }
                        }
                    }
                }
            }
        };

        var tokens = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.True(tokens < 10_000);
    }

    [Fact]
    public void Picture_as_raw_base64_source_is_not_a_filling_turn()
    {
        var state = new JsonObject
        {
            ["local_context"] = 131072,
            ["local_vision"] = "Enabled"
        };
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "please describe this image" },
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = new string('E', 280_000)
                            }
                        }
                    }
                }
            },
            ["max_tokens"] = 16384
        };

        var tokens = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.True(tokens < 10_000);
        Assert.False(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.False(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            payload));
    }

    [Fact]
    public void Long_alphanumeric_text_is_counted_as_tokens_not_as_a_picture()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = new string('n', 14000) }
            }
        };

        var fillTokens = LocalRequestOverlayRouting.EstimatePromptTokens(
            payload,
            LocalRequestOverlayRouting.FillingCharsPerToken);
        Assert.True(fillTokens >= 7000);
        Assert.False(LocalChatPayloadSignals.PayloadHasImage(payload));
    }
}
