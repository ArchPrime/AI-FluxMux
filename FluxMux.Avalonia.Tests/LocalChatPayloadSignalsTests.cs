using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalChatPayloadSignalsTests
{
    [Fact]
    public void PayloadHasImage_ignores_a_picture_url_in_system_text()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "see https://example.com/shot.png in the docs"
                },
                new JsonObject { ["role"] = "user", ["content"] = "write a script" }
            }
        };

        Assert.False(LocalChatPayloadSignals.PayloadHasImage(payload));
        Assert.False(LocalChatPayloadSignals.LatestUserTurnHasImage(payload));
    }

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
        Assert.Equal(
            payloadWantsThinking && !LocalReasoningLaunchPolicy.IsThinkingEnabled(hotReasoning),
            needThinking);
    }

    [Theory]
    [InlineData("XHigh")]
    [InlineData("Medium")]
    [InlineData("Low")]
    [InlineData("On")]
    public void Live_thinking_depth_does_not_offer_a_reasoning_reload(string liveReasoning)
    {
        var payload = new JsonObject
        {
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "continue" }
            }
        };

        var (_, needThinking) = LocalReloadRouting.EvaluateCapabilityNeeds(payload, hotVision: true, liveReasoning);
        Assert.False(needThinking);
    }

    [Fact]
    public void Off_still_offers_a_reasoning_reload_when_the_payload_asks_to_think()
    {
        var payload = new JsonObject
        {
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "continue" }
            }
        };

        var (_, needThinking) = LocalReloadRouting.EvaluateCapabilityNeeds(payload, hotVision: true, "Off");
        Assert.True(needThinking);
    }

    [Fact]
    public void KeepMostRecentImages_forwards_only_the_last_few_pictures()
    {
        var messages = new JsonArray();
        for (var i = 0; i < 5; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "run " + i },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,run" + i }
                    }
                }
            });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ack " + i });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(3, LocalChatPayloadSignals.KeepMostRecentImages(payload));
        Assert.True(LocalChatPayloadSignals.LatestUserTurnHasImage(payload));
        var json = payload.ToJsonString();
        Assert.Contains("data:image/png;base64,run4", json);
        Assert.Contains("data:image/png;base64,run3", json);
        Assert.DoesNotContain("data:image/png;base64,run2", json);
        Assert.DoesNotContain("data:image/png;base64,run1", json);
        Assert.DoesNotContain("data:image/png;base64,run0", json);
        Assert.Contains("run 0", json);
        Assert.Contains(LocalChatPayloadSignals.PictureDropNote, json);
        Assert.DoesNotContain("Do not interrogate", json, StringComparison.Ordinal);
        Assert.DoesNotContain("new Cline task", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cline_tool_screenshots_count_as_this_turn_and_older_ones_are_dropped()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "inspect the projection" }
        };
        for (var i = 0; i < 5; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "screenshot " + i,
                ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "call" + i } }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "shot " + i },
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = "shot" + i
                        }
                    }
                }
            });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalChatPayloadSignals.PayloadHasImage(payload));
        Assert.True(LocalChatPayloadSignals.LatestUserTurnHasImage(payload));
        Assert.Equal(4, LocalChatPayloadSignals.KeepMostRecentImages(payload));
        var json = payload.ToJsonString();
        Assert.Contains("shot4", json);
        Assert.DoesNotContain("\"data\":\"shot3\"", json);
        Assert.DoesNotContain("\"data\":\"shot2\"", json);
        Assert.DoesNotContain("\"data\":\"shot1\"", json);
        Assert.DoesNotContain("\"data\":\"shot0\"", json);
        Assert.Contains("shot 0", json);
        Assert.Contains(LocalChatPayloadSignals.PictureOmittedMark, json);
        Assert.Contains(LocalChatPayloadSignals.PictureDropNote, json);
        Assert.Contains("Attach the picture", json);
        Assert.DoesNotContain("Do not interrogate", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_text_that_only_names_a_png_is_not_a_picture()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "inspect the projection" },
                new JsonObject
                {
                    ["role"] = "tool",
                    ["content"] = "wrote shot.png and see https://example.com/debug.png"
                }
            }
        };

        Assert.False(LocalChatPayloadSignals.PayloadHasImage(payload));
        Assert.False(LocalChatPayloadSignals.LatestUserTurnHasImage(payload));
        Assert.Equal(0, LocalChatPayloadSignals.KeepMostRecentImages(payload));
    }

    [Fact]
    public void KeepMostRecentImages_keeps_the_last_parts_on_one_turn()
    {
        var parts = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "compare these" }
        };
        for (var i = 0; i < 5; i++)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,shot" + i }
            });
        }

        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = parts }
            }
        };

        Assert.Equal(0, LocalChatPayloadSignals.KeepMostRecentImages(payload));
        var json = payload.ToJsonString();
        Assert.Contains("shot4", json);
        Assert.Contains("shot3", json);
        Assert.Contains("shot2", json);
        Assert.Contains("shot1", json);
        Assert.Contains("shot0", json);
        Assert.DoesNotContain(LocalChatPayloadSignals.PictureDropNote, json);
    }

    [Fact]
    public void KeepMostRecentImages_forwards_a_picture_the_user_attaches_this_turn()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "inspect the projection" }
        };
        for (var i = 0; i < 5; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = "old" + i
                        }
                    }
                }
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "here is the missing frame" },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,supplied" }
                }
            }
        });

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(4, LocalChatPayloadSignals.KeepMostRecentImages(payload));
        var json = payload.ToJsonString();
        Assert.Contains("supplied", json);
        Assert.Contains("old4", json);
        Assert.DoesNotContain("\"data\":\"old3\"", json);
        Assert.DoesNotContain("\"data\":\"old2\"", json);
        Assert.DoesNotContain("\"data\":\"old1\"", json);
        Assert.DoesNotContain("\"data\":\"old0\"", json);
        Assert.Contains(LocalChatPayloadSignals.PictureDropNote, json);
        Assert.Contains("This Client-app chat can continue", json);
    }

    [Fact]
    public void HasRepeatedToolCommand_detects_the_last_two_shell_calls()
    {
        var payload = PayloadWithAssistantCalls(
            ShellCall("execute_command", "dir"),
            ShellCall("execute_command", "  dir "));

        Assert.True(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out var command));
        Assert.Equal("dir", command);
    }

    [Fact]
    public void HasRepeatedToolCommand_allows_the_last_two_read_file_calls()
    {
        var payload = PayloadWithAssistantCalls(
            FileCall("read_file", "src/app.ts"),
            FileCall("read_file", "src/app.ts"));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
    }

    [Fact]
    public void HasRepeatedToolCommand_allows_harness_read_of_the_same_source_file()
    {
        var payload = PayloadWithAssistantCalls(
            FileCall("read", @"C:\AI_Workbench\Workspace\game\car_racing.py"),
            FileCall("read", @"C:\AI_Workbench\Workspace\game\car_racing.py"));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
    }

    [Fact]
    public void HasRepeatedToolCommand_allows_harness_edit_of_the_same_source_file()
    {
        var payload = PayloadWithAssistantCalls(
            FileCall("edit", @"C:\AI_Workbench\Workspace\game\car_racing.py"),
            FileCall("edit", @"C:\AI_Workbench\Workspace\game\car_racing.py"));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
    }

    [Fact]
    public void HasRepeatedToolCommand_allows_any_file_tool_on_the_same_path()
    {
        var payload = PayloadWithAssistantCalls(
            FileCall("apply_patch", @"C:\AI_Workbench\Workspace\game\car_racing.py"),
            FileCall("apply_patch", @"C:\AI_Workbench\Workspace\game\car_racing.py"));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
        Assert.True(LocalChatPayloadSignals.IsClosedLoopFingerprint(
            @"mystery_patch C:\AI_Workbench\Workspace\game\car_racing.py"));
    }

    [Fact]
    public void HasRepeatedToolCommand_allows_repeated_npm_test()
    {
        var payload = PayloadWithAssistantCalls(
            ShellCall("execute_command", "npm test"),
            ShellCall("execute_command", "  npm   test "));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
    }

    [Fact]
    public void HasRepeatedToolCommand_allows_test_edit_test_closed_loop()
    {
        var payload = PayloadWithAssistantCalls(
            ShellCall("execute_command", "npm test"),
            FileCall("replace_in_file", "src/app.ts"),
            ShellCall("execute_command", "npm test"));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
    }

    [Fact]
    public void HasRepeatedToolCommand_allows_read_replace_read_closed_loop()
    {
        var payload = PayloadWithAssistantCalls(
            FileCall("read_file", "src/app.ts"),
            FileCall("replace_in_file", "src/app.ts"),
            FileCall("read_file", "src/app.ts"));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
    }

    [Fact]
    public void HasRepeatedToolCommand_requires_the_last_two_shell_calls_to_match()
    {
        var payload = PayloadWithAssistantCalls(
            ShellCall("execute_command", "npm test"),
            ShellCall("execute_command", "npm test"),
            ShellCall("execute_command", "npm run build"));

        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
    }

    [Fact]
    public void HasRepeatedToolCommand_detects_xml_execute_command()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "run the tests" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "<execute_command>\n<command>dir</command>\n</execute_command>"
                },
                new JsonObject { ["role"] = "tool", ["content"] = "ok" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "<execute_command><command>dir</command></execute_command>"
                }
            }
        };

        Assert.True(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out var command));
        Assert.Equal("dir", command);
    }

    private static JsonObject PayloadWithAssistantCalls(params JsonObject[] calls)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "do the work" }
        };
        foreach (var call in calls)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray { call }
            });
            messages.Add(new JsonObject { ["role"] = "tool", ["content"] = "ok" });
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject ShellCall(string name, string command)
        => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = "{\"command\":\"" + command + "\"}"
            }
        };

    private static JsonObject FileCall(string name, string path)
        => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = "{\"path\":\"" + path + "\"}"
            }
        };
}
