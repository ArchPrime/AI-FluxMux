using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// "Hail Mary" context salvage for the one failure forward-compaction cannot fix:
/// a single over-full turn (a huge embedded file/screenshot, a repeated tool loop,
/// a bloated tool result) that by itself pushes the prompt past the context window.
///
/// Forward compaction (<see cref="LocalHistoryCompaction"/>) keeps the most recent
/// turns verbatim and summarizes everything older. It has nothing older to summarize
/// when ONE message is the problem, so it bails out ("too few turns, or one over-full
/// turn"). Hail Mary is the sibling that shrinks the payload without a model round-trip:
/// a deterministic, non-AI prune pipeline that removes duplicates, collapses repeated
/// tool-call / read loops, truncates oversized tool results, and strips embedded
/// image blobs — in that order, stopping as soon as the prompt fits.
///
/// Stages 1-4 are non-destructive to the user's task and the conversation structure
/// (they only remove waste), so they may run automatically on a context-overflow 400.
/// Stage 5 (the blunt middle-cut of older turns) is destructive and stays behind the
/// operator's prompted button.
/// </summary>
public static class HailMaryCompaction
{
    /// <summary>
    /// A message that accounts for at least this fraction of the total prompt is the
    /// "over-full turn" Hail Mary targets. Below this, no single message dominates and
    /// the blunt middle-cut is the right tool, not surgical truncation.
    /// </summary>
    public const double DominantMessageFraction = 0.40;

    /// <summary>
    /// When a tool-call key repeats at least this many times, the middle repeats are
    /// pruned (first and last kept). Exact repeats are always pruned regardless of count.
    /// </summary>
    public const int LoopMiddlePruneThreshold = 3;

    /// <summary>
    /// Token cap for an older tool-result body. Head + tail are kept with an elision
    /// marker between. Reuses the gateway's 4-chars/token estimate.
    /// </summary>
    public const int ToolResultTokenCap = 1000;

    /// <summary>
    /// How many of the most recent tool results keep their full body even when the
    /// payload is over the window. Everything older is truncated to the cap.
    /// </summary>
    public const int KeepRecentResults = 8;

    /// <summary>
    /// How many of the most recent image-bearing turns keep their embedded image.
    /// Older images are stripped (the Client app still has them).
    /// </summary>
    public const int KeepRecentImages = 1;

    /// <summary>
    /// The blunt middle-cut keeps the most recent <c>keepTurns</c> messages verbatim and
    /// drops the older conversational turns in between. Tool-pair-safe: it never splits a
    /// tool result from its call, and it never drops the last real user task.
    /// </summary>
    public const int MiddleCutKeepTurns = 8;

    /// <summary>
    /// One-off aggression stops for the manual Hail Mary button. The button is a REAR-context
    /// prune: it drops older conversational turns so the full forward turn can complete with
    /// room to spare. Each stop trades "how much rear context to keep" against "how much
    /// headroom to leave for the forward turn". Always preserved regardless of stop: the
    /// system prompt, the last real user task, and the active tool-pair (tool-pair-safe).
    /// </summary>
    public enum HailMaryAggression
    {
        /// <summary>Keep ~8 recent rear turns; just fit under the window. "Nudge it under."</summary>
        Gentle,
        /// <summary>Keep ~4 recent rear turns; leave ~20% of the window as headroom.</summary>
        Balanced,
        /// <summary>Keep ~2 recent rear turns; leave ~40% of the window as headroom.</summary>
        Aggressive,
    }

    /// <summary>Rear turns kept for each stop.</summary>
    public static int KeepTurnsFor(HailMaryAggression stop) => stop switch
    {
        HailMaryAggression.Gentle => 8,
        HailMaryAggression.Balanced => 4,
        HailMaryAggression.Aggressive => 2,
        _ => MiddleCutKeepTurns,
    };

    /// <summary>Fraction of the context window to leave as headroom for the forward turn.</summary>
    public static double HeadroomFractionFor(HailMaryAggression stop) => stop switch
    {
        HailMaryAggression.Gentle => 0.0,
        HailMaryAggression.Balanced => 0.20,
        HailMaryAggression.Aggressive => 0.40,
        _ => 0.0,
    };

    /// <summary>
    /// Manual, one-off REAR-context prune for the Hail Mary button. Drops older conversational
    /// turns (the "rear") so the full forward turn can complete with the headroom the chosen
    /// stop requests. Unlike <see cref="Salvage"/> (a forward prune that shrinks the current
    /// payload stage-by-stage), this aims the middle-cut at a headroom target derived from the
    /// stop, and optionally strips older embedded images (independent of the stop).
    /// The system prompt, last real user task, and active tool-pair are always preserved.
    /// </summary>
    public static HailMaryResult RearContextPrune(
        JsonObject payload,
        int contextTokens,
        HailMaryAggression stop,
        bool stripImages,
        int? measuredTokens = null)
    {
        if (payload is null || contextTokens <= 0)
        {
            return HailMaryResult.None;
        }

        // Use the model's measured token count when available; the 4-chars/token estimate
        // over-counts for dense tokenizers (Qwen, ~3-4 chars/token), which made this prune a
        // no-op (before <= target) even when the prompt genuinely exceeded the window.
        var before = measuredTokens ?? LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var headroomFraction = HeadroomFractionFor(stop);
        var target = (int)(contextTokens * (1.0 - headroomFraction));
        if (before <= target)
        {
            return HailMaryResult.None;
        }

        var report = new HailMaryReport
        {
            BeforeTokens = before,
            TargetTokens = target,
        };

        var bestTokens = before;

        // Calibrate the estimate to the measured count. The raw 4-chars/token estimate
        // over-counts for dense tokenizers (Qwen, ~3-4 chars/token). We compute the ratio
        // of measured to estimated for the original payload, and use that ratio to scale
        // the estimate after each drop. This makes the loop's stop condition accurate.
        var rawEstimate = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var calibrationRatio = (measuredTokens is > 0 && rawEstimate > 0)
            ? (double)measuredTokens.Value / rawEstimate
            : 1.0;

        // Optional: strip older embedded images first (independent of the stop). Pure waste
        // removal — never touches the forward task or the most recent image.
        if (stripImages && StripOldImages(payload, report))
        {
            var t = (int)(LocalRequestOverlayRouting.EstimatePromptTokens(payload) * calibrationRatio);
            bestTokens = Math.Min(bestTokens, t);
            report.AfterTokens = t;
        }

        // The rear prune: drop older conversational turns until the prompt fits under the
        // headroom target, keeping at least KeepTurnsFor(stop) recent turns. Tool-pair-safe;
        // always keeps the system prompt + last real user task.
        var keepTurns = KeepTurnsFor(stop);
        if (MiddleCutOlderTurns(payload, report, target, keepTurns, calibrationRatio))
        {
            var t = (int)(LocalRequestOverlayRouting.EstimatePromptTokens(payload) * calibrationRatio);
            bestTokens = Math.Min(bestTokens, t);
            report.AfterTokens = t;
        }

        report.AfterTokens = bestTokens;
        return report.ToResult();
    }

    /// <summary>
    /// Runs the full Hail Mary pipeline on the payload, mutating it in place.
    /// Stages 1-4 run automatically; stage 5 (middle-cut) only runs when
    /// <paramref name="allowMiddleCut"/> is true (the operator's button).
    /// Stops as soon as the prompt fits under <paramref name="contextTokens"/>.
    /// Returns a report naming what was cut and the token delta.
    /// <paramref name="middleCutKeepTurnsOverride"/> lets the caller make the middle-cut more
    /// aggressive (keep fewer turns) when the prompt is clearly over the context window and the
    /// default keep-turns count is too conservative to free enough space.
    /// </summary>
    public static HailMaryResult Salvage(
        JsonObject payload,
        int contextTokens,
        int headroom = LocalHistoryCompaction.Headroom,
        bool allowMiddleCut = false,
        int middleCutKeepTurnsOverride = 0,
        bool stripImages = true)
    {
        if (payload is null || contextTokens <= 0)
        {
            return HailMaryResult.None;
        }

        var before = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var target = contextTokens - Math.Max(0, headroom);
        if (before <= target)
        {
            return HailMaryResult.None;
        }

        var report = new HailMaryReport
        {
            BeforeTokens = before,
            TargetTokens = target,
        };

        // Track the minimum token count reached across all stages. A stage can
        // temporarily add tokens (e.g. loop-collapse stubs a result), so the final
        // count is not always the best. We report the best (lowest) count achieved.
        var bestTokens = before;

        // Stage 1: dedupe identical messages (any role).
        if (DedupeIdenticalMessages(payload, report))
        {
            var t = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
            bestTokens = Math.Min(bestTokens, t);
            report.AfterTokens = t;
            if (t <= target)
            {
                return report.ToResult();
            }
        }

        // Stage 2: collapse repeated tool-call loops (command + read loops).
        if (CollapseToolLoops(payload, report))
        {
            var t = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
            bestTokens = Math.Min(bestTokens, t);
            report.AfterTokens = t;
            if (t <= target)
            {
                return report.ToResult();
            }
        }

        // Stage 3: truncate oversized tool-result bodies to the token cap.
        if (TruncateOversizedToolResults(payload, report))
        {
            var t = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
            bestTokens = Math.Min(bestTokens, t);
            report.AfterTokens = t;
            if (t <= target)
            {
                return report.ToResult();
            }
        }

        // Stage 4: strip embedded image blobs from all-but-the-most-recent turn.
        // Gated on stripImages: automatic paths (rejoin prune, compact fallback) keep images
        // so scenarios that compare one image with another are preserved; only the manual
        // Hail Mary button strips older images.
        if (stripImages && StripOldImages(payload, report))
        {
            var t = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
            bestTokens = Math.Min(bestTokens, t);
            report.AfterTokens = t;
            if (t <= target)
            {
                return report.ToResult();
            }
        }

        // Stage 5 (button-gated): blunt middle-cut of older turns, tool-pair-safe.
        // Token-driven: drops the oldest turns until the prompt fits under the target,
        // keeping at least minKeepTurns recent turns for conversational context.
        if (allowMiddleCut)
        {
            var minKeepTurns = middleCutKeepTurnsOverride > 0 ? middleCutKeepTurnsOverride : MiddleCutKeepTurns;
            if (MiddleCutOlderTurns(payload, report, target, minKeepTurns))
            {
                var t = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
                bestTokens = Math.Min(bestTokens, t);
                report.AfterTokens = t;
            }
        }

        // Report the best (lowest) token count achieved, not the final count.
        report.AfterTokens = bestTokens;
        return report.ToResult();
    }

    /// <summary>
    /// Stage 1: drop exact-duplicate messages. Two messages are duplicates when their
    /// role and full JSON content are identical (Cline retries, re-sent user text).
    /// The first occurrence is kept; later copies are removed.
    /// </summary>
    private static bool DedupeIdenticalMessages(JsonObject payload, HailMaryReport report)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            // Never dedupe the system prompt — it is structural, not noise.
            if (RoleOf(message) == "system")
            {
                continue;
            }

            var key = RoleOf(message) + "\u0001" + (message.ToJsonString() ?? string.Empty);
            if (seen.Contains(key))
            {
                removed.Add(i);
            }
            else
            {
                seen.Add(key);
            }
        }

        if (removed.Count == 0)
        {
            return false;
        }

        // Remove from the end so earlier indexes stay valid.
        foreach (var index in removed.OrderByDescending(x => x))
        {
            messages.RemoveAt(index);
        }

        report.DedupedMessages = removed.Count;
        report.Stages.Add("dedupe: removed " + removed.Count + " duplicate message(s)");
        return true;
    }

    /// <summary>
    /// Stage 2: collapse repeated tool-call loops. One detector keyed on the tool name:
    /// shell tools key on the command string; read tools key on (path, range). For each
    /// distinct key, the first occurrence is kept. Exact repeats are always pruned; when a
    /// key appears at least <see cref="LoopMiddlePruneThreshold"/> times, the middle repeats
    /// are pruned too (first and last kept). A pruned repeat's tool result is stubbed with
    /// an "already ran / re-read" line (option ii), consistent with the existing
    /// already-ran ledger, so the model knows the content exists earlier and does not loop
    /// into a fresh call.
    /// </summary>
    private static bool CollapseToolLoops(JsonObject payload, HailMaryReport report)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        // Collect every tool call with its key and the index of the assistant message
        // that issued it.
        var calls = new List<(int MessageIndex, int CallOrdinal, string Key, string Describe)>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject assistant
                || RoleOf(assistant) != "assistant"
                || assistant["tool_calls"] is not JsonArray toolCalls)
            {
                continue;
            }

            var ordinal = 0;
            foreach (var node in toolCalls.OfType<JsonObject>())
            {
                var key = LoopKey(node);
                if (!string.IsNullOrEmpty(key))
                {
                    calls.Add((i, ordinal, key, LocalToolResultClearing.DescribeToolCall(node)));
                }

                ordinal++;
            }
        }

        if (calls.Count == 0)
        {
            return false;
        }

        // Group by key, preserving first-seen order.
        var groups = new Dictionary<string, List<(int MessageIndex, int CallOrdinal, string Describe)>>();
        var order = new List<string>();
        foreach (var call in calls)
        {
            if (!groups.TryGetValue(call.Key, out var list))
            {
                list = new List<(int, int, string)>();
                groups[call.Key] = list;
                order.Add(call.Key);
            }

            list.Add((call.MessageIndex, call.CallOrdinal, call.Describe));
        }

        // Decide which (messageIndex, callOrdinal) pairs to prune.
        var prune = new HashSet<(int, int)>();
        foreach (var key in order)
        {
            var list = groups[key];
            if (list.Count < 2)
            {
                continue;
            }

            // Exact repeats: keep the first, prune the rest.
            for (var n = 1; n < list.Count; n++)
            {
                prune.Add((list[n].MessageIndex, list[n].CallOrdinal));
            }

            // Middle-prune for keys that repeat >= threshold: keep first AND last,
            // prune only the middle. (The exact-repeat pass above already pruned the
            // last; re-keep it when the key is a genuine loop.)
            if (list.Count >= LoopMiddlePruneThreshold)
            {
                prune.Remove((list[^1].MessageIndex, list[^1].CallOrdinal));
            }
        }

        if (prune.Count == 0)
        {
            return false;
        }

        // Remove the pruned tool calls from their assistant messages, and stub the
        // matching tool results.
        var stubbed = 0;
        foreach (var (messageIndex, callOrdinal) in prune)
        {
            if (messages[messageIndex] is not JsonObject assistant
                || assistant["tool_calls"] is not JsonArray toolCalls
                || callOrdinal >= toolCalls.Count)
            {
                continue;
            }

            var call = toolCalls[callOrdinal] as JsonObject;
            if (call is null)
            {
                continue;
            }

            var callId = call["id"]?.ToString();
            var describe = LocalToolResultClearing.DescribeToolCall(call);

            // Stub the matching tool result so the model knows it already ran / was re-read.
            if (StubMatchingResult(messages, messageIndex, callId, describe))
            {
                stubbed++;
            }

            toolCalls.RemoveAt(callOrdinal);
        }

        // Drop assistant messages that ended up with no tool calls and no content.
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject assistant
                && RoleOf(assistant) == "assistant"
                && assistant["tool_calls"] is JsonArray { Count: 0 }
                && string.IsNullOrWhiteSpace(ExtractText(assistant["content"])))
            {
                messages.RemoveAt(i);
            }
        }

        report.LoopsCollapsed = prune.Count;
        report.LoopResultsStubbed = stubbed;
        report.Stages.Add("loops: collapsed " + prune.Count + " repeated tool call(s), stubbed " + stubbed + " result(s)");
        return true;
    }

    /// <summary>
    /// Builds the loop key for a tool call. Shell tools key on the command; read tools key
    /// on (path, range). Returns empty for tools that are not loop-prunable.
    /// </summary>
    private static string LoopKey(JsonObject call)
    {
        var name = LocalSessionArtifactPolicy.ReadToolName(call);
        var args = call["function"]?["arguments"] ?? call["arguments"];

        if (IsShellToolName(name))
        {
            var command = ReadArgField(args, "command", "cmd", "input");
            return string.IsNullOrWhiteSpace(command) ? string.Empty : "shell\u0001" + command.Trim();
        }

        if (IsReadToolName(name))
        {
            var path = LocalSessionArtifactPolicy.ReadToolPath(call);
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var offset = ReadArgField(args, "start_line", "offset", "start");
            var limit = ReadArgField(args, "end_line", "limit", "count", "max_lines");
            return "read\u0001" + path.Trim() + "\u0001" + offset.Trim() + "\u0001" + limit.Trim();
        }

        return string.Empty;
    }

    private static bool IsShellToolName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Equals("execute_command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("run_command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("shell", StringComparison.OrdinalIgnoreCase)
            || value.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || value.Equals("command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("run_terminal_cmd", StringComparison.OrdinalIgnoreCase)
            || value.Equals("terminal", StringComparison.OrdinalIgnoreCase)
            || value.Equals("powershell", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReadToolName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Equals("read_file", StringComparison.OrdinalIgnoreCase)
            || value.Equals("read_files", StringComparison.OrdinalIgnoreCase)
            || value.Equals("read", StringComparison.OrdinalIgnoreCase)
            || value.Equals("view", StringComparison.OrdinalIgnoreCase)
            || value.Equals("view_file", StringComparison.OrdinalIgnoreCase)
            || value.Equals("open_file", StringComparison.OrdinalIgnoreCase)
            || value.Equals("cat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the tool result that matches the given call (by tool_call_id when present,
    /// otherwise the next tool-result message after the issuing assistant message) and
    /// replaces its body with an "already ran / re-read" stub. Returns true when a result
    /// was stubbed.
    /// </summary>
    private static bool StubMatchingResult(JsonArray messages, int assistantIndex, string? callId, string describe)
    {
        // Prefer an exact tool_call_id match.
        if (!string.IsNullOrWhiteSpace(callId))
        {
            for (var i = assistantIndex + 1; i < messages.Count; i++)
            {
                if (messages[i] is not JsonObject result
                    || !LocalToolResultClearing.IsToolResult(result))
                {
                    continue;
                }

                if (string.Equals(result["tool_call_id"]?.ToString(), callId, StringComparison.Ordinal))
                {
                    result["content"] = FormatLoopStub(describe);
                    return true;
                }
            }
        }

        // Fall back to the first tool-result message immediately after the assistant.
        for (var i = assistantIndex + 1; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject next)
            {
                continue;
            }

            if (LocalToolResultClearing.IsToolResult(next))
            {
                next["content"] = FormatLoopStub(describe);
                return true;
            }

            if (RoleOf(next) == "assistant")
            {
                break;
            }
        }

        return false;
    }

    private static string FormatLoopStub(string describe)
    {
        var detail = string.IsNullOrWhiteSpace(describe) ? string.Empty : " " + describe;
        var body = LocalToolResultClearing.OmittedMark
            + "; this call already ran earlier in the conversation" + detail
            + " — see the earlier result. Do not re-run it]";
        return "<tool_response>" + body + "</tool_response>";
    }

    /// <summary>
    /// Stage 3: truncate oversized tool-result bodies to <see cref="ToolResultTokenCap"/>
    /// tokens (head + tail with an elision marker). The most recent
    /// <see cref="KeepRecentResults"/> tool results keep their full body.
    /// </summary>
    private static bool TruncateOversizedToolResults(JsonObject payload, HailMaryReport report)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var resultIndexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is JsonObject msg && LocalToolResultClearing.IsToolResult(msg))
            {
                resultIndexes.Add(i);
            }
        }

        if (resultIndexes.Count <= KeepRecentResults)
        {
            return false;
        }

        var keepFrom = resultIndexes.Count - KeepRecentResults;
        var capChars = ToolResultTokenCap * 4; // 4-chars/token estimate.
        var truncated = 0;
        for (var n = 0; n < keepFrom; n++)
        {
            var index = resultIndexes[n];
            if (messages[index] is not JsonObject result)
            {
                continue;
            }

            var text = ExtractText(result["content"]);
            if (text.StartsWith(LocalToolResultClearing.OmittedMark, StringComparison.Ordinal))
            {
                continue; // already stubbed
            }

            if (text.Length <= capChars)
            {
                continue;
            }

            result["content"] = TruncateHeadTail(text, capChars);
            truncated++;
        }

        if (truncated == 0)
        {
            return false;
        }

        report.ResultsTruncated = truncated;
        report.Stages.Add("results: truncated " + truncated + " oversized tool result(s) to " + ToolResultTokenCap + " tokens");
        return true;
    }

    /// <summary>
    /// Stage 4: strip embedded image blobs from all-but-the-most-recent image-bearing
    /// turn. The most recent <see cref="KeepRecentImages"/> image(s) are kept; older ones
    /// are replaced with a text marker. The Client app still has the full image.
    /// </summary>
    private static bool StripOldImages(JsonObject payload, HailMaryReport report)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        // Find image-bearing messages from the end.
        var imageIndexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is JsonObject msg && HasImagePart(msg["content"]))
            {
                imageIndexes.Add(i);
            }
        }

        if (imageIndexes.Count <= KeepRecentImages)
        {
            return false;
        }

        var keepFrom = imageIndexes.Count - KeepRecentImages;
        var stripped = 0;
        for (var n = 0; n < keepFrom; n++)
        {
            var index = imageIndexes[n];
            if (messages[index] is not JsonObject msg)
            {
                continue;
            }

            msg["content"] = StripImagesFromContent(msg["content"]);
            stripped++;
        }

        if (stripped == 0)
        {
            return false;
        }

        report.ImagesStripped = stripped;
        report.Stages.Add("images: stripped " + stripped + " older embedded image(s), kept the most recent " + KeepRecentImages);
        return true;
    }

    /// <summary>
    /// Stage 5 (button-gated): the blunt middle-cut. Token-driven: keeps the system prompt,
    /// the last real user task, and as many of the most recent turns as needed for the prompt
    /// to fit under <paramref name="targetTokens"/>; drops the older conversational turns in
    /// between. Tool-pair-safe: it never splits a tool result from its call.
    ///
    /// Unlike a fixed keep-turns count, this scales with the chat length: in a 500-turn chat
    /// it drops hundreds of turns until the prompt fits; in a 12-turn chat it drops only what
    /// is needed. The <paramref name="minKeepTurns"/> floor guarantees at least that many
    /// recent turns are kept even if the prompt would fit with fewer (so the model still has
    /// some conversational context).
    /// </summary>
    private static bool MiddleCutOlderTurns(JsonObject payload, HailMaryReport report, int targetTokens, int minKeepTurns = MiddleCutKeepTurns, double calibrationRatio = 1.0)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var lastRealUser = IndexOfLastRealUser(messages);
        if (lastRealUser < 1)
        {
            return false;
        }

        // The oldest index we are allowed to drop. We never drop the system prompt (guarded by
        // role in the loop) or anything at/after the minimum keep-turns floor. Start by keeping
        // only the last real user task + any turns after it. Then walk backward from
        // lastRealUser - 1, dropping turns until the prompt fits under targetTokens, but keep
        // at least minKeepTurns recent turns.

        var minKeepStart = Math.Max(1, lastRealUser - minKeepTurns + 1);

        // Walk backward from lastRealUser - 1 down to minKeepStart, dropping turns as long as
        // the prompt still fits under targetTokens after the drop.
        var dropped = 0;
        for (var i = lastRealUser - 1; i >= minKeepStart; i--)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            // Keep the system prompt and any tool-result that belongs to a kept call.
            if (RoleOf(message) == "system")
            {
                continue;
            }

            if (LocalToolResultClearing.IsToolResult(message))
            {
                // Only drop a tool result if its matching call is also being dropped.
                var call = LocalToolResultClearing.FindMatchingCall(messages, i);
                if (call is not null)
                {
                    continue; // its call is kept, so keep the result
                }
            }

            // Drop this older turn and check if the prompt now fits. Use the calibrated
            // estimate (scaled to the measured count) rather than the raw 4-chars/token
            // estimate, which over-counts for dense tokenizers and would stop the loop
            // too early.
            messages.RemoveAt(i);
            dropped++;

            var est = (int)(LocalRequestOverlayRouting.EstimatePromptTokens(payload) * calibrationRatio);
            if (est <= targetTokens)
            {
                break;
            }
        }

        if (dropped == 0)
        {
            return false;
        }

        report.MiddleCutDropped = dropped;
        report.Stages.Add("middle-cut: dropped " + dropped + " older turn(s) to fit " + targetTokens + " tokens (kept the last real user task + " + (messages.Count - 1 - (IndexOfLastRealUser(messages) >= 0 ? IndexOfLastRealUser(messages) : 0)) + " most recent turns)");
        return true;
    }

    private static int AlignKeepStart(JsonArray messages, int keepStart)
    {
        keepStart = Math.Clamp(keepStart, 1, Math.Max(1, messages.Count - 1));
        while (keepStart > 1 && LocalToolResultClearing.IsToolResult(messages[keepStart] as JsonObject))
        {
            keepStart--;
        }

        while (keepStart < messages.Count - 1 && LocalToolResultClearing.IsToolResult(messages[keepStart] as JsonObject))
        {
            keepStart++;
        }

        return keepStart;
    }

    private static int IndexOfLastRealUser(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject message
                && RoleOf(message) == "user"
                && !LocalToolResultClearing.IsToolResult(message))
            {
                var text = ExtractText(message["content"]);
                if (!text.StartsWith("Compressed prior conversation context", StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string TruncateHeadTail(string text, int capChars)
    {
        if (text.Length <= capChars)
        {
            return text;
        }

        var half = capChars / 2;
        var head = text[..half];
        var tail = text[^half..];
        var elided = text.Length - head.Length - tail.Length;
        return head + "\n…[" + elided + " chars elided]…\n" + tail;
    }

    private static string StripImagesFromContent(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonArray parts)
        {
            var kept = new JsonArray();
            foreach (var part in parts)
            {
                if (part is JsonObject obj && HasImagePart(obj))
                {
                    kept.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "[image not forwarded to llama-server; the Client app still has it]"
                    });
                    continue;
                }

                kept.Add(part.DeepClone());
            }

            return kept.ToJsonString();
        }

        if (content is JsonValue value)
        {
            var text = value.ToString() ?? string.Empty;
            if (text.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("data:application/octet-stream;base64,", StringComparison.OrdinalIgnoreCase))
            {
                return "[image not forwarded to llama-server; the Client app still has it]";
            }

            return text;
        }

        return content.ToString() ?? string.Empty;
    }

    private static bool HasImagePart(JsonNode? node)
    {
        if (node is JsonObject part
            && (IsImageType(part["type"]?.ToString())
                || part["image_url"] is not null
                || (part["source"]?["media_type"]?.ToString() ?? string.Empty)
                    .StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (node is JsonArray items)
        {
            return items.Any(HasImagePart);
        }

        return false;
    }

    private static bool IsImageType(string? type)
        => !string.IsNullOrWhiteSpace(type)
           && (type.Equals("image", StringComparison.OrdinalIgnoreCase)
               || type.Equals("image_url", StringComparison.OrdinalIgnoreCase)
               || type.Contains("image", StringComparison.OrdinalIgnoreCase));

    private static string ReadArgField(JsonNode? args, params string[] keys)
    {
        if (args is null)
        {
            return string.Empty;
        }

        if (args is JsonObject obj)
        {
            return FirstNonEmpty(keys.Select(key => obj[key]?.ToString()).ToArray());
        }

        var text = args.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject parsed)
            {
                return FirstNonEmpty(keys.Select(key => parsed[key]?.ToString()).ToArray());
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string RoleOf(JsonObject message)
        => (message["role"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

    private static string ExtractText(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value)
        {
            return value.ToString() ?? string.Empty;
        }

        if (content is JsonArray parts)
        {
            var chunks = new List<string>();
            foreach (var part in parts.OfType<JsonObject>())
            {
                var text = part["text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    chunks.Add(text);
                }
            }

            return string.Join("\n", chunks);
        }

        return content.ToString() ?? string.Empty;
    }
}

/// <summary>
/// The outcome of a Hail Mary salvage: how much the prompt shrank and whether it now
/// fits. <see cref="None"/> is the sentinel for "nothing was cut / already fit".
/// </summary>
public sealed record HailMaryResult(
    int BeforeTokens,
    int AfterTokens,
    int TargetTokens,
    bool FitsNow,
    IReadOnlyList<string> Stages,
    int DedupedMessages = 0,
    int LoopsCollapsed = 0,
    int LoopResultsStubbed = 0,
    int ResultsTruncated = 0,
    int ImagesStripped = 0,
    int MiddleCutDropped = 0)
{
    public static readonly HailMaryResult None = new(0, 0, 0, false, new List<string>());

    public int TokensSaved => Math.Max(0, BeforeTokens - AfterTokens);

    /// <summary>
    /// A one-line Diagnostics summary naming what was cut and the token delta, per the
    /// Health/Diagnostics naming rule.
    /// </summary>
    public string DiagnosticsLine
    {
        get
        {
            if (Stages.Count == 0)
            {
                return "hail-mary: nothing to cut (prompt already fits or no waste found)";
            }

            return "hail-mary: " + string.Join("; ", Stages)
                   + " — " + BeforeTokens + " -> " + AfterTokens + " tokens (saved " + TokensSaved + ")"
                   + (FitsNow ? " — now fits" : " — still over, operator action needed");
        }
    }
}

/// <summary>
/// Mutable accumulator for a Hail Mary run. Each stage records what it cut; the final
/// <see cref="ToResult"/> snapshots the token delta and whether the prompt now fits.
/// </summary>
public sealed class HailMaryReport
{
    public int BeforeTokens { get; init; }
    public int TargetTokens { get; init; }
    public int AfterTokens { get; set; }

    public int DedupedMessages { get; set; }
    public int LoopsCollapsed { get; set; }
    public int LoopResultsStubbed { get; set; }
    public int ResultsTruncated { get; set; }
    public int ImagesStripped { get; set; }
    public int MiddleCutDropped { get; set; }

    public List<string> Stages { get; } = new();

    public HailMaryResult ToResult()
        => new(
            BeforeTokens: BeforeTokens,
            AfterTokens: AfterTokens,
            TargetTokens: TargetTokens,
            FitsNow: AfterTokens <= TargetTokens,
            Stages: Stages.ToList(),
            DedupedMessages: DedupedMessages,
            LoopsCollapsed: LoopsCollapsed,
            LoopResultsStubbed: LoopResultsStubbed,
            ResultsTruncated: ResultsTruncated,
            ImagesStripped: ImagesStripped,
            MiddleCutDropped: MiddleCutDropped);
}