using System;
using System.Collections.Concurrent;
using System.Globalization;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Auto steer (28 Nov): when the model in a Client app loops or keeps failing, AI-FluxMux first
/// tells it so in a note on its next request, and pauses the turn for the person only if that
/// does not work. Before, the repeat note was part of the Repeated command rule and the long-run
/// checks (Mill check, the Closed-loop ceiling) went straight to a pause that waited for the
/// person: a Cline chat paused at 256 tools and waited five minutes, and with Port rules off a
/// run of 800+ calls had no check at all. Notes carry only what AI-FluxMux observed (counts,
/// error kinds), never the model's own words. Independent of Port rules
/// (<see cref="PortForwardingRules.AutoSteerEnabled"/>): their pauses stay theirs.
/// </summary>
public sealed class AutoSteer
{
    /// <summary>Tool calls the model gets, after being asked to stop and report, before a long run is paused.</summary>
    public const int MillGrace = 16;

    /// <summary>With Auto steer on, a repeated command is paused at this count (one more chance after the firm note).</summary>
    public const int RepeatPauseWithSteer = 4;

    /// <summary>Without Auto steer, the Repeated command rule pauses at this count, as before.</summary>
    public const int RepeatPauseWithoutSteer = 3;

    private readonly ConcurrentDictionary<string, int> _millSteeredAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tool count at which this run was first steered, recording <paramref name="toolsSinceHuman"/>
    /// when it is new. A run is the tool calls since the person's last message, kept apart per
    /// Client app (<paramref name="key"/>); a count lower than the recorded one means the person
    /// wrote since, so a new run starts.
    /// </summary>
    public int MillSteeredAt(string key, int toolsSinceHuman)
        => _millSteeredAt.AddOrUpdate(
            key ?? string.Empty,
            toolsSinceHuman,
            (_, recorded) => toolsSinceHuman < recorded ? toolsSinceHuman : recorded);

    /// <summary>True while the model still has its chance after being asked to stop and report.</summary>
    public static bool WithinMillGrace(int steeredAt, int toolsSinceHuman) => toolsSinceHuman - steeredAt < MillGrace;

    /// <summary>
    /// The note for a long or unproductive run: the count and, when the run is over the ceiling,
    /// that; then stop and report to the person, unless the very next call finishes the task.
    /// </summary>
    public static string StopAndReportNote(int toolsSinceHuman, bool overCeiling, bool firmer)
        => "Note from AI-FluxMux: you have made "
           + toolsSinceHuman.ToString(CultureInfo.InvariantCulture)
           + " tool calls since the person's last message"
           + (overCeiling ? string.Empty : ", and the recent ones do not look like progress (reading or repeating without changing anything)")
           + ". "
           + (firmer
               ? "You were already asked to stop. Do not make another tool call: reply to the person now. "
               : "Before any further tool call, stop and reply to the person. ")
           + "Say what you have done, what is blocking you, and what you propose next, then wait for their answer"
           + (firmer ? "." : ". Carry on with tools only if the very next call finishes the task.");
}
