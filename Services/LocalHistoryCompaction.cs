using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

public static class LocalHistoryCompaction
{
    public const double HighWatermark = 0.85;
    public const int KeepTurns = 8;
    public const int ToolKeepTurns = 16;
    public const int Headroom = 256;
    public const int PreservedUserChars = 8000;

    public static bool IsNearLimit(int promptTokens, int contextTokens)
        => IsNearLimit(promptTokens, contextTokens, PortForwardingRules.Defaults);

    public static bool IsNearLimit(int promptTokens, int contextTokens, PortForwardingRules rules)
    {
        if (contextTokens <= 0 || promptTokens <= 0)
        {
            return false;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var watermark = (int)Math.Floor(contextTokens * live.CompactWatermark);
        if (promptTokens < watermark)
        {
            return false;
        }

        return promptTokens + 1 + live.CompactHeadroom <= contextTokens;
    }

    public static bool ShouldForwardCompact(
        bool compactEnabled,
        int promptTokens,
        int contextTokens,
        bool promptExceeds,
        bool fillingEstimate = false)
        => ShouldForwardCompact(
            compactEnabled,
            promptTokens,
            contextTokens,
            promptExceeds,
            fillingEstimate,
            PortForwardingRules.Defaults);

    public static bool ShouldForwardCompact(
        bool compactEnabled,
        int promptTokens,
        int contextTokens,
        bool promptExceeds,
        bool fillingEstimate,
        PortForwardingRules rules)
    {
        if (!compactEnabled)
        {
            return false;
        }

        // The 2-chars/token "filling" estimate is deliberately pessimistic so a
        // code-heavy pack is compacted before llama-server 400s. But it doubles
        // the perceived token count for dense tokenizers (Qwen, ~3-4 chars/token),
        // so on its own it would trip compaction at roughly half the real context
        // fill. Gate it: only let the filling estimate fire once the standard
        // 4-chars/token estimate is already in the filling zone (>= 60% of the
        // window). Below that the window is not actually filling, so the safe
        // promptExceeds / IsNearLimit (85% watermark) conditions decide instead.
        var fillingZone = contextTokens > 0 && promptTokens * 5 >= contextTokens * 3;
        return promptExceeds || (fillingEstimate && fillingZone) || IsNearLimit(promptTokens, contextTokens, rules);
    }

    public static bool IsDestinationTight(int destinationContext, int turnTokens)
        => IsDestinationTight(destinationContext, turnTokens, PortForwardingRules.Defaults);

    public static bool IsDestinationTight(int destinationContext, int turnTokens, PortForwardingRules rules)
    {
        if (destinationContext <= 0 || turnTokens <= 0)
        {
            return false;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        return destinationContext < turnTokens + live.CompactHeadroom;
    }

    public static bool TryCompactPayload(JsonObject payload, int keepTurns = KeepTurns, bool force = false)
        => TryCompactPayload(payload, PortForwardingRules.Defaults with { CompactKeepTurns = keepTurns }, force);

    public static bool TryCompactPayload(JsonObject payload, PortForwardingRules rules, bool force = false)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var compacted = CompactMessages(messages, rules, force);
        if (compacted is null)
        {
            return false;
        }

        payload["messages"] = compacted;
        return true;
    }

    public static JsonArray? CompactMessages(JsonArray messages, int keepTurns = KeepTurns, bool force = false)
        => CompactMessages(messages, PortForwardingRules.Defaults with { CompactKeepTurns = keepTurns }, force);

    /// <summary>
    /// Compacts with a cut point that moves in steps rather than one message per turn, so the
    /// forwarded prompt keeps the same start (system, summary, kept turns) for several turns
    /// and llama-server's prompt cache only rereads the newest turn. A Client app resends its
    /// whole chat every turn, so the cut is recomputed each time; with a one-message slide the
    /// summary and the first kept turn changed on every request and almost nothing after the
    /// system prompt could be reused. The stepped cut keeps up to one step more of recent turns,
    /// so <paramref name="fits"/> is checked and today's tighter cut is used if it does not fit.
    /// </summary>
    public static bool TryCompactPayloadStable(JsonObject payload, PortForwardingRules rules, Func<JsonObject, bool> fits)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var stable = CompactMessages(messages, rules, force: true, steppedCut: true);
        if (stable is not null)
        {
            var trial = new JsonObject { ["messages"] = stable };
            foreach (var (key, value) in payload)
            {
                if (key != "messages")
                {
                    trial[key] = value?.DeepClone();
                }
            }

            if (fits(trial))
            {
                payload["messages"] = trial["messages"]!.DeepClone();
                return true;
            }
        }

        return TryCompactPayload(payload, rules, force: true);
    }

    public static JsonArray? CompactMessages(JsonArray messages, PortForwardingRules rules, bool force = false)
        => CompactMessages(messages, rules, force, steppedCut: false);

    /// <summary>Messages a fill-based cut moves by at a time (see <see cref="TryCompactPayloadToFill"/>).</summary>
    public const int FillCutStep = 32;

    /// <summary>
    /// Compacts so the kept recent messages fill about <paramref name="targetShare"/> of the
    /// Context, instead of keeping a fixed number of messages. The keep count was the whole rule:
    /// the Live-Switch fidelity test (26 Nov) showed a ~197K-token agent chat cut to 9,282 tokens on
    /// a 163,840 Context (6 %), losing 7 of 10 planted facts, because "keep 6 turns" (16-24
    /// messages with tools) is a tiny slice of an agent chat where every tool call is a message.
    /// The keep count stays as a minimum. The cut start is rounded up to a multiple of
    /// <see cref="FillCutStep"/> messages, so it stays at the same message while that many new
    /// ones arrive and llama-server's prompt cache keeps the start of the prompt — the reason the
    /// stepped cut exists. <paramref name="tokenRatio"/> scales the chars / 4 estimate to the
    /// loaded model's real count (measured tokens / estimate for this request), which on a
    /// code-heavy chat was about 1.5. Falls back to <see cref="TryCompactPayloadStable"/>.
    /// </summary>
    public static bool TryCompactPayloadToFill(
        JsonObject payload,
        PortForwardingRules rules,
        int contextTokens,
        double targetShare,
        double tokenRatio,
        Func<JsonObject, bool> fits)
    {
        if (payload["messages"] is JsonArray messages && contextTokens > 0)
        {
            var start = FillCutStart(messages, rules, (int)(contextTokens * Math.Clamp(targetShare, 0.2, 0.9)), tokenRatio);
            if (start is int keepFrom)
            {
                var filled = CompactMessages(messages, rules, force: true, steppedCut: false, explicitStart: keepFrom);
                if (filled is not null)
                {
                    var trial = new JsonObject { ["messages"] = filled };
                    foreach (var (key, value) in payload)
                    {
                        if (key != "messages")
                        {
                            trial[key] = value?.DeepClone();
                        }
                    }

                    if (fits(trial))
                    {
                        payload["messages"] = trial["messages"]!.DeepClone();
                        return true;
                    }
                }
            }
        }

        return TryCompactPayloadStable(payload, rules, fits);
    }

    /// <summary>The first message to keep so the kept tail fits <paramref name="budgetTokens"/>,
    /// rounded up to a <see cref="FillCutStep"/> boundary; null when nothing useful can be cut.</summary>
    internal static int? FillCutStart(JsonArray messages, PortForwardingRules rules, int budgetTokens, double tokenRatio)
    {
        if (messages.Count < 4 || budgetTokens <= 0)
        {
            return null;
        }

        var ratio = Math.Clamp(tokenRatio <= 0 ? 1.0 : tokenRatio, 0.5, 3.0);
        double Tokens(JsonNode? node) => (node?.ToJsonString().Length ?? 0) / 4.0 * ratio;

        // The system prompt, the summary of the dropped part and the pinned user task are kept too.
        var used = messages.OfType<JsonObject>().Where(m => RoleOf(m) == "system").Sum(Tokens) + 2500;
        var start = messages.Count;
        for (var i = messages.Count - 1; i >= 1; i--)
        {
            var cost = Tokens(messages[i]);
            if (used + cost > budgetTokens)
            {
                break;
            }

            used += cost;
            start = i;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var minimumKept = Math.Max(1, Math.Min(live.CompactKeepTurns, messages.Count - 2));
        start = Math.Min(start, messages.Count - minimumKept);
        start = (int)Math.Ceiling(start / (double)FillCutStep) * FillCutStep;
        start = Math.Min(start, messages.Count - minimumKept);
        return start <= 1 ? null : start;
    }

    private static JsonArray? CompactMessages(JsonArray messages, PortForwardingRules rules, bool force, bool steppedCut, int? explicitStart = null)
    {
        if (messages.Count < 4)
        {
            return null;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var keepTurns = live.CompactKeepTurns;
        var toolHistory = HasToolHistory(messages);
        keepTurns = Math.Clamp(toolHistory ? Math.Max(keepTurns, live.CompactToolKeepTurns) : keepTurns, 6, 24);
        keepTurns = Math.Min(keepTurns, Math.Max(live.CompactKeepTurns, messages.Count / 2));
        var conversational = 0;
        foreach (var node in messages.OfType<JsonObject>())
        {
            var role = RoleOf(node);
            if (role is "user" or "assistant")
            {
                conversational++;
            }
        }

        if (!force && (messages.Count < 10 || conversational < 6))
        {
            return null;
        }

        var split = Math.Min(keepTurns, Math.Max(1, messages.Count - 1));
        var rawStart = messages.Count - split;
        if (steppedCut && split > 1)
        {
            // Round the cut down to a multiple of the keep count: it then stays at the same
            // message while the next `split` messages arrive, and the kept tail runs from
            // `split` up to just under twice that.
            rawStart = Math.Max(1, rawStart / split * split);
        }

        if (explicitStart is int requested)
        {
            rawStart = requested;
        }

        var keepStart = AlignKeepStart(messages, rawStart);
        if (keepStart < 1)
        {
            return null;
        }

        var lastRealUserIndex = IndexOfLastRealUser(messages);
        JsonObject? preservedUser = null;
        if (lastRealUserIndex >= 0 && lastRealUserIndex < keepStart)
        {
            preservedUser = PreserveUserTask(messages[lastRealUserIndex] as JsonObject, live.CompactPreservedUserChars);
        }

        // The opening request is pinned too. It usually states the task and its fixed facts (names,
        // targets, constraints), and it never changes, so pinning it costs no prompt-cache reuse.
        // Only the newest user task used to be kept: the Live-Switch test (26 Nov) lost the codename
        // given in the opening request every time a chat was compacted.
        var firstRealUserIndex = IndexOfFirstRealUser(messages);
        JsonObject? preservedOpening = null;
        if (firstRealUserIndex >= 0 && firstRealUserIndex < keepStart && firstRealUserIndex != lastRealUserIndex)
        {
            // A chat that began from a pasted hand over brief keeps it whole: it is already a summary,
            // and cutting it at Preserved user chars threw away what it was written to keep.
            var openingText = ExtractText((messages[firstRealUserIndex] as JsonObject)?["content"]);
            var isBrief = ContextEngine.ConversationText.HumanPart(openingText).TrimStart().StartsWith(HandoverBrief.Title, StringComparison.Ordinal);
            preservedOpening = LabelOpening(PreserveUserTask(
                messages[firstRealUserIndex] as JsonObject,
                isBrief ? Math.Max(live.CompactPreservedUserChars, PinnedBriefChars) : live.CompactPreservedUserChars));
        }

        var summaryText = BuildForwardSummary(messages, keepStart);
        if (string.IsNullOrWhiteSpace(summaryText) && force)
        {
            // Force-compact: even if there are no specific tool results, notes, or decisions to
            // summarize, produce a generic summary so the compact can proceed.
            summaryText = "Compressed prior conversation context (older turns):\n(older turns omitted)";
        }
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            return null;
        }

        if (!force && summaryText.Length < 180)
        {
            return null;
        }

        var compacted = new JsonArray();
        foreach (var node in messages.OfType<JsonObject>())
        {
            if (RoleOf(node) == "system")
            {
                compacted.Add(node.DeepClone());
            }
        }

        if (preservedOpening is not null)
        {
            compacted.Add(preservedOpening);
            compacted.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Noted the original request." });
        }

        compacted.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = summaryText
        });

        if (preservedUser is not null)
        {
            compacted.Add(preservedUser);
        }

        for (var i = keepStart; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject tail)
            {
                continue;
            }

            if (RoleOf(tail) == "system")
            {
                continue;
            }

            compacted.Add(tail.DeepClone());
        }

        if (compacted.Count >= messages.Count)
        {
            return null;
        }

        return compacted;
    }

    /// <summary>A pasted brief at the start of a chat is pinned up to this many characters.</summary>
    public const int PinnedBriefChars = 40000;

    /// <summary>Marks the pinned opening as the starting point, which later messages may have replaced.</summary>
    public const string OpeningLabel = "[Where this chat started. Later messages may have changed the task; where they do, follow them.]\n";

    /// <summary>
    /// Labels the pinned opening request (26 Nov). Pinned word for word ahead of everything else, it
    /// anchored a model to a goal the chat had long moved on from. The label never changes, so the
    /// prompt cache is unaffected.
    /// </summary>
    private static JsonObject? LabelOpening(JsonObject? opening)
    {
        switch (opening?["content"])
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                opening["content"] = OpeningLabel + text;
                break;
            case JsonArray parts:
                parts.Insert(0, new JsonObject { ["type"] = "text", ["text"] = OpeningLabel });
                break;
        }

        return opening;
    }

    private static bool HasToolHistory(JsonArray messages)
    {
        foreach (var node in messages.OfType<JsonObject>())
        {
            if (IsToolFollowUp(node) || node["tool_calls"] is JsonArray { Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    internal static int AlignKeepStart(JsonArray messages, int keepStart)
    {
        keepStart = Math.Clamp(keepStart, 1, Math.Max(1, messages.Count - 1));
        while (keepStart > 1 && IsToolFollowUp(messages[keepStart] as JsonObject))
        {
            keepStart--;
        }

        while (keepStart < messages.Count - 1 && IsToolFollowUp(messages[keepStart] as JsonObject))
        {
            keepStart++;
        }

        return keepStart;
    }

    internal static int IndexOfFirstRealUser(JsonArray messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is JsonObject message && IsRealUserTask(message))
            {
                return i;
            }
        }

        return -1;
    }

    internal static int IndexOfLastRealUser(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject message && IsRealUserTask(message))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The newest message with words the person wrote (not a Client app's hook or reminder text).</summary>
    internal static int IndexOfLastPersonMessage(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject message && IsRealUserTask(message)
                && ContextEngine.ConversationText.HumanPart(ExtractText(message["content"])).Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    // A tool the model calls to ask the person something: Cline's ask_question and
    // ask_followup_question, and the like in other Client apps (ask_user, question, request_input).
    private static readonly Regex QuestionToolName = new(
        @"(^|_)ask($|_)|question|follow_?up|request_?input|ask_?user", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The questions the model asked the person through a question tool, by call id. The person's
    /// answer comes back as that tool's result, not as a user message (27 Nov: a brief listed only
    /// the hook note Cline adds after it, "The user has answered the question", and lost the
    /// answer, "this took something like 10 minutes").
    /// </summary>
    internal static Dictionary<string, string> QuestionCalls(JsonArray messages)
    {
        var questions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["tool_calls"] is not JsonArray calls)
            {
                continue;
            }

            foreach (var call in calls.OfType<JsonObject>())
            {
                var name = call["function"]?["name"]?.ToString() ?? call["name"]?.ToString() ?? string.Empty;
                if (call["id"]?.ToString() is not { Length: > 0 } id || !QuestionToolName.IsMatch(name))
                {
                    continue;
                }

                var args = call["function"]?["arguments"] ?? call["arguments"];
                JsonNode? parsed = args;
                if (args is JsonValue raw && raw.TryGetValue<string>(out var json))
                {
                    try
                    {
                        parsed = JsonNode.Parse(json);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        parsed = null;
                    }
                }

                questions[id] = CollapseWhitespace((parsed as JsonObject)?["question"]?.ToString() ?? string.Empty);
            }
        }

        return questions;
    }

    /// <summary>
    /// What the person said in [<paramref name="from"/>, <paramref name="to"/>), oldest first: their
    /// own messages (Client-app text removed) and their answers to the model's questions, with the
    /// question asked. Shared by compaction's summary and the hand over brief.
    /// </summary>
    internal static IEnumerable<(int Index, string Text, string? Question)> PersonMessages(JsonArray messages, int from, int to)
    {
        var questions = QuestionCalls(messages);
        for (var i = Math.Max(0, from); i < Math.Min(to, messages.Count); i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            if (IsToolFollowUp(message))
            {
                if (message["tool_call_id"]?.ToString() is { Length: > 0 } id && questions.TryGetValue(id, out var question))
                {
                    var answer = ToolResultBody(ExtractText(message["content"])).Trim();
                    var human = ContextEngine.ConversationText.HumanPart(answer);
                    if ((human.Length > 0 ? human : answer) is { Length: > 0 } said)
                    {
                        yield return (i, said, question);
                    }
                }

                continue;
            }

            if (!IsRealUserTask(message))
            {
                continue;
            }

            var text = ContextEngine.ConversationText.HumanPart(ExtractText(message["content"])).Trim();
            if (text.Length > 0 && !ContextEngine.ConversationText.IsAiFluxMuxNotice(text))
            {
                yield return (i, text, null);
            }
        }
    }

    internal static string? BuildForwardSummary(JsonArray messages, int keepStart, int maxNotes = MaxDroppedNotes, int maxChars = MaxSummaryChars)
    {
        var lines = new List<string>
        {
            "Compressed prior conversation context (older turns):",
            // Tasks change direction and decisions get reversed (26 Nov), so the summary says which
            // word wins rather than presenting every note as still true.
            HandoverBrief.LatestWinsRule,
        };
        var alreadyRan = LocalToolResultClearing.CollectAlreadyRanLines(messages, keepStart);
        if (alreadyRan.Count > 0)
        {
            lines.Add(LocalToolResultClearing.AlreadyRanHeader);
            lines.AddRange(alreadyRan);
        }

        // A tool ledger already names dropped commands. Do not also paste truncated file bodies —
        // that is how Compact taught the model to re-run the same read/shell call. Single lines that
        // state a value (a setting, a commit, a test result) are different: they are what the work
        // found, and without them the model has to re-run the call to know it.
        var facts = CollectToolResultFacts(messages, keepStart);
        if (facts.Count > 0)
        {
            lines.Add("Values found in older tool output (oldest first; a later value replaces an earlier one):");
            lines.AddRange(facts);
        }

        // The person's earlier messages are kept in every chat. They used to be summarised only
        // when there was no tool ledger, so in any agent chat a requirement or a ruled-out option
        // the person gave disappeared with the turn it was in (Live-Switch test, 26 Nov).
        var notes = CollectDroppedNotes(messages, keepStart, maxNotes);
        if (notes.Count > 0)
        {
            lines.Add("Earlier messages from the person (oldest first; later ones override earlier ones):");
            lines.AddRange(notes);
        }

        // Industry best practice: preserve conclusions and decisions, not
        // the reasoning process. The model doesn't need to re-derive WHY it
        // chose JWT over sessions — it needs to know THAT it chose JWT and
        // that sessions were ruled out. The reasoning is expendable; the
        // decision is not.
        var decisions = CollectAssistantDecisions(messages, keepStart);
        if (decisions.Count > 0)
        {
            // The model's conclusions are notes, not facts: a brief once listed "a brand-new NVIDIA
            // driver" as the cause the person had already ruled out, then said to treat it as settled
            // (27 Nov). An objection the person made straight after is attached to the conclusion.
            lines.Add("What the model concluded (oldest first; it may have been wrong, and the person's messages override it):");
            lines.AddRange(decisions);
        }

        if (lines.Count <= 1)
        {
            return null;
        }

        var summaryText = string.Join("\n", lines);
        return summaryText.Length > maxChars ? summaryText[..maxChars] : summaryText;
    }

    /// <summary>
    /// Extracts the assistant's conclusions and decisions from dropped turns.
    /// Industry best practice: keep conclusions, drop thinking. The model
    /// doesn't need the 20 pages of reasoning that got it to "use JWT" —
    /// it needs to know that JWT was chosen and why sessions were ruled out.
    /// </summary>
    private const int MaxDecisions = 12;

    // The first twelve assistant texts used to be taken as they came: a repeated closing line ("Done
    // with this part.", "Checking export run 4.") filled the list, and a decision after the first
    // dozen ("client.py sets the retry budget to 1375 ms") never appeared (Harness lab, 26 Nov). Now a
    // text repeated with only its numbers changed is kept once, and over the cap the most
    // informative are kept, in chat order.
    private static List<string> CollectAssistantDecisions(JsonArray messages, int keepStart)
    {
        var found = new List<(int Index, int Score, string Line)>();
        var shapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, line) in AssistantDecisionLines(messages, keepStart))
        {
            if (shapes.Add(Digits.Replace(line.ToLowerInvariant(), "#")))
            {
                found.Add((index, InformationScore(line), line));
            }
        }

        var kept = found.Count <= MaxDecisions
            ? found
            : found.OrderByDescending(d => d.Score).ThenByDescending(d => d.Index).Take(MaxDecisions).OrderBy(d => d.Index).ToList();
        var person = PersonMessages(messages, 0, messages.Count).ToList();
        return kept.Select(d => d.Line + Objection(person, d.Index)).ToList();
    }

    // The person disagreeing: "no the slowdown predates the driver update", "that's not it", "stop".
    private static readonly Regex Disagreement = new(
        @"^\W*(no|nope|not|wrong|incorrect|stop|don'?t|isn'?t|it'?s not|that'?s not|that is not|you'?re wrong|you are wrong)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The person's next words after the conclusion at <paramref name="index"/>, when they disagree.</summary>
    private static string Objection(List<(int Index, string Text, string? Question)> person, int index)
    {
        var next = person.FirstOrDefault(p => p.Index > index);
        if (next.Text is null || !Disagreement.IsMatch(next.Text))
        {
            return string.Empty;
        }

        var said = CollapseWhitespace(next.Text);
        return " (the person replied: \"" + (said.Length > 200 ? said[..200].TrimEnd() + "…" : said) + "\")";
    }

    private static IEnumerable<(int Index, string Line)> AssistantDecisionLines(JsonArray messages, int keepStart)
    {
        for (var i = 0; i < Math.Min(keepStart, messages.Count); i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            if (RoleOf(message) != "assistant")
            {
                continue;
            }

            var raw = ExtractText(message["content"]);
            var text = CollapseWhitespace(raw);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Skip tool-call-only messages (no actual text content)
            if (text.Length < 20)
            {
                continue;
            }

            // Extract the first meaningful sentence or two — this is where
            // the conclusion/decision lives. The rest is reasoning. Markdown bold and code blocks
            // are left out: cut into sentences they left stray "**" and half a C# block (27 Nov).
            text = CodeBlock.Replace(text, "(code)").Replace("**", string.Empty, StringComparison.Ordinal);
            var extracted = ExtractDecision(text);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                yield return (i, "- " + extracted);
            }
        }
    }

    /// <summary>
    /// Extracts the decision/conclusion from an assistant message.
    /// Looks for patterns like "I'll...", "I've...", "The...", "Decision:",
    /// "Plan:", "Summary:", "In summary", "To summarize", "The key...".
    /// Falls back to the first 200 chars if no pattern matches.
    /// </summary>
    private static string ExtractDecision(string text)
    {
        // Try to find explicit decision markers
        var markers = new[]
        {
            "Decision:", "Plan:", "Summary:", "In summary", "To summarize",
            "The key", "Key decision", "I'll", "I've", "I have",
            "I decided", "I chose", "I went with", "I'm using",
            "The approach", "The strategy", "The fix", "The solution",
            "Here's what", "Here is what", "What I did", "What I changed",
            "I updated", "I modified", "I added", "I removed", "I created",
            "I refactored", "I implemented", "I fixed", "I resolved",
            "The change", "The update", "The modification"
        };

        // Whole sentences only (27 Nov): a marker matched in the middle of a sentence ("…is the key.
        // Let me…") and the cut at 250 characters gave fragments such as "the key. Let me analyze it
        // and check the" that said nothing. The decision now starts at the start of the sentence
        // holding the marker and ends at the end of a sentence.
        foreach (var marker in markers)
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var start = SentenceStart(text, idx);
                // A marker inside a sentence counts only when the sentence begins with it.
                if (start == idx || string.Equals(marker, "Decision:", StringComparison.OrdinalIgnoreCase))
                {
                    return Sentences(text, start, DecisionChars);
                }

                idx = text.IndexOf(marker, idx + marker.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        // No marker: the opening sentences.
        return Sentences(text, 0, DecisionChars);
    }

    private const int DecisionChars = 300;

    private static readonly Regex CodeBlock = new("```.*?(```|$)", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SentenceEnd = new(@"[.!?](?=\s|$)", RegexOptions.Compiled);

    /// <summary>Where the sentence holding <paramref name="index"/> starts (after ". ", ": ", "## ", "**" and the like).</summary>
    private static int SentenceStart(string text, int index)
    {
        // The text is whitespace-collapsed, so a sentence starts after "<end mark> " or at 0.
        var i = index;
        while (i > 0 && !(text[i - 1] == ' ' && i >= 2 && text[i - 2] is '.' or '!' or '?' or ':'))
        {
            i--;
        }

        // Markdown that opened the line ("## Root cause", "**Fix**", "- item") is not the sentence.
        while (i < index && text[i] is ' ' or '#' or '*' or '-' or '>')
        {
            i++;
        }

        return i;
    }

    /// <summary>Whole sentences from <paramref name="start"/>, up to about <paramref name="max"/> characters; one long sentence is cut at a word, marked with "…".</summary>
    private static string Sentences(string text, int start, int max)
    {
        var end = start;
        foreach (Match m in SentenceEnd.Matches(text, start))
        {
            if (m.Index + 1 - start > max)
            {
                break;
            }

            end = m.Index + 1;
        }

        if (end > start)
        {
            return text[start..end].Trim().TrimStart('#', '*', ' ');
        }

        var rest = text[start..];
        if (rest.Length <= max)
        {
            return rest.Trim();
        }

        var cut = rest.LastIndexOf(' ', max);
        return rest[..(cut > max / 2 ? cut : max)].Trim() + "…";
    }

    /// <summary>The summary's length cap: small next to the room the fill-based cut leaves.</summary>
    public const int MaxSummaryChars = 8000;

    /// <summary>How many of the person's earlier messages compaction's summary lists.</summary>
    public const int MaxDroppedNotes = 20;

    /// <summary>Of those, how many are simply the newest, kept whatever their score.</summary>
    public const int NewestDroppedNotes = 8;

    /// <summary>
    /// The person's dropped messages: the most informative are chosen (names, numbers, paths and
    /// codes count) and then listed in chat order. "Carry on" style messages carry nothing to keep;
    /// the first eight messages of any kind used to be all that was kept. A message repeated with
    /// only its numbers changed ("Carry on with the next module (12).") is boilerplate: only the
    /// newest copy is a candidate, and it scores nothing, because 40 such copies — each with one
    /// digit — outscored a plain-words requirement ("Ondrea Vatsk will review the change") and
    /// pushed it out of the hand over brief (26 Nov).
    /// </summary>
    private static List<string> CollectDroppedNotes(JsonArray messages, int keepStart, int maxNotes = MaxDroppedNotes)
    {
        var candidates = new List<(int Index, int Score, string Text, string Shape)>();
        // Only what the person wrote: a Client app's own text in a user message (Cline's
        // environment_details file lists, tool results, resume prompts) would otherwise score
        // as "informative" for its paths and crowd out the person's own words. Their answers to
        // the model's questions are their words too.
        foreach (var (i, said, question) in PersonMessages(messages, 0, keepStart))
        {
            var text = CollapseWhitespace(said);
            if (text.Length > 200)
            {
                text = text[..200] + "…";
            }

            var shown = question is null ? text : "(answering \"" + Shorten(question, 100) + "\") " + text;
            candidates.Add((i, InformationScore(text), shown, Digits.Replace(text.ToLowerInvariant(), "#")));
        }

        var repeated = candidates.GroupBy(c => c.Shape).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var pool = candidates
            .Where(c => !repeated.Contains(c.Shape) || c.Index == candidates.Last(o => o.Shape == c.Shape).Index)
            .Select(c => repeated.Contains(c.Shape) ? c with { Score = 0 } : c)
            .ToList();
        // The newest dropped messages are kept whatever their score: in a task that changes
        // direction they are the latest word, and informativeness alone picked older, wordier
        // messages over a short "no, do it the other way" (26 Nov).
        var newest = pool.Where(c => c.Score > 0 || !repeated.Contains(c.Shape)).OrderByDescending(c => c.Index).Take(Math.Min(NewestDroppedNotes, maxNotes)).ToList();
        var informative = pool.Except(newest).OrderByDescending(c => c.Score).ThenByDescending(c => c.Index).Take(Math.Max(0, maxNotes - newest.Count));
        return newest.Concat(informative)
            .OrderBy(c => c.Index)
            .Select(c => "- user: " + c.Text)
            .ToList();
    }

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// Words that carry a specific value: with digits, inner capitals, or - _ . / in them, and
    /// names — a capitalised word that does not start a sentence ("Ondrea Vatsk"), "I" aside.
    /// </summary>
    private static int InformationScore(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var score = 0;
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var bare = w.Trim('.', ',', ';', ':', '(', ')', '"', '\'', '!', '?');
            var sentenceStart = i == 0 || words[i - 1].EndsWith('.') || words[i - 1].EndsWith('!') || words[i - 1].EndsWith('?') || words[i - 1].EndsWith(':');
            if (w.Any(char.IsDigit)
                || w.Skip(1).Any(char.IsUpper)
                || bare.IndexOfAny(['-', '_', '/', '.']) > 0
                || (!sentenceStart && bare.Length > 1 && char.IsUpper(bare[0])))
            {
                score++;
            }
        }

        return score;
    }

    // A setting with a literal value (metrics_port = 47193), not code (total = sum(...)).
    private static readonly Regex SettingLine = new(
        "^\\s*[A-Za-z][\\w.\\-]*\\s*[=:]\\s*(\"[^\"]{1,80}\"|'[^']{1,80}'|-?\\d[\\d.,_]*\\s*\\w{0,4}|true|false|[\\w.\\-/:]{1,60})\\s*$",
        RegexOptions.Compiled);

    // A file read's line number, as tools print it: Harness "4: ", "     4<tab>", "4→". Left on, it
    // kept a setting from matching (Harness lab, 26 Nov: "4: metrics_port = 47193" was not a value).
    private static readonly Regex LineNumberPrefix = new(@"^\s*\d{1,6}(?::\s?|\t|→)", RegexOptions.Compiled);

    private static readonly Regex EchoedHeading = new(@"^\s*(={3,}|-{3,}|#{2,}|\*{3,}).*?(={3,}|-{3,}|\*{3,})?\s*$", RegexOptions.Compiled);

    // A line of git log output: a hash, then the subject.
    private static readonly Regex CommitLine = new("^\\s*[0-9a-f]{7,40}\\s+\\S", RegexOptions.Compiled);

    // A test, build or error outcome.
    private static readonly Regex OutcomeLine = new(
        "\\b(flaky|failed|failure|error|passed|warning|exception|traceback)\\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The call's target: a path or command, or the first entry of Cline 4.x's lists
    // (run_commands {"commands": ["…"]}, read_files {"paths": ["…"]}).
    private static readonly Regex CallTarget = new(
        // The first 80 characters, whatever the length: requiring the closing quote within 80 left
        // every longer command unnamed ("run_commands: cache-reuse: False", 27 Nov).
        "\"(?:path|file_path|command|target|commands|paths)\"\\s*:\\s*\\[?\\s*\"((?:[^\"\\\\]|\\\\.){1,80})", RegexOptions.Compiled);

    /// <summary>
    /// The output text of a tool result. Cline 4.x sends a command's output as the JSON of its
    /// result list (<c>[{"query","result","error"}]</c>), so the whole output was one line with
    /// escaped line breaks and no setting in it could match a line: a Cline chat's config read
    /// (<c>metrics_port = 47193</c>) was dropped from every summary (26 Nov, Hail Mary fidelity
    /// test). The list's result and error texts are read instead; anything else is used as is.
    /// </summary>
    /// <summary>
    /// Up to <paramref name="max"/> lines of a tool result that state a value — a setting with a
    /// literal value, a git log line, a test or error outcome — with any line number a tool printed
    /// removed. Shared by compaction's summary and Omit's stubs, so a value survives either.
    /// </summary>
    internal static List<string> ValueLines(string toolResultText, int max = 3)
        => ToolResultBody(toolResultText).Split('\n')
            .Select(l => LineNumberPrefix.Replace(l, string.Empty).TrimEnd())
            .Where(l => l.Length is > 0 and <= 160)
            // A heading the command printed for itself ("=== Check for any warning about … ===") is
            // not a value, though "warning" in it matched as an outcome.
            .Where(l => !EchoedHeading.IsMatch(l))
            .Where(l => SettingLine.IsMatch(l) || CommitLine.IsMatch(l) || OutcomeLine.IsMatch(l))
            .Select(l => l.Trim())
            .Distinct()
            .Take(max)
            .ToList();

    // Terminal colour codes in command output (PowerShell's "\e[32;1mLineNumber\e[0m").
    private static readonly Regex AnsiCode = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    internal static string ToolResultBody(string text)
        => AnsiCode.Replace(ToolResultJsonBody(text), string.Empty);

    private static string ToolResultJsonBody(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('[') && !trimmed.StartsWith('{'))
        {
            return text;
        }

        try
        {
            var node = JsonNode.Parse(trimmed);
            var items = node is JsonArray list ? list.OfType<JsonObject>().ToList() : node is JsonObject one ? [one] : [];
            var parts = items
                .SelectMany(item => new[] { item["result"], item["error"] })
                .Select(value => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            return parts.Count > 0 ? string.Join("\n", parts!) : text;
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
    }

    private const int MaxToolFacts = 40;

    /// <summary>
    /// Lines from dropped tool results that state a value — a setting, a commit line, a test or
    /// error outcome — up to three per result, the newest results' values kept when there are too
    /// many. Each is labelled with the call that produced it.
    /// </summary>
    private static List<string> CollectToolResultFacts(JsonArray messages, int keepStart)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["tool_calls"] is not JsonArray calls)
            {
                continue;
            }

            foreach (var call in calls.OfType<JsonObject>())
            {
                var id = call["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                // Named as "Already ran" names it (a command by its echoed heading, unescaped), so
                // the two lists read alike; the raw JSON target gave labels such as
                // "run_commands Write-Host '=== Most recent launch args ==='; $lines = Get-Content C:\\AI_Workben".
                var described = LocalToolResultClearing.DescribeToolCall(call);
                if (described.Length == 0)
                {
                    var name = call["function"]?["name"]?.ToString() ?? "tool";
                    var target = CallTarget.Match(call["function"]?["arguments"]?.ToString() ?? string.Empty).Groups[1].Value;
                    described = string.IsNullOrWhiteSpace(target) ? name : name + " " + target;
                }

                callNames[id] = described;
            }
        }

        var perResult = new List<(string Source, List<string> Lines)>();
        for (var i = 0; i < keepStart; i++)
        {
            if (messages[i] is not JsonObject message || !IsToolFollowUp(message))
            {
                continue;
            }

            var text = ToolResultBody(ExtractText(message["content"]));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var source = callNames.TryGetValue(message["tool_call_id"]?.ToString() ?? string.Empty, out var named) ? named : "tool";
            var found = ValueLines(text);
            if (found.Count > 0)
            {
                perResult.Add((source, found));
            }
        }

        // Newest first, a value is kept once (27 Nov: a brief listed "cache-reuse: False" three
        // times, and "spec-type: False" above a later "spec-type: True" with nothing to say which
        // was current). A setting read again replaces the earlier reading of the same setting; a
        // call run again (the same command or path) replaces what its earlier run reported, so
        // an old build failure does not outlive the build that fixed it.
        var facts = new List<string>();
        var seenLines = new HashSet<string>(StringComparer.Ordinal);
        var seenSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        for (var r = perResult.Count - 1; r >= 0 && facts.Count < MaxToolFacts; r--)
        {
            var (source, lines) = perResult[r];
            if ((source.Contains(": ", StringComparison.Ordinal) || source.Contains(' ', StringComparison.Ordinal)) && !seenSources.Add(source))
            {
                continue;
            }

            var kept = new List<string>();
            foreach (var line in lines)
            {
                var setting = SettingLine.IsMatch(line) ? line.Split(['=', ':'], 2)[0].Trim() : null;
                if (!seenLines.Add(line) || (setting is not null && !seenSettings.Add(setting)))
                {
                    continue;
                }

                kept.Add("- " + source + ": " + line);
            }

            facts.InsertRange(0, kept.Take(MaxToolFacts - facts.Count));
        }

        return facts;
    }

    internal static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    internal static string CollapseWhitespace(string text)
        => string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static JsonObject? PreserveUserTask(JsonObject? message, int preservedUserChars = PreservedUserChars)
    {
        if (message is null || !IsRealUserTask(message))
        {
            return null;
        }

        var clone = message.DeepClone() as JsonObject;
        if (clone is null)
        {
            return null;
        }

        var keepChars = preservedUserChars > 0 ? preservedUserChars : PreservedUserChars;
        if (clone["content"] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.Length > keepChars)
        {
            clone["content"] = text[..keepChars];
        }

        return clone;
    }

    internal static bool IsRealUserTask(JsonObject message)
    {
        if (RoleOf(message) != "user" || IsToolFollowUp(message))
        {
            return false;
        }

        var text = ExtractText(message["content"]);
        return !text.StartsWith("Compressed prior conversation context", StringComparison.Ordinal);
    }

    internal static bool IsToolFollowUp(JsonObject? message)
    {
        if (message is null)
        {
            return false;
        }

        var role = RoleOf(message);
        if (role is "tool" or "function")
        {
            return true;
        }

        if (role != "user")
        {
            return false;
        }

        var text = ExtractText(message["content"]).TrimStart();
        return text.StartsWith("<tool_response>", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tool result", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tool output", StringComparison.OrdinalIgnoreCase);
    }

    internal static string RoleOf(JsonObject message)
        => (message["role"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

    internal static string ExtractText(JsonNode? content)
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
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonValue textValue)
                {
                    builder.Append(textValue);
                    continue;
                }

                if (part is not JsonObject obj)
                {
                    continue;
                }

                var text = obj["text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.Append(text);
                    builder.Append(' ');
                }
            }

            return builder.ToString().Trim();
        }

        if (content is JsonObject objContent)
        {
            return objContent["text"]?.ToString() ?? objContent.ToJsonString();
        }

        return content.ToString() ?? string.Empty;
    }
}