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
            lines.Add("Values found in older tool output:");
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
            lines.Add("Key decisions:");
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
    private static List<string> CollectAssistantDecisions(JsonArray messages, int keepStart)
    {
        var decisions = new List<string>();
        for (var i = 0; i < keepStart && decisions.Count < 12; i++)
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
            // the conclusion/decision lives. The rest is reasoning.
            var extracted = ExtractDecision(text);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                decisions.Add("- " + extracted);
            }
        }

        return decisions;
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

        foreach (var marker in markers)
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                // Take from the marker to the next paragraph break or 250 chars
                var start = idx;
                var end = Math.Min(text.Length, start + 250);
                // Try to find a natural break (period + space, or newline)
                // Search from 50 chars before the cap, but never before the marker: a short
                // message ("I'll read it.") used to make this start index negative and throw.
                var periodIdx = text.IndexOf(". ", Math.Max(start, end - 50), StringComparison.Ordinal);
                if (periodIdx > start && periodIdx < end)
                {
                    end = periodIdx + 1;
                }
                return text[start..end].Trim();
            }
        }

        // No marker found — take the first 200 chars as a rough summary
        if (text.Length > 200)
        {
            // Try to break at a sentence boundary
            var periodIdx = text.IndexOf(". ", 100, StringComparison.Ordinal);
            if (periodIdx > 0 && periodIdx < 250)
            {
                return text[..(periodIdx + 1)].Trim();
            }
            return text[..200].Trim() + "…";
        }

        return text.Trim();
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
        for (var i = 0; i < keepStart; i++)
        {
            if (messages[i] is not JsonObject message || !IsRealUserTask(message))
            {
                continue;
            }

            // Only what the person wrote: a Client app's own text in a user message (Cline's
            // environment_details file lists, tool results, resume prompts) would otherwise score
            // as "informative" for its paths and crowd out the person's own words.
            var text = CollapseWhitespace(ContextEngine.ConversationText.HumanPart(ExtractText(message["content"])));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Length > 200)
            {
                text = text[..200] + "…";
            }

            candidates.Add((i, InformationScore(text), text, Digits.Replace(text.ToLowerInvariant(), "#")));
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

    // A line of git log output: a hash, then the subject.
    private static readonly Regex CommitLine = new("^\\s*[0-9a-f]{7,40}\\s+\\S", RegexOptions.Compiled);

    // A test, build or error outcome.
    private static readonly Regex OutcomeLine = new(
        "\\b(flaky|failed|failure|error|passed|warning|exception|traceback)\\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CallTarget = new(
        "\"(?:path|file_path|command|target)\"\\s*:\\s*\"([^\"]{1,80})\"", RegexOptions.Compiled);

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

                var name = call["function"]?["name"]?.ToString() ?? "tool";
                var target = CallTarget.Match(call["function"]?["arguments"]?.ToString() ?? string.Empty).Groups[1].Value;
                callNames[id] = string.IsNullOrWhiteSpace(target) ? name : name + " " + target;
            }
        }

        var perResult = new List<List<string>>();
        for (var i = 0; i < keepStart; i++)
        {
            if (messages[i] is not JsonObject message || !IsToolFollowUp(message))
            {
                continue;
            }

            var text = ExtractText(message["content"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var source = callNames.TryGetValue(message["tool_call_id"]?.ToString() ?? string.Empty, out var named) ? named : "tool";
            var found = text.Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.Length is > 0 and <= 160)
                .Where(l => SettingLine.IsMatch(l) || CommitLine.IsMatch(l) || OutcomeLine.IsMatch(l))
                .Distinct()
                .Take(3)
                .Select(l => "- " + source + ": " + l.Trim())
                .ToList();
            if (found.Count > 0)
            {
                perResult.Add(found);
            }
        }

        var facts = new List<string>();
        for (var r = perResult.Count - 1; r >= 0 && facts.Count < MaxToolFacts; r--)
        {
            facts.InsertRange(0, perResult[r].Take(MaxToolFacts - facts.Count));
        }

        return facts;
    }

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