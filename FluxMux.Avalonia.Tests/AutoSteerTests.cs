using System.IO;
using FluxMux.Avalonia.Services;
using Xunit;
using static FluxMux.Avalonia.Services.LocalChatPayloadSignals;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Auto steer (28 Nov): notes to the model before any pause, whether Port rules are on or off.
/// The failure texts are the ones Cline returned in the operator's chats.
/// </summary>
public sealed class AutoSteerTests
{
    [Theory]
    [InlineData("{\"error\":\"Tool call run_commands emitted invalid JSON arguments: Tool call arguments could not be parsed as JSON.\"}", ToolFailureKind.TooLarge)]
    [InlineData("{\"error\":\"Editor input too large: new_text was 15951 characters, exceeding the recommended limit of 6000.\"}", ToolFailureKind.TooLarge)]
    [InlineData("{\"error\":\"Editor operation failed: No replacement performed: text not found in c:\\\\x\\\\a.cs\"}", ToolFailureKind.TextNotFound)]
    [InlineData("{\"error\":\"Editor operation failed: No replacement performed: multiple occurrences of text found in a.cs\"}", ToolFailureKind.TextNotUnique)]
    [InlineData("{\"error\":\"Editor operation failed: a.cs already exists, but `old_text` was omitted.\"}", ToolFailureKind.FileExists)]
    [InlineData("{\"error\":\"Error reading file: ENOENT: no such file or directory, stat 'C:\\\\a.cs'\"}", ToolFailureKind.PathNotFound)]
    [InlineData("{\"error\":\"Search failed: Search timed out after 30000ms\"}", ToolFailureKind.TooSlow)]
    [InlineData("{\"error\":\"Command not executed: file modifications are blocked in plan mode.\"}", ToolFailureKind.PlanMode)]
    [InlineData("{\"error\":\"Command exited with code 1\"}", ToolFailureKind.Other)]
    public void Failures_are_classified_by_their_error_text(string result, ToolFailureKind kind)
        => Assert.Equal(kind, ClassifyToolFailure(result));

    [Fact]
    public void Two_failures_of_the_same_known_kind_get_a_note_with_the_fix()
    {
        var notFound = "{\"error\":\"Editor operation failed: No replacement performed: text not found in a.cs\"}";
        var exit1 = "{\"error\":\"Command exited with code 1\"}";
        Assert.True(FailuresNeedNote([notFound, notFound]));
        Assert.False(FailuresNeedNote([exit1, exit1]));
        Assert.False(FailuresNeedNote([notFound]));
        Assert.True(FailuresNeedNote([exit1, exit1, exit1]));

        var note = FailedToolRunNote([notFound, notFound]);
        Assert.Contains("failed the same way", note, System.StringComparison.Ordinal);
        Assert.Contains("Read the file again", note, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_too_large_call_is_told_to_write_in_smaller_pieces()
    {
        var bad = "{\"error\":\"Tool call editor emitted invalid JSON arguments\"}";
        Assert.Contains("several smaller calls", FailedToolRunNote([bad, bad, bad]), System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_run_is_steered_once_then_paused_after_the_grace_and_a_new_run_starts_over()
    {
        var steer = new AutoSteer();
        Assert.Equal(256, steer.MillSteeredAt("Cline", 256));
        Assert.Equal(256, steer.MillSteeredAt("Cline", 260));
        Assert.True(AutoSteer.WithinMillGrace(256, 260));
        Assert.False(AutoSteer.WithinMillGrace(256, 256 + AutoSteer.MillGrace));
        // Another Client app has its own run.
        Assert.Equal(40, steer.MillSteeredAt("Harness", 40));
        // The person wrote: the count dropped, so this is a new run.
        Assert.Equal(12, steer.MillSteeredAt("Cline", 12));
    }

    [Fact]
    public void The_stop_and_report_note_asks_the_model_to_reply_to_the_person()
    {
        var first = AutoSteer.StopAndReportNote(256, overCeiling: true, firmer: false);
        Assert.Contains("256 tool calls since the person's last message", first, System.StringComparison.Ordinal);
        Assert.Contains("stop and reply to the person", first, System.StringComparison.Ordinal);
        Assert.Contains("You were already asked to stop", AutoSteer.StopAndReportNote(260, true, firmer: true), System.StringComparison.Ordinal);
        Assert.Contains("do not look like progress", AutoSteer.StopAndReportNote(40, overCeiling: false, firmer: false), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_steer_survives_Port_rules_off_and_is_saved()
    {
        var off = new PortForwardingRules { Enabled = false, AutoSteerEnabled = true }.ForForwarding();
        Assert.True(off.AutoSteerEnabled);
        Assert.False(off.RepeatedCommandEnabled);

        var dir = Path.Combine(Path.GetTempPath(), "autosteer_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new PortForwardingRulesStore(Path.Combine(dir, "rules.json"));
            store.Save(new PortForwardingRules { AutoSteerEnabled = false });
            Assert.False(store.Load().AutoSteerEnabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
