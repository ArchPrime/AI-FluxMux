using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalChatPayloadSignalsTests
{
    [Fact]
    public void PayloadHasImage_detects_image_url_in_message_content()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "see https://example.com/shot.png please"
                }
            }
        };

        Assert.True(LocalChatPayloadSignals.PayloadHasImage(payload));
    }

    [Fact]
    public void PayloadHasImage_detects_multipart_image_parts()
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
                        new JsonObject { ["type"] = "text", ["text"] = "look" },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,abc" } }
                    }
                }
            }
        };

        Assert.True(LocalChatPayloadSignals.PayloadHasImage(payload));
    }

    [Fact]
    public void LatestUserTurnHasImage_ignores_a_picture_on_an_earlier_turn()
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
                        new JsonObject { ["type"] = "text", ["text"] = "look" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,abc" }
                        }
                    }
                },
                new JsonObject { ["role"] = "assistant", ["content"] = "ack" },
                new JsonObject { ["role"] = "user", ["content"] = "write a script that counts from 1 to 10" }
            }
        };

        Assert.True(LocalChatPayloadSignals.PayloadHasImage(payload));
        Assert.False(LocalChatPayloadSignals.LatestUserTurnHasImage(payload));
        Assert.True(LocalChatPayloadSignals.StripImagesFromPayload(payload));
        Assert.False(LocalChatPayloadSignals.PayloadHasImage(payload));
    }

    [Fact]
    public void Leftover_picture_does_not_need_a_vision_reload_on_a_text_follow_up()
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
                        new JsonObject { ["type"] = "text", ["text"] = "look" },
                        new JsonObject
                        {
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = "AAAA"
                            }
                        }
                    }
                },
                new JsonObject { ["role"] = "assistant", ["content"] = "ack" },
                new JsonObject { ["role"] = "user", ["content"] = "fill the rest of this Context" }
            }
        };

        var (needVision, needThinking) = LocalReloadRouting.EvaluateCapabilityNeeds(
            payload,
            hotVision: false,
            hotReasoning: "Off");

        Assert.True(LocalChatPayloadSignals.PayloadHasImage(payload));
        Assert.False(needVision);
        Assert.False(needThinking);
        Assert.Null(LocalReloadRouting.DecideOffer(
            new JsonObject { ["local_reload_pool"] = new JsonArray() },
            needVision,
            needThinking,
            hotContext: 213056,
            turnTokens: 55000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "qwen.gguf",
            hotVariant: "max context",
            payloadHasImage: LocalChatPayloadSignals.LatestUserTurnHasImage(payload)));
    }

    [Fact]
    public void LatestUserTurnHasImage_is_true_when_this_user_turn_has_a_picture()
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
                        new JsonObject { ["type"] = "text", ["text"] = "look" },
                        new JsonObject
                        {
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = "AAAA"
                            }
                        }
                    }
                }
            }
        };

        Assert.True(LocalChatPayloadSignals.PayloadHasImage(payload));
        Assert.True(LocalChatPayloadSignals.LatestUserTurnHasImage(payload));
    }

    [Fact]
    public void PayloadWantsThinking_reads_chat_template_kwargs_and_top_level_flag()
    {
        var kwargsPayload = new JsonObject
        {
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true }
        };
        var topLevelPayload = new JsonObject { ["enable_thinking"] = true };
        var plainPayload = new JsonObject();

        Assert.True(LocalChatPayloadSignals.PayloadWantsThinking(kwargsPayload));
        Assert.True(LocalChatPayloadSignals.PayloadWantsThinking(topLevelPayload));
        Assert.False(LocalChatPayloadSignals.PayloadWantsThinking(plainPayload));
    }

    [Fact]
    public void PayloadHasToolCalls_detects_assistant_tool_calls_and_tool_role()
    {
        var toolCallPayload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "1" } }
                }
            }
        };
        var toolRolePayload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "tool", ["content"] = "ok" }
            }
        };

        Assert.True(LocalChatPayloadSignals.PayloadHasToolCalls(toolCallPayload));
        Assert.True(LocalChatPayloadSignals.PayloadHasToolCalls(toolRolePayload));
    }

    [Fact]
    public void PayloadLooksLikePatchOrPlan_detects_diff_and_cline_tool_markers()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "```diff\n--- a/foo.cs\n+++ b/foo.cs\nrun_command"
                }
            }
        };

        Assert.True(LocalChatPayloadSignals.PayloadLooksLikePatchOrPlan(payload));
    }

    [Theory]
    [InlineData(true, "Off", true, false)]
    [InlineData(false, "Off", false, true)]
    [InlineData(false, "On", false, false)]
    [InlineData(true, "On", true, false)]
    public void EvaluateCapabilityNeeds_matches_gateway_reload_rules(
        bool hotVision,
        string hotReasoning,
        bool payloadHasImage,
        bool payloadWantsThinking)
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = payloadHasImage
                        ? "see https://example.com/shot.png"
                        : "plain text"
                }
            }
        };

        if (payloadWantsThinking)
        {
            payload["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true };
        }

        var (needVision, needThinking) = LocalReloadRouting.EvaluateCapabilityNeeds(payload, hotVision, hotReasoning);

        Assert.Equal(payloadHasImage && !hotVision, needVision);
        Assert.Equal(payloadWantsThinking && !hotReasoning.Equals("On", StringComparison.OrdinalIgnoreCase), needThinking);
    }
}
