using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluxMux.Avalonia.Services.ContextEngine;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// "Chat handover brief" (26 Nov): a text the person pastes as the first message
/// of a new chat — after a chat is stuck, before switching Client app or model, or to park a task
/// for later. It replaced Hail Mary's "Trimming it in AI-FluxMux", which cut only the one request it
/// forwarded: the Client app resent the whole chat on the next turn, so it could not reset a chat,
/// and the automatic compaction and 95 % last resort already did the rest of its job.
///
/// Built from the chat exactly as the Client app last sent it, with the same pieces compaction keeps
/// (so it shares that code and its tests): the opening request word for word, the summary of the
/// older part (commands run, values found in tool output, the person's earlier messages, decisions),
/// the files changed, the latest turns word for word, and the current task. It is not built from
/// VIBE_STATE: that note is captured a line at a time and can hold a stale task or a copy of the last
/// reply. File contents are left out on purpose — the files are on disk and the new chat rereads
/// them — and so is AI-FluxMux's own inserted text. Pure, so it is unit-tested against the
/// Live-Switch test's ten planted facts.
/// </summary>
public static class HandoverBrief
{
    /// <summary>Messages at the end of the chat that are quoted rather than summarised.</summary>
    public const int RecentMessages = 12;

    public const int OpeningChars = 4000;

    /// <summary>
    /// Hail Mary does not rebuild a chat whose newest message alone is over this share of the
    /// Context: "retry" would send it again and overflow again.
    /// </summary>
    public const double OversizedLastMessageShare = 0.25;

    public const int RecentMessageChars = 1500;
    public const int RecentToolResultChars = 800;
    public const int RecentTotalChars = 10000;
    public const int MaxFilesChanged = 40;

    // A brief is read once by a fresh chat, so it can list more of the person's messages than the
    // summary compaction repeats on every turn.
    public const int MaxPersonMessages = 60;
    public const int MaxSummaryChars = 20000;

    public const string Title = "# Hand over brief";

    private static readonly Regex EditToolName = new(
        "(write|edit|replace|create|apply|patch|insert|delete|rename|move)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A call that returns a file's contents, as DescribeToolCall names it ("read_file: src/a.py",
    // "run_command: cat src/a.py", "execute_command: Get-Content a.txt").
    private static readonly Regex FileReadCall = new(
        @"^(\w*(read|view|open)\w*\b|\w+:\s*(cat|type|head|tail|more|less|Get-Content|gc)\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] PathFields = ["path", "file_path", "file", "target_file", "filename"];

    /// <summary>
    /// The brief, or null when the chat holds nothing the person wrote. <paramref name="clientApp"/>
    /// names where it came from (null = not recognised); <paramref name="capturedUtc"/> is when the
    /// Client app sent it.
    ///
    /// Laid out for how tasks really go (26 Nov): the direction changes as the work goes, and
    /// decisions get reversed, so the brief leads with where things stand now (the latest turns word
    /// for word and the person's final message), then how we got here (the person's messages, oldest
    /// first, condensed), then the facts gathered, and only last where it started — labelled as a
    /// starting point later messages may have replaced. It says outright that later messages
    /// override earlier ones. Leading with the opening request anchored a new chat to a goal that
    /// could be long gone. A chat that itself began from a brief carries that brief forward: its
    /// starting point stays the starting point, and its history and facts join this one's, so a task
    /// handed over several times keeps its whole trajectory.
    ///
    /// The person's final message is either in full or left out (<paramref name="includeLastMessage"/>),
    /// never cut: a cut copy helps neither the new chat nor the person, who may want to edit and
    /// resend it. Whether to leave it out is the caller's (see <see cref="FinalMessageImpact"/>).
    /// </summary>
    public static string? Build(
        JsonArray? messages,
        string? clientApp,
        DateTime capturedUtc,
        bool includeLastMessage = true)
    {
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        if (first < 0)
        {
            return null;
        }

        var last = LocalHistoryCompaction.IndexOfLastRealUser(messages);
        var opening = HumanText(messages[first] as JsonObject);
        if (string.IsNullOrWhiteSpace(opening))
        {
            return null;
        }

        var earlier = EarlierBrief.Parse(opening);

        // The quoted tail; everything between the opening and it is condensed. A short chat is quoted whole.
        var keepStart = messages.Count - RecentMessages <= first + 1
            ? first + 1
            : LocalHistoryCompaction.AlignKeepStart(messages, messages.Count - RecentMessages);
        var lastIndex = last > first ? last : -1;

        var text = new StringBuilder();
        text.Append(Title).Append(" — ")
            .Append(capturedUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture))
            .Append(", from a ").Append(string.IsNullOrWhiteSpace(clientApp) ? "Client app" : clientApp).Append(" chat\n\n")
            .Append("This brief continues an earlier chat. AI-FluxMux wrote it from that chat. ")
            .Append(LatestWinsRule)
            .Append(" File contents are not in it; the files are on disk, so reread any you need before changing them.\n");

        text.Append("\n## Where things stand now\n");
        var recent = RecentLines(messages, keepStart, lastIndex);
        if (recent.Count > 0)
        {
            text.Append("### Latest turns (word for word)\n");
            foreach (var line in recent)
            {
                text.Append(line).Append('\n');
            }
        }

        var current = lastIndex >= 0 ? HumanText(messages[lastIndex] as JsonObject) : string.Empty;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var answered = messages.Skip(lastIndex + 1).OfType<JsonObject>().Any(m => LocalHistoryCompaction.RoleOf(m) == "assistant");
            text.Append(answered ? "### Your final message\n" : "### Your final message (not answered yet)\n");
            if (!includeLastMessage)
            {
                text.Append("Left out of this brief on purpose (" + current.Length.ToString("N0", CultureInfo.InvariantCulture)
                            + " characters): it may be what overflowed the earlier chat. Ask for it again, in a different form or in smaller parts, if it is still needed.\n");
            }
            else
            {
                text.Append(current).Append('\n').Append(ImageNote(messages[lastIndex] as JsonObject));
            }
        }

        var timeline = Timeline(messages, first + 1, keepStart, earlier?.History);
        if (timeline.Count > 0)
        {
            text.Append("\n## How we got here (your messages, oldest first; later ones override earlier ones)\n");
            foreach (var line in timeline)
            {
                text.Append(line).Append('\n');
            }
        }

        var facts = Facts(messages, keepStart, earlier?.Facts, MaxSummaryChars);
        if (facts.Length > 0)
        {
            text.Append("\n## Facts gathered along the way\n").Append(facts);
        }

        var earlierImages = messages.Take(keepStart).OfType<JsonObject>().Sum(ImageCount);
        if (earlierImages > 0)
        {
            text.Append("(" + earlierImages + (earlierImages == 1 ? " image was" : " images were")
                        + " attached earlier in the chat; a text brief cannot carry them, so attach any that still matter.)\n");
        }

        text.Append("\n## Where it started\n")
            .Append("The chat began with this request. Later messages may have changed the task; where they do, follow them.\n")
            .Append(Cap(earlier?.Started ?? opening, OpeningChars)).Append('\n');

        text.Append("\n## Continue\n")
            .Append("Carry on from where the latest turns stop, in the most recent direction. Treat the facts above as settled "
                    + "unless a later message or the files on disk say otherwise, and do not redo work already done.\n");
        return text.ToString();
    }

    /// <summary>Opening words of Hail Mary's rebuild summary; <see cref="HailMary.HailMaryVerification"/> looks for them.</summary>
    public const string RescueSummaryHeading = "Summary of the earlier part of this chat, written by AI-FluxMux (Hail Mary)";

    /// <summary>Stated in every brief and compaction summary: in a task that changes direction, the latest word wins.</summary>
    public const string LatestWinsRule = "Later messages override earlier ones: where anything below conflicts, follow the latest.";

    /// <summary>
    /// The summary Hail Mary writes in place of messages [0, <paramref name="keepStart"/>) when it
    /// rebuilds a stuck chat in the Client app's own files (26 Nov). The same shape as the brief,
    /// without "where things stand now": the messages from <paramref name="keepStart"/> on stay in
    /// the chat word for word and are that. Capped at <paramref name="maxChars"/>; the facts give
    /// way first, then the history's middle, the starting point last. Null when the chat holds
    /// nothing the person wrote.
    /// </summary>
    public static string? BuildRescueSummary(JsonArray? messages, int keepStart, int maxChars)
    {
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var opening = first >= 0 ? HumanText(messages[first] as JsonObject) : string.Empty;
        if (string.IsNullOrWhiteSpace(opening))
        {
            return null;
        }

        var earlier = EarlierBrief.Parse(opening);
        var head = RescueSummaryHeading + " to make room. "
                   + "The most recent messages follow word for word. " + LatestWinsRule
                   + " File contents are not in it; reread a file before changing it.\n";
        var started = "\n## Where it started\nThe chat began with this request. Later messages may have changed the task; where they do, follow them.\n"
                      + Cap(earlier?.Started ?? opening, Math.Min(OpeningChars, Math.Max(200, maxChars / 4))) + "\n";

        var room = maxChars - head.Length - started.Length;
        var history = new StringBuilder();
        var timeline = Timeline(messages, first + 1, keepStart, earlier?.History, Math.Max(0, room / 2));
        if (timeline.Count > 0)
        {
            history.Append("\n## How we got here (your messages, oldest first; later ones override earlier ones)\n");
            foreach (var line in timeline)
            {
                history.Append(line).Append('\n');
            }
        }

        var factsRoom = room - history.Length - 40;
        var facts = factsRoom > 200 ? Facts(messages, keepStart, earlier?.Facts, factsRoom) : string.Empty;
        var middle = facts.Length > 0 ? "\n## Facts gathered along the way\n" + facts : string.Empty;

        var text = head + history + middle + started;
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    /// <summary>
    /// AI-FluxMux's record of a chat for a Client app's own compaction (26 Nov): the person's
    /// messages in order, the values found in tool output, decisions and files changed, and where
    /// the chat started, from the whole chat as the app last sent it. A client's summariser sees
    /// only what the app chose to send it, often with tool output cut short, so values that lived
    /// there (a port in a config file, a flaky test, a commit hash) were lost: Cline's own
    /// compaction of the Live-Switch chat kept 7 of 10 facts. It goes into the app's summary
    /// request and onto the summary the app gets back (<see cref="HailMary.ClientCompactionRequest"/>
    /// adds the wording for each). Null when the chat holds nothing the person wrote.
    /// </summary>
    public static string? ClientSummaryRecord(JsonArray? messages, int maxChars = 8000)
    {
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var opening = first >= 0 ? HumanText(messages[first] as JsonObject) : string.Empty;

        // After a Client app's first compaction its chats start with the summary it kept, which ends
        // with AI-FluxMux's earlier record, and the opening request is gone from them. That record is
        // carried forward, so a chat compacted again keeps what the first compaction kept.
        var earlier = PreviousClientRecord(messages) ?? (string.IsNullOrWhiteSpace(opening) ? null : EarlierBrief.Parse(opening));
        var startedText = string.IsNullOrWhiteSpace(earlier?.Started) ? opening : earlier.Started;
        if (string.IsNullOrWhiteSpace(startedText) && earlier is null && first < 0)
        {
            return null;
        }

        var head = LatestWinsRule + "\n";
        var started = string.IsNullOrWhiteSpace(startedText)
            ? string.Empty
            : "\n## Where it started\n" + Cap(startedText, Math.Min(OpeningChars, Math.Max(200, maxChars / 5))) + "\n";
        var room = maxChars - head.Length - started.Length;
        var text = new StringBuilder(head);
        var timeline = Timeline(messages, first + 1, messages.Count, earlier?.History, Math.Max(0, room / 2));
        if (timeline.Count > 0)
        {
            text.Append("\n## The person's messages (oldest first; later ones override earlier ones)\n");
            foreach (var line in timeline)
            {
                text.Append(line).Append('\n');
            }
        }

        var factsRoom = maxChars - text.Length - started.Length - 40;
        var facts = factsRoom > 200 ? Facts(messages, messages.Count, earlier?.Facts, factsRoom) : string.Empty;
        if (facts.Length > 0)
        {
            text.Append("\n## Facts gathered along the way\n").Append(facts);
        }

        text.Append(started);
        var all = text.ToString();
        return all.Length <= maxChars ? all : all[..maxChars];
    }

    /// <summary>Heading that opens AI-FluxMux's record on a summary a Client app keeps.</summary>
    public const string ClientRecordHeading = "Record kept by AI-FluxMux from the whole conversation";

    /// <summary>The newest AI-FluxMux record found in the chat (on a summary the app kept), as an earlier brief.</summary>
    private static EarlierBrief? PreviousClientRecord(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var text = messages[i] is JsonObject message ? LocalHistoryCompaction.ExtractText(message["content"]) : string.Empty;
            var at = text.LastIndexOf(ClientRecordHeading, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var body = text[at..];
            var lineEnd = body.IndexOf('\n');
            body = lineEnd < 0 ? string.Empty : body[(lineEnd + 1)..];
            string? started = null, facts = null;
            var history = new List<string>();
            foreach (var section in ("\n" + body).Split("\n## "))
            {
                var nl = section.IndexOf('\n');
                if (nl < 0)
                {
                    continue;
                }

                var heading = section[..nl].Trim();
                var content = section[(nl + 1)..].Trim();
                if (heading.StartsWith("Where it started", StringComparison.Ordinal))
                {
                    started = content;
                }
                else if (heading.StartsWith("The person's messages", StringComparison.Ordinal))
                {
                    history.AddRange(content.Split('\n').Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)));
                }
                else if (heading.StartsWith("Facts gathered", StringComparison.Ordinal))
                {
                    facts = content;
                }
            }

            return new EarlierBrief(started ?? string.Empty, history, facts ?? string.Empty);
        }

        return null;
    }

    private const int TimelineEntryChars = 240;
    private const int TimelineChars = 8000;
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// The person's messages in [<paramref name="from"/>, <paramref name="to"/>), oldest first, each
    /// condensed to one line, after any carried from an earlier brief. A message repeated with only
    /// its numbers changed ("Carry on with module 12") is shown once with a count. Over the cap, the
    /// oldest few and the newest are kept, the middle marked as left out: the start and the latest
    /// direction both matter.
    /// </summary>
    private static List<string> Timeline(JsonArray messages, int from, int to, IReadOnlyList<string>? carried, int maxChars = TimelineChars)
    {
        var lines = new List<string>();
        if (carried is { Count: > 0 })
        {
            lines.Add("- (in the chat before this one:)");
            lines.AddRange(carried.Select(l => "  " + l.TrimStart()));
        }

        string? lastShape = null;
        var repeats = 0;
        for (var i = Math.Max(0, from); i < Math.Min(to, messages.Count); i++)
        {
            if (messages[i] is not JsonObject message || !LocalHistoryCompaction.IsRealUserTask(message))
            {
                continue;
            }

            var said = LocalHistoryCompaction.CollapseWhitespace(HumanText(message));
            if (said.Length == 0 || ConversationText.IsAiFluxMuxNotice(said))
            {
                continue;
            }

            var shape = Digits.Replace(said.ToLowerInvariant(), "#");
            if (shape == lastShape)
            {
                repeats++;
                lines[^1] = Regex.Replace(lines[^1], @" \(and \d+ more like it\)$", string.Empty) + " (and " + repeats + " more like it)";
                continue;
            }

            lastShape = shape;
            repeats = 0;
            lines.Add("- " + Cap(said, TimelineEntryChars) + (ImageNote(message) is { Length: > 0 } note ? " " + note.TrimEnd() : string.Empty));
        }

        if (lines.Sum(l => l.Length + 1) <= maxChars || lines.Count <= 6)
        {
            return lines;
        }

        const int head = 3;
        var tail = new List<string>();
        var used = lines.Take(head).Sum(l => l.Length + 1) + 60;
        for (var i = lines.Count - 1; i >= head && used + lines[i].Length + 1 <= maxChars; i--)
        {
            tail.Insert(0, lines[i]);
            used += lines[i].Length + 1;
        }

        var dropped = lines.Count - head - tail.Count;
        return dropped <= 0
            ? lines
            : [.. lines.Take(head), "- (… " + dropped + " messages in between left out …)", .. tail];
    }

    /// <summary>
    /// Commands run, values found in tool output and decisions for [0, <paramref name="keepStart"/>),
    /// from compaction's own summary without its notes on the person's messages (the history
    /// covers those), then the files changed, after any facts carried from an earlier brief.
    /// </summary>
    private static string Facts(JsonArray messages, int keepStart, string? carried, int maxChars)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(carried))
        {
            text.Append("From the chat before this one:\n").Append(Cap(carried.Trim(), Math.Max(200, maxChars / 3))).Append('\n');
        }

        var summary = keepStart > 1 ? LocalHistoryCompaction.BuildForwardSummary(messages, keepStart, maxNotes: 0, maxChars) : null;
        var body = summary?.Split('\n', 2) is [_, var rest] ? rest.Trim() : string.Empty;
        body = string.Join('\n', body.Split('\n').Where(l => !l.Equals(LatestWinsRule, StringComparison.Ordinal)));
        if (body.Length > 0)
        {
            text.Append(body).Append('\n');
        }

        var files = FilesChanged(messages);
        if (files.Count > 0)
        {
            text.Append("Files changed:\n");
            foreach (var file in files)
            {
                text.Append("- ").Append(file).Append('\n');
            }
        }

        var all = text.ToString();
        return all.Length <= maxChars ? all : all[..maxChars].TrimEnd() + "\n";
    }

    /// <summary>
    /// A brief found at the start of a chat (the chat began by pasting one): its starting point,
    /// its history lines and its facts, carried into the next brief instead of being quoted as an
    /// ordinary opening request and cut at <see cref="OpeningChars"/>.
    /// </summary>
    internal sealed record EarlierBrief(string Started, IReadOnlyList<string> History, string Facts)
    {
        public static EarlierBrief? Parse(string opening)
        {
            if (!opening.TrimStart().StartsWith(Title, StringComparison.Ordinal))
            {
                return null;
            }

            var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in ("\n" + opening).Split("\n## ").Skip(1))
            {
                var split = part.Split('\n', 2);
                var heading = split[0].Trim();
                var body = split.Length > 1 ? split[1].Trim() : string.Empty;
                var key = heading.StartsWith("How we got here", StringComparison.OrdinalIgnoreCase) ? "history"
                    : heading.StartsWith("Where it started", StringComparison.OrdinalIgnoreCase) || heading.StartsWith("Original request", StringComparison.OrdinalIgnoreCase) ? "started"
                    : heading.StartsWith("Facts gathered", StringComparison.OrdinalIgnoreCase) || heading.StartsWith("Earlier in the chat", StringComparison.OrdinalIgnoreCase) ? "facts"
                    : heading.StartsWith("Where things stand now", StringComparison.OrdinalIgnoreCase) ? "now"
                    : heading;
                sections[key] = body;
            }

            var started = sections.TryGetValue("started", out var s)
                ? string.Join('\n', s.Split('\n').Where(l => !l.StartsWith("The chat began with this request.", StringComparison.Ordinal))).Trim()
                : string.Empty;
            var history = sections.TryGetValue("history", out var h)
                ? h.Split('\n').Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).ToList()
                : new List<string>();
            // What stood "now" in the earlier chat is history in this one: its final message joins the timeline.
            if (sections.TryGetValue("now", out var now)
                && now.Split("### Your final message", 2) is [_, var final]
                && final.Split('\n', 2) is [_, var finalText]
                && finalText.Trim() is { Length: > 0 } said
                && !said.StartsWith("Left out of this brief", StringComparison.Ordinal))
            {
                history.Add("- " + Cap(LocalHistoryCompaction.CollapseWhitespace(said), TimelineEntryChars));
            }

            return new EarlierBrief(started.Length > 0 ? started : opening, history, sections.TryGetValue("facts", out var f) ? f : string.Empty);
        }
    }

    /// <summary>"2026-11-26 1025 leave out cancelled invoices priya.md": the date sorts, the words say where the task stands.</summary>
    public static string FileName(JsonArray messages, DateTime capturedUtc)
    {
        // Named after the latest direction (the person's final message), since the task may have
        // moved on from its opening; a chat that began from a brief otherwise named every brief
        // "hand over brief …".
        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var opening = first >= 0 ? HumanText(messages[first] as JsonObject) : string.Empty;
        var latest = LastPersonMessage(messages);
        var basis = latest.Length >= 12 ? latest : EarlierBrief.Parse(opening)?.Started ?? opening;
        var words = Regex.Replace(basis, @"[^\p{L}\p{N} ]+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("task", StringComparison.OrdinalIgnoreCase))
            .Take(7);
        var label = string.Join(" ", words).ToLowerInvariant();
        return capturedUtc.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)
               + (label.Length > 0 ? " " + (label.Length > 60 ? label[..60].TrimEnd() : label) : string.Empty) + ".md";
    }

    /// <summary>The person's last message in the chat (their own words), or empty when it is the opening request.</summary>
    public static string LastPersonMessage(JsonArray? messages)
    {
        if (messages is null)
        {
            return string.Empty;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var last = LocalHistoryCompaction.IndexOfLastRealUser(messages);
        return last > first ? HumanText(messages[last] as JsonObject) : string.Empty;
    }

    /// <summary>
    /// What is known about the person's final message, in estimated tokens (chars / 4 scaled by the
    /// measured ratio, pictures counted as the gateway counts them): the chat before it, the message
    /// itself, everything after it (tool calls, results, replies), whether the chat was then
    /// refused as too large, and the room a new chat would have with this brief. What it cannot
    /// know is stated in <see cref="Describe"/>: if the chat filled before the work the message set
    /// off was done, or the message was refused before any reply, its full cost is unknown.
    /// </summary>
    public sealed record FinalMessageImpact(
        int ContextTokens,
        int BeforeTokens,
        int MessageTokens,
        int AfterTokens,
        int ToolCallsAfter,
        bool AnythingAfter,
        bool Overflowed,
        int NewChatTokens)
    {
        public int FreeTokens => Math.Max(0, ContextTokens - BeforeTokens - MessageTokens - AfterTokens);

        /// <summary>Left out by default only when the chat was refused for size after the final message.</summary>
        public bool ExcludeByDefault => Overflowed;

        private string Of(int tokens)
            => "about " + tokens.ToString("N0", CultureInfo.InvariantCulture) + "/" + ContextTokens.ToString("N0", CultureInfo.InvariantCulture)
               + " tokens (" + Percent(tokens) + " of the Context)";

        private string Percent(int tokens)
            => (ContextTokens > 0 ? (int)Math.Round(100.0 * tokens / ContextTokens) : 0).ToString(CultureInfo.InvariantCulture) + " %";

        private static string Choice(bool included)
            => included
                ? "Your final message is included; untick to leave it out."
                : "Tick to include your final message anyway.";

        /// <summary>The notice's sentence about the final message: the facts, and where they stop.</summary>
        public string Describe(bool included)
        {
            if (ContextTokens <= 0)
            {
                return Overflowed
                    ? "The chat was refused as too large after your final message; its size against the Context is not known. " + Choice(included)
                    : string.Empty;
            }

            if (Overflowed && !AnythingAfter)
            {
                return "Your final message required " + Of(MessageTokens) + ", but there were only "
                       + Math.Max(0, ContextTokens - BeforeTokens).ToString("N0", CultureInfo.InvariantCulture)
                       + " tokens free, so the chat failed. " + Choice(included);
            }

            if (Overflowed)
            {
                var remaining = Math.Max(0, ContextTokens - BeforeTokens - MessageTokens);
                return "Your final message occupied " + Of(MessageTokens) + ", and triggered a model response"
                       + (ToolCallsAfter > 0 ? " (" + ToolCallsAfter.ToString("N0", CultureInfo.InvariantCulture) + (ToolCallsAfter == 1 ? " tool call)" : " tool calls)") : string.Empty)
                       + " that used up all the remaining context (" + Percent(remaining) + ") before the chat failed. "
                       + "What the final cost of that reply would have been is unknown. " + Choice(included);
            }

            return "The model had free space of " + Of(FreeTokens) + " at time of handover brief preparation. "
                   + "A new chat under the same model should have around "
                   + Math.Max(0, ContextTokens - NewChatTokens).ToString("N0", CultureInfo.InvariantCulture) + "/" + ContextTokens.ToString("N0", CultureInfo.InvariantCulture)
                   + " tokens free (" + Percent(Math.Max(0, ContextTokens - NewChatTokens)) + " of the Context).";
        }
    }

    /// <summary>
    /// Measures <see cref="FinalMessageImpact"/> for the chat. <paramref name="briefChars"/> is the
    /// brief's length, which with the system prompt is what a new chat starts with.
    /// </summary>
    public static FinalMessageImpact? Impact(JsonArray? messages, int contextTokens, double tokenRatio, bool overflowed, int briefChars)
    {
        if (messages is null)
        {
            return null;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var last = LocalHistoryCompaction.IndexOfLastRealUser(messages);
        if (last <= first)
        {
            return null;
        }

        var ratio = HailMary.RescuePlanner.NormaliseRatio(tokenRatio);
        int Tokens(IEnumerable<JsonNode?> part)
        {
            var array = new JsonArray();
            foreach (var node in part)
            {
                array.Add(node?.DeepClone());
            }

            return (int)Math.Ceiling(LocalRequestOverlayRouting.EstimatePromptTokens(new JsonObject { ["messages"] = array }) * ratio);
        }

        var after = messages.Skip(last + 1).Where(m => LocalHistoryCompaction.RoleOf(m as JsonObject ?? new JsonObject()) != "system").ToList();
        var system = messages.Where(m => LocalHistoryCompaction.RoleOf(m as JsonObject ?? new JsonObject()) == "system");
        return new FinalMessageImpact(
            contextTokens,
            Tokens(messages.Take(last)),
            Tokens([messages[last]]),
            Tokens(after),
            after.OfType<JsonObject>().Sum(m => (m["tool_calls"] as JsonArray)?.Count ?? 0),
            after.Count > 0,
            overflowed,
            Tokens(system) + (int)Math.Ceiling(briefChars / 4.0 * ratio));
    }

    /// <summary>Pictures in a message, which a text brief cannot carry.</summary>
    internal static int ImageCount(JsonObject? message)
        => message?["content"] is JsonArray parts
            ? parts.OfType<JsonObject>().Count(p => (p["type"]?.ToString() ?? string.Empty) is "image_url" or "image" or "input_image")
            : 0;

    private static string ImageNote(JsonObject? message)
        => ImageCount(message) is var n and > 0
            ? "(" + n + (n == 1 ? " image was" : " images were") + " attached here; " + (n == 1 ? "it is" : "they are") + " not in this brief, so attach " + (n == 1 ? "it" : "them") + " again if needed)\n"
            : string.Empty;

    /// <summary>
    /// Up to 80 characters of the first line the person wrote in the opening request, as it stands in
    /// the chat (for a chat begun from a brief, the brief's own first line), for finding the Client
    /// app's chat file that holds this chat. Taken from the messages, not read back out of a summary:
    /// reading it from under the summary's "## Original request" heading broke silently when that
    /// section was renamed (26 Nov), and Hail Mary then matched no Cline task at all.
    /// </summary>
    public static string OpeningProbe(JsonArray? messages)
    {
        var first = messages is null ? -1 : LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var line = first < 0
            ? string.Empty
            : HumanText(messages![first] as JsonObject).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 80 ? line[..80] : line;
    }

    private static string HumanText(JsonObject? message)
        => message is null
            ? string.Empty
            : ConversationText.HumanPart(LocalHistoryCompaction.ExtractText(message["content"])).Trim();

    private static string Cap(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + " …";

    /// <summary>Paths named by file-changing tool calls, in the order first changed.</summary>
    internal static List<string> FilesChanged(JsonArray messages)
    {
        var files = new List<string>();
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["tool_calls"] is not JsonArray calls)
            {
                continue;
            }

            foreach (var call in calls.OfType<JsonObject>())
            {
                var name = call["function"]?["name"]?.ToString() ?? call["name"]?.ToString() ?? string.Empty;
                if (!EditToolName.IsMatch(name))
                {
                    continue;
                }

                var path = ArgumentPath(call["function"]?["arguments"] ?? call["arguments"]);
                if (!string.IsNullOrWhiteSpace(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase) && files.Count < MaxFilesChanged)
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }

    private static string? ArgumentPath(JsonNode? arguments)
    {
        var args = arguments as JsonObject;
        if (args is null && arguments is JsonValue raw && raw.TryGetValue<string>(out var json))
        {
            try
            {
                args = JsonNode.Parse(json) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        return args is null
            ? null
            : PathFields.Select(f => args[f]?.ToString()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();
    }

    /// <summary>The quoted tail, oldest first, trimmed from the oldest end to fit the cap.</summary>
    private static List<string> RecentLines(JsonArray messages, int keepStart, int skipIndex = -1)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = keepStart; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            // The person's last message has its own section, so it can be left out.
            var role = LocalHistoryCompaction.RoleOf(message);
            if (role == "system" || i == skipIndex)
            {
                continue;
            }

            if (LocalHistoryCompaction.IsToolFollowUp(message))
            {
                var result = LocalHistoryCompaction.ExtractText(message["content"]).Trim();
                var source = callNames.TryGetValue(message["tool_call_id"]?.ToString() ?? string.Empty, out var named) ? named : "tool";
                if (result.Length > 0)
                {
                    // A file's contents are left out even here: the new chat rereads the file, and a
                    // quoted copy may already be out of date. Command output (a test run, an error)
                    // is what the work found, so it is quoted.
                    lines.Add(FileReadCall.IsMatch(source)
                        ? "- result of " + source + ": (file contents left out; reread the file if needed)"
                        : "- result of " + source + ": " + Cap(result, RecentToolResultChars));
                }

                continue;
            }

            if (role == "user")
            {
                var said = HumanText(message);
                if (said.Length > 0 && !ConversationText.IsAiFluxMuxNotice(said))
                {
                    lines.Add("- person: " + Cap(said, RecentMessageChars) + (ImageNote(message) is { Length: > 0 } note ? " " + note.TrimEnd() : string.Empty));
                }

                continue;
            }

            var reply = LocalHistoryCompaction.ExtractText(message["content"]).Trim();
            if (reply.Length > 0 && !ConversationText.IsAiFluxMuxNotice(reply))
            {
                lines.Add("- assistant: " + Cap(reply, RecentMessageChars));
            }

            if (message["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls.OfType<JsonObject>())
                {
                    var described = LocalToolResultClearing.DescribeToolCall(call);
                    if (call["id"]?.ToString() is { Length: > 0 } id)
                    {
                        callNames[id] = described;
                    }

                    if (described.Length > 0)
                    {
                        lines.Add("- assistant ran " + described);
                    }
                }
            }
        }

        var total = lines.Sum(l => l.Length + 1);
        while (lines.Count > 1 && total > RecentTotalChars)
        {
            total -= lines[0].Length + 1;
            lines.RemoveAt(0);
        }

        return lines;
    }
}
