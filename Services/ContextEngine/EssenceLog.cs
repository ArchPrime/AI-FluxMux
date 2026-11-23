using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// One entry in the essence log's goal-evolution history: a recorded pivot of the task.
/// Captured live (mechanically or by the model) so the essence survives compaction and cold
/// resume. This is the "how the goal changed" record that a lossy compaction would otherwise
/// destroy.
/// </summary>
public sealed record EssenceEvolutionEntry(
    string From,
    string To,
    string Why,
    DateTimeOffset AtUtc);

/// <summary>
/// A captured candidate intent-shift: a user message that the heuristic flagged as possibly
/// changing, correcting, or steering the goal. Stored VERBATIM (the fidelity payload) plus the
/// prior task as an anchor, so the reading model can later judge whether it was a pivot, a
/// correction, or ordinary steering. The mechanical layer deliberately does NOT classify it —
/// it records the raw material and a weak "candidate" tag, biasing toward recall. Bounded so
/// the durable file cannot balloon; the reinjected views surface only a small recent subset.
/// </summary>
public sealed record EssenceIntentShift(
    string Verbatim,
    string PriorTask,
    DateTimeOffset AtUtc);

/// <summary>
/// The durable essence log: the single source of truth for a session's *meaning* — the current
/// task/state/next/files snapshot PLUS the history of how the goal evolved, the key decisions,
/// and the dead ends. Written continuously while the model is live; re-asserted after a
/// compaction or cold resume so the essence survives even when the raw context is discarded.
/// Mirrors ContextSwapStore's resilience: all I/O via <see cref="IContextIo"/>, never throws.
/// </summary>
public sealed class EssenceLog
{
    private const int MaxEvolutionEntries = 64;   // bound the history so the file cannot balloon
    private const int MaxDecisions = 64;
    private const int MaxDeadEnds = 64;
    private const int MaxFiles = 128;
    private const int MaxIntentShifts = 64;        // bound the intent-shift candidates (verbatim)
    private const int MaxEntryChars = 2_000;      // cap any single free-text field

    private readonly IContextIo _io;
    private readonly string _path;

    public EssenceLog(IContextIo io, string path)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The path of the essence log file.</summary>
    public string Path => _path;

    /// <summary>Loads the current essence. Returns an empty <see cref="Essence"/> when the file is
    /// missing or unreadable (never throws).</summary>
    public Essence Load()
    {
        try
        {
            if (!_io.Exists(_path))
            {
                return Essence.Empty;
            }

            var text = _io.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Essence.Empty;
            }

            var parsed = Essence.FromJson(text);
            return parsed ?? Essence.Empty;
        }
        catch
        {
            return Essence.Empty;
        }
    }

    /// <summary>Persists <paramref name="essence"/> to disk (atomic, never throws).</summary>
    public void Save(Essence essence)
    {
        if (essence is null)
        {
            return;
        }

        try
        {
            _io.WriteAllTextResilient(_path, essence.ToJson());
        }
        catch
        {
            // A failed essence write must never break a turn.
        }
    }

    /// <summary>Records a goal pivot in the evolution history and updates the current task.
    /// Bounded (oldest dropped first). Never throws.</summary>
    public void RecordEvolution(string from, string to, string why)
    {
        // A "change" to the same goal is not a change. 55 of 64 saved entries were exactly that
        // (the same task label recorded as a pivot on every turn of a long agent loop).
        if (string.IsNullOrWhiteSpace(to)
            || ConversationText.Normalize(Truncate(from)) == ConversationText.Normalize(Truncate(to)))
        {
            return;
        }

        var e = Load();
        e.Evolution.Add(new EssenceEvolutionEntry(
            Truncate(from), Truncate(to), Truncate(why), DateTimeOffset.UtcNow));
        while (e.Evolution.Count > MaxEvolutionEntries)
        {
            e.Evolution.RemoveAt(0);
        }

        e.Current = e.Current with { Task = Truncate(to) };
        Save(e);
    }

    /// <summary>Records a candidate intent-shift (a user message that may have changed/corrected/
    /// steered the goal) VERBATIM, with the prior task as an anchor. Bounded (oldest dropped
    /// first). Never throws. The mechanical layer does NOT classify the shift — it stores the raw
    /// material so the reading model can later judge whether it was a pivot, a correction, or
    /// ordinary steering.</summary>
    public void RecordIntentShift(string verbatim, string priorTask)
    {
        if (string.IsNullOrWhiteSpace(verbatim))
        {
            return;
        }

        var e = Load();
        // Client apps resend the whole chat every turn, so the same user message reaches this
        // point again and again; one was stored 30 times. Keep each message once.
        var normalized = ConversationText.Normalize(Truncate(verbatim));
        if (e.IntentShifts.Exists(shift => ConversationText.Normalize(shift.Verbatim) == normalized))
        {
            return;
        }

        e.IntentShifts.Add(new EssenceIntentShift(
            Truncate(verbatim), Truncate(priorTask), DateTimeOffset.UtcNow));
        while (e.IntentShifts.Count > MaxIntentShifts)
        {
            e.IntentShifts.RemoveAt(0);
        }

        Save(e);
    }

    /// <summary>Appends a key decision / finding. Bounded. Never throws.</summary>
    public void RecordDecision(string decision)
    {
        if (string.IsNullOrWhiteSpace(decision))
        {
            return;
        }

        var e = Load();
        e.Decisions.Add(Truncate(decision));
        while (e.Decisions.Count > MaxDecisions)
        {
            e.Decisions.RemoveAt(0);
        }

        Save(e);
    }

    /// <summary>Appends a dead end / failed approach (what NOT to retry). Bounded. Never throws.</summary>
    public void RecordDeadEnd(string deadEnd)
    {
        if (string.IsNullOrWhiteSpace(deadEnd))
        {
            return;
        }

        var e = Load();
        e.DeadEnds.Add(Truncate(deadEnd));
        while (e.DeadEnds.Count > MaxDeadEnds)
        {
            e.DeadEnds.RemoveAt(0);
        }

        Save(e);
    }

    /// <summary>Records a key file/symbol (distinct, bounded). Never throws.</summary>
    public void RecordFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        var e = Load();
        var f = Truncate(file).Trim();
        if (!e.Files.Any(existing => string.Equals(existing, f, StringComparison.OrdinalIgnoreCase)))
        {
            e.Files.Add(f);
            while (e.Files.Count > MaxFiles)
            {
                e.Files.RemoveAt(0);
            }
        }

        Save(e);
    }

    /// <summary>Updates the current snapshot (task/state/next/files). Only non-empty arguments
    /// override; blank arguments leave the existing value untouched. Never throws.</summary>
    public void UpdateCurrent(string? task, string? state, string? next, string? files)
    {
        var e = Load();
        e.Current = e.Current with
        {
            Task = string.IsNullOrWhiteSpace(task) ? e.Current.Task : Truncate(task),
            State = string.IsNullOrWhiteSpace(state) ? e.Current.State : Truncate(state),
            Next = string.IsNullOrWhiteSpace(next) ? e.Current.Next : Truncate(next),
            Files = string.IsNullOrWhiteSpace(files) ? e.Current.Files : Truncate(files),
        };

        Save(e);
    }

    /// <summary>True when the log carries any meaningful content.</summary>
    public bool HasContent() => Load().HasContent();

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > MaxEntryChars ? value[..MaxEntryChars] + "…" : value;
    }
}

/// <summary>
/// The current-snapshot half of the essence: where the session is *right now* (task/state/next/
/// files). Kept small and flat so it can be re-injected cheaply.
/// </summary>
public sealed record EssenceCurrent(
    string Task,
    string State,
    string Next,
    string Files)
{
    public static EssenceCurrent Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Task)
        && string.IsNullOrWhiteSpace(State)
        && string.IsNullOrWhiteSpace(Next)
        && string.IsNullOrWhiteSpace(Files);
}

/// <summary>
/// The full essence: the current snapshot plus the durable history (goal evolution, decisions,
/// dead ends, key files). This is what gets re-asserted after compaction / cold resume.
/// </summary>
public sealed class Essence
{
    public EssenceCurrent Current { get; set; } = EssenceCurrent.Empty;
    public List<EssenceEvolutionEntry> Evolution { get; set; } = new();

    /// <summary>Candidate intent-shifts (verbatim user messages that may have changed/corrected/
    /// steered the goal), most recent last. Bounded. The reading model judges each; the mechanical
    /// layer only captures the raw material.</summary>
    public List<EssenceIntentShift> IntentShifts { get; set; } = new();
    public List<string> Decisions { get; set; } = new();
    public List<string> DeadEnds { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public int Version { get; set; } = 1;

    /// <summary>
    /// Returns a fresh, empty <see cref="Essence"/> instance. This is a factory method (not a
    /// shared singleton) so that callers can safely mutate the returned instance without affecting
    /// other callers. A shared singleton would cause cross-test and cross-session contamination:
    /// any mutation to <c>Essence.Empty.Current</c> or <c>Essence.Empty.IntentShifts</c> would
    /// leak into every other code path that reads <c>Essence.Empty</c>.
    /// </summary>
    public static Essence Empty => new();

    public bool HasContent() =>
        !Current.IsEmpty
        || Evolution.Count > 0
        || IntentShifts.Count > 0
        || Decisions.Count > 0
        || DeadEnds.Count > 0
        || Files.Count > 0;

    /// <summary>Renders the essence into a Cline-style "Context summary" block (the compaction
    /// body): ## Goal / ## State / ## Next / ## Files plus the essence sections.</summary>
    public string ToClineSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Goal");
        sb.AppendLine(Dash(Current.Task));
        sb.AppendLine();

        if (Evolution.Count > 0)
        {
            sb.AppendLine("## Goal evolution (how the task changed)");
            foreach (var ev in Evolution)
            {
                sb.AppendLine("- " + Dash(ev.From) + " → " + Dash(ev.To)
                    + (string.IsNullOrWhiteSpace(ev.Why) ? string.Empty : " (" + ev.Why.Trim() + ")"));
            }

            sb.AppendLine();
        }

        // Candidate intent-shifts: verbatim user messages that may have changed/corrected/steered
        // the goal. Bounded to the most recent 8 so this rich view does not balloon the compaction
        // file. Each line carries the prior task (anchor) + the user's exact words, so the reading
        // model can judge whether it was a pivot, a correction, or ordinary steering.
        if (IntentShifts.Count > 0)
        {
            sb.AppendLine("## Intent-shift candidates (user messages that may have changed the goal — judge each)");
            var recent = IntentShifts.Skip(Math.Max(0, IntentShifts.Count - 8)).ToList();
            foreach (var s in recent)
            {
                sb.AppendLine("- " + Dash(s.Verbatim)
                    + (string.IsNullOrWhiteSpace(s.PriorTask) ? string.Empty : " [prior task: " + s.PriorTask.Trim() + "]"));
            }

            sb.AppendLine();
        }

        sb.AppendLine("## State");
        sb.AppendLine(Dash(Current.State));
        sb.AppendLine();
        sb.AppendLine("## Next");
        sb.AppendLine(Dash(Current.Next));
        sb.AppendLine();

        if (Decisions.Count > 0)
        {
            sb.AppendLine("## Key decisions & findings");
            foreach (var d in Decisions)
            {
                sb.AppendLine("- " + d.Trim());
            }

            sb.AppendLine();
        }

        if (DeadEnds.Count > 0)
        {
            sb.AppendLine("## Dead ends (do not retry)");
            foreach (var d in DeadEnds)
            {
                sb.AppendLine("- " + d.Trim());
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Files");
        sb.AppendLine(Dash(Files.Count > 0 ? string.Join(", ", Files) : Current.Files));
        return sb.ToString().TrimEnd();
    }

    /// <summary>A short, terse block for system-prompt re-injection (much smaller than
    /// <see cref="ToClineSummary"/>): only the current snapshot + the most recent pivot.</summary>
    public string ToReinjectionBlock()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ESSENCE — current session context]");
        sb.AppendLine("Task: " + Dash(Current.Task));
        sb.AppendLine("State: " + Dash(Current.State));
        sb.AppendLine("Next: " + Dash(Current.Next));
        if (Files.Count > 0)
        {
            sb.AppendLine("Key files: " + string.Join(", ", Files));
        }
        else if (!string.IsNullOrWhiteSpace(Current.Files))
        {
            sb.AppendLine("Key files: " + Current.Files.Trim());
        }

        if (Evolution.Count > 0)
        {
            var last = Evolution[Evolution.Count - 1];
            sb.AppendLine("Last goal change: " + Dash(last.From) + " → " + Dash(last.To));
        }

        // Candidate intent-shifts: only the most recent 1-2, hard-truncated. This is the
        // speed-sensitive path (injected into the live system prompt at a compaction edge), so
        // keep it minimal. The full bounded history is in ToClineSummary / the durable file.
        if (IntentShifts.Count > 0)
        {
            var recent = IntentShifts.Skip(Math.Max(0, IntentShifts.Count - 2)).ToList();
            foreach (var s in recent)
            {
                sb.AppendLine("Intent-shift candidate: " + Dash(TruncateForReinjection(s.Verbatim, 120))
                    + (string.IsNullOrWhiteSpace(s.PriorTask) ? string.Empty : " [prior: " + TruncateForReinjection(s.PriorTask, 80) + "]"));
            }
        }

        return sb.ToString();
    }

    /// <summary>Collapses whitespace and hard-truncates a value for the terse reinjection block
    /// (the speed-sensitive path). Keeps each intent-shift line to a single bounded line so the
    /// system-prompt injection stays small.</summary>
    private static string TruncateForReinjection(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
        return text.Length > maxChars ? text[..maxChars].TrimEnd() + "…" : text;
    }

    private static string Dash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    public string ToJson() => JsonSerializer.Serialize(this, EssenceJson.Options);

    public static Essence? FromJson(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Essence>(json, EssenceJson.Options);
}

/// <summary>Shared <see cref="JsonSerializerOptions"/> for <see cref="Essence"/>.</summary>
internal static class EssenceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}