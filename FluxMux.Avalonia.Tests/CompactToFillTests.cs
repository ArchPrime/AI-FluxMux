using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Compaction keeps recent messages up to a target share of the Context instead of a fixed
/// message count. The Live-Switch fidelity test (26 Nov) showed "keep 6 turns" cutting a ~197K
/// agent chat to 9,282 tokens on a 163,840 Context and losing 7 of 10 planted facts.
/// </summary>
public sealed class CompactToFillTests
{
    private static JsonObject AgentChat(int toolRounds, int charsPerResult = 4000)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding agent." },
            new JsonObject { ["role"] = "user", ["content"] = "Tidy the billing service." },
        };
        for (var i = 0; i < toolRounds; i++)
        {
            if (i % 4 == 0)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Carry on (" + i + ")." });
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "c" + i, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{\"path\":\"m" + i + ".py\"}" } } },
            });
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c" + i, ["content"] = new string((char)('a' + i % 26), charsPerResult) + i });
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static int Tokens(JsonObject payload) => LocalRequestOverlayRouting.EstimatePromptTokens(payload);

    private static bool Fits(JsonObject trial, int context) => Tokens(trial) + 257 <= context;

    [Fact]
    public void A_long_agent_chat_keeps_about_the_target_share_not_a_handful_of_messages()
    {
        var chat = AgentChat(200); // ~200K estimated tokens
        const int context = 163840;
        Assert.True(LocalHistoryCompaction.TryCompactPayloadToFill(chat, PortForwardingRules.Defaults, context, 0.6, 1.0, t => Fits(t, context)));
        var kept = Tokens(chat);
        // Up to 60 % of the Context, and far more than the old fixed cut (~5 % here).
        Assert.InRange(kept, (int)(context * 0.4), (int)(context * 0.6) + 2000);
    }

    [Fact]
    public void The_cut_stays_put_while_new_messages_arrive_so_the_prompt_cache_holds()
    {
        const int context = 163840;
        var starts = Enumerable.Range(0, 6).Select(extra =>
        {
            var chat = AgentChat(200 + extra);
            var messages = (JsonArray)chat["messages"]!;
            Assert.True(LocalHistoryCompaction.TryCompactPayloadToFill(chat, PortForwardingRules.Defaults, context, 0.6, 1.0, t => Fits(t, context)));
            // The first kept tool call after the summary identifies the cut.
            return chat["messages"]!.AsArray().OfType<JsonObject>().First(m => m["tool_calls"] is JsonArray)["tool_calls"]![0]!["id"]!.ToString();
        }).Distinct().Count();
        Assert.True(starts <= 2, "the cut moved on " + starts + " of 6 turns");
    }

    [Fact]
    public void The_measured_ratio_shrinks_the_kept_part_when_the_model_counts_more_tokens()
    {
        const int context = 163840;
        var plain = AgentChat(200);
        var dense = AgentChat(200);
        LocalHistoryCompaction.TryCompactPayloadToFill(plain, PortForwardingRules.Defaults, context, 0.6, 1.0, t => Fits(t, context));
        LocalHistoryCompaction.TryCompactPayloadToFill(dense, PortForwardingRules.Defaults, context, 0.6, 1.5, t => Tokens(t) * 1.5 + 257 <= context);
        Assert.True(Tokens(dense) * 1.5 <= context * 0.6 + 3000, "the dense chat, counted as the model counts it, must still be near the target");
        Assert.True(Tokens(dense) < Tokens(plain));
    }

    [Fact]
    public void A_tiny_budget_still_keeps_the_minimum_recent_messages()
    {
        var chat = AgentChat(60, charsPerResult: 40000);
        var before = ((JsonArray)chat["messages"]!).Count;
        Assert.True(LocalHistoryCompaction.TryCompactPayloadToFill(chat, PortForwardingRules.Defaults, 16384, 0.6, 1.0, _ => true));
        var keptTail = ((JsonArray)chat["messages"]!).OfType<JsonObject>().Count(m => m["role"]?.ToString() is "tool" or "assistant");
        Assert.True(keptTail >= PortForwardingRules.Defaults.CompactKeepTurns / 2, "kept " + keptTail);
        Assert.True(((JsonArray)chat["messages"]!).Count < before);
    }
}
