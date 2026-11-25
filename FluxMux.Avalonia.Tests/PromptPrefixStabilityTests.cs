using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// llama-server reuses its prompt cache only up to the first token that differs from the
/// previous request. These tests replay a long Cline-style tool session through the real
/// Omit and Compact passes and measure how much of each forwarded prompt changes from the
/// turn before — the part llama-server has to read again.
/// </summary>
public sealed class PromptPrefixStabilityTests
{
    private const int DefaultContext = 65_536;

    [Fact]
    public void Forwarded_prompt_mostly_extends_the_previous_turn_in_a_long_tool_session()
    {
        var run = Replay(PortForwardingRules.Defaults);
        WriteReport(run);
        WriteReport(Replay(PortForwardingRules.Defaults, stableCut: false), "fluxmux-prefix-stability-oldcut.txt");

        // Once Omit and Compact are both active, a turn should usually reread only the new
        // tail, not the bulk of the prompt.
        Assert.True(run.Compacted > 10, "the session should reach compaction");
        Assert.True(run.RereadShareAfterCompact < 0.35,
            "after compaction, turns reread " + run.RereadShareAfterCompact.ToString("P0", CultureInfo.InvariantCulture) + " of the prompt");
    }

    [Fact]
    public void Omit_alone_keeps_the_prompt_start_stable_in_a_large_Context()
    {
        // The live case: 163840 Context, long read-heavy session, compaction never triggers.
        // The old Omit stubbed one more result every turn (similarity 0.13, ~39K tokens reread).
        var run = Replay(PortForwardingRules.Defaults, turns: 90, context: 163_840, clineReads: true);
        WriteReport(run, "fluxmux-prefix-stability-omit.txt");
        var old = Replay(PortForwardingRules.Defaults, turns: 90, stableCut: false, context: 163_840, clineReads: true);
        WriteReport(old, "fluxmux-prefix-stability-omit-old.txt");

        Assert.True(run.RereadShare < 0.15, "turns reread " + run.RereadShare.ToString("P0", CultureInfo.InvariantCulture));
        Assert.True(run.RereadShare < old.RereadShare / 2, "new " + run.RereadShare.ToString("P0", CultureInfo.InvariantCulture) + " vs old " + old.RereadShare.ToString("P0", CultureInfo.InvariantCulture));
    }

    internal sealed record ReplayResult(
        int Turns,
        int Compacted,
        long TotalChars,
        long RereadChars,
        long TotalCharsAfterCompact,
        long RereadCharsAfterCompact,
        List<string> Lines)
    {
        public double RereadShare => TotalChars == 0 ? 0 : (double)RereadChars / TotalChars;

        public double RereadShareAfterCompact => TotalCharsAfterCompact == 0 ? 0 : (double)RereadCharsAfterCompact / TotalCharsAfterCompact;
    }

    internal static ReplayResult Replay(PortForwardingRules rules, int turns = 90, bool stableCut = true, int context = DefaultContext, bool clineReads = false)
    {
        var history = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = Filler("You are Cline. Rules and tool docs. ", 40_000, 1) },
            new JsonObject { ["role"] = "user", ["content"] = "<task>Refactor the settings loader and add tests.</task>" }
        };

        string previous = string.Empty;
        long total = 0, reread = 0, totalAfter = 0, rereadAfter = 0;
        var compacted = 0;
        var lines = new List<string>();
        for (var t = 0; t < turns; t++)
        {
            var callId = "call_" + t.ToString(CultureInfo.InvariantCulture);
            history.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "Decision: read the next file. Step " + t.ToString(CultureInfo.InvariantCulture),
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = callId,
                        ["type"] = "function",
                        ["function"] = clineReads
                            ? new JsonObject { ["name"] = "read_files", ["arguments"] = "{\"paths\":[\"src/file" + t.ToString(CultureInfo.InvariantCulture) + ".cs\"]}" }
                            : new JsonObject { ["name"] = "read_file", ["arguments"] = "{\"path\":\"src/file" + t.ToString(CultureInfo.InvariantCulture) + ".cs\"}" }
                    }
                }
            });
            history.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = callId,
                ["content"] = Filler("// file " + t.ToString(CultureInfo.InvariantCulture) + " body. ", 6_000, t + 7)
            });

            var payload = new JsonObject { ["messages"] = history.DeepClone() };
            if (stableCut)
            {
                LocalToolResultClearing.ClearOlderResultsWhenFilling(payload, rules, context);
            }
            else
            {
                LocalToolResultClearing.ClearOlderResults(payload, rules);
            }

            var didCompact = false;
            var estimate = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
            if (estimate >= (int)(context * 0.8))
            {
                didCompact = stableCut
                    ? LocalHistoryCompaction.TryCompactPayloadStable(
                        payload,
                        rules,
                        trial => !LocalRequestOverlayRouting.PromptExceedsContext(trial, context, rules.CompactHeadroom))
                    : LocalHistoryCompaction.TryCompactPayload(payload, rules, force: true);
                compacted += didCompact ? 1 : 0;
            }

            var text = Render(payload);
            var common = CommonPrefix(previous, text);
            var changed = text.Length - common;
            total += text.Length;
            reread += changed;
            if (compacted > 0)
            {
                totalAfter += text.Length;
                rereadAfter += changed;
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{t,3} chars={text.Length,7} reread={changed,7} ({(text.Length == 0 ? 0 : (double)changed / text.Length),5:P0}){(didCompact ? " compact" : string.Empty)}"));
            previous = text;
        }

        return new ReplayResult(turns, compacted, total, reread, totalAfter, rereadAfter, lines);
    }

    internal static void WriteReport(ReplayResult run, string name = "fluxmux-prefix-stability.txt")
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"turns={run.Turns} compacted={run.Compacted} reread overall={run.RereadShare:P1} after first compact={run.RereadShareAfterCompact:P1} rereadChars={run.RereadChars} totalChars={run.TotalChars}"));
        foreach (var line in run.Lines)
        {
            sb.AppendLine(line);
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), name), sb.ToString());
    }

    // Approximates the chat template: role markers then content, in order.
    private static string Render(JsonObject payload)
    {
        var sb = new StringBuilder();
        foreach (var message in (payload["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            sb.Append("<|").Append(message["role"]?.ToString()).Append("|>");
            sb.Append(message["content"] is JsonValue v ? v.ToString() : message["content"]?.ToJsonString());
            if (message["tool_calls"] is JsonArray calls)
            {
                sb.Append(calls.ToJsonString());
            }

            sb.Append("<|end|>");
        }

        return sb.ToString();
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    private static string Filler(string seed, int chars, int salt)
    {
        var sb = new StringBuilder(chars + seed.Length);
        var k = salt;
        while (sb.Length < chars)
        {
            sb.Append(seed).Append(k.ToString(CultureInfo.InvariantCulture)).Append(' ');
            k = (k * 31 + 7) % 10_007;
        }

        return sb.ToString(0, chars);
    }
}
