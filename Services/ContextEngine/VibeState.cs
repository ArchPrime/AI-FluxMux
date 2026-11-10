using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The persistent, structured "vibe" of a session: what the user is working on, the current
/// state, and the next step. This is the machine-readable core of <c>VIBE_STATE.md</c> — the
/// file the user can read and edit, and the thing the context engine re-injects at the top of
/// every local turn so the model always knows "where we are" even after the working context has
/// been paged away.
///
/// It is deliberately small and flat: a handful of short strings that the model can update in
/// place (via a tool call) and that survive context resets. The human-readable <c>VIBE_STATE.md</c>
/// is rendered from this record (see <see cref="VibeStateRenderer"/>); the record is the source
/// of truth, the markdown is the view.
///
/// This type is pure data — no I/O, no dependencies — so it is trivially testable and reusable
/// across platforms.
/// </summary>
public sealed record VibeState
{
    /// <summary>
    /// A one-line summary of what the user is working on (the "task"). Stable across turns;
    /// only changes when the user pivots to a new task.
    /// </summary>
    [JsonPropertyName("task")]
    public string Task { get; init; } = string.Empty;

    /// <summary>
    /// The current state of the work: what has been done, what is in progress, what is blocked.
    /// Updated as the session progresses.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// The next concrete step to take. This is the most important field for re-injection: it
    /// tells the model exactly where to resume after a context reset.
    /// </summary>
    [JsonPropertyName("next")]
    public string Next { get; init; } = string.Empty;

    /// <summary>
    /// Key files, symbols, or decisions the model should keep in mind. Free-form, comma or
    /// newline separated.
    /// </summary>
    [JsonPropertyName("files")]
    public string Files { get; init; } = string.Empty;

    /// <summary>
    /// ISO-8601 UTC timestamp of the last time this state was written. Used for staleness
    /// detection and to avoid clobbering a newer state with an older one.
    /// </summary>
    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>
    /// A blank state (no task, no state, no next). Used as the default when no
    /// <c>VIBE_STATE.md</c> exists yet.
    /// </summary>
    public static VibeState Empty { get; } = new();

    /// <summary>
    /// True when this state carries no meaningful content (all fields blank).
    /// </summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Task)
        && string.IsNullOrWhiteSpace(State)
        && string.IsNullOrWhiteSpace(Next)
        && string.IsNullOrWhiteSpace(Files);

    /// <summary>
    /// Serializes this state to a compact JSON object. The JSON is the on-disk format embedded
    /// in <c>VIBE_STATE.md</c> (inside a fenced <c>```json</c> block) so it can be parsed back
    /// without ambiguity.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, VibeStateJson.Options);
    }

    /// <summary>
    /// Parses a <c>VibeState</c> from JSON. Returns <see cref="Empty"/> on null/blank input;
    /// throws on malformed JSON (callers that want resilience should catch and fall back).
    /// </summary>
    public static VibeState FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        return JsonSerializer.Deserialize<VibeState>(json, VibeStateJson.Options) ?? Empty;
    }

    /// <summary>
    /// A defensive copy with <see cref="UpdatedUtc"/> set to now. Used when the engine writes a
    /// fresh state so the timestamp always reflects the write time.
    /// </summary>
    public VibeState WithTimestamp(DateTimeOffset now) => this with { UpdatedUtc = now };
}

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for <see cref="VibeState"/>: camelCase, no
/// indentation (compact), and ignore-on-missing so older files parse cleanly when new fields
/// are added.
/// </summary>
internal static class VibeStateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}