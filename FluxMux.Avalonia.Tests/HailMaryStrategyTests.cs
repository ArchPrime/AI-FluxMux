using System;
using System.IO;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.HailMary;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HailMaryStrategyTests
{
    // ── Picker ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WaitForCline", HailMaryStrategy.WaitForCline)]
    [InlineData("ExternalRescue", HailMaryStrategy.ExternalRescue)]
    [InlineData("anything else", HailMaryStrategy.ExternalRescue)]
    public void Picker_radios_map_to_their_strategy(string radio, HailMaryStrategy expected)
    {
        var converter = new FluxMux.Avalonia.ViewModels.HailMaryStrategyToBoolConverter();
        var picked = converter.ConvertBack(true, typeof(HailMaryStrategy), radio, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, picked);
        Assert.Equal(true, converter.Convert(expected, typeof(bool), expected.ToString(), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Strategy resolver ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Default_IsWaitForCline()
    {
        var strategy = CompactionStrategyResolver.Resolve(CompactionStrategyResolver.Default);
        Assert.IsType<WaitForClineStrategy>(strategy);
        Assert.Equal("wait-for-cline", strategy.Name);
    }

    [Fact]
    public void Resolve_WaitForCline_ReturnsWaitForCline()
    {
        var strategy = CompactionStrategyResolver.Resolve(HailMaryStrategy.WaitForCline);
        Assert.IsType<WaitForClineStrategy>(strategy);
    }

    [Fact]
    public void Resolve_ExternalRescue_ReturnsExternalRescue()
    {
        var strategy = CompactionStrategyResolver.Resolve(HailMaryStrategy.ExternalRescue);
        Assert.IsType<ExternalRescueStrategy>(strategy);
        Assert.Equal("external-rescue", strategy.Name);
    }

    // ── FidelitySnapshot.ToClineSummary ────────────────────────────────────────────

    [Fact]
    public void ToClineSummary_IncludesAllSections()
    {
        var snapshot = new FidelitySnapshot
        {
            Goal = "Fix the bug",
            State = "Done: read the code. In progress: writing the fix.",
            Next = "Run the tests",
            Files = "src/Foo.cs, src/Bar.cs",
            RecentTurns = new[] { "[user] hello", "[assistant] hi there" },
        };

        var summary = snapshot.ToClineSummary();

        Assert.Contains("## Goal", summary);
        Assert.Contains("Fix the bug", summary);
        Assert.Contains("## State", summary);
        Assert.Contains("Done: read the code", summary);
        Assert.Contains("## Next", summary);
        Assert.Contains("Run the tests", summary);
        Assert.Contains("## Files", summary);
        Assert.Contains("src/Foo.cs", summary);
        Assert.Contains("## Recent turns", summary);
        Assert.Contains("[user] hello", summary);
    }

    [Fact]
    public void ToClineSummary_EmptyFields_ShowDash()
    {
        var snapshot = new FidelitySnapshot { Goal = "Only goal" };
        var summary = snapshot.ToClineSummary();
        Assert.Contains("—", summary); // empty State/Next/Files render as dash
        Assert.DoesNotContain("## Recent turns", summary); // no recent turns section
    }

    [Fact]
    public void FidelitySnapshot_IsEmpty_WhenNoContent()
    {
        var empty = new FidelitySnapshot();
        Assert.True(empty.IsEmpty);

        var withGoal = new FidelitySnapshot { Goal = "x" };
        Assert.False(withGoal.IsEmpty);
    }

    // ── VibeStatePlusMessagesFidelitySource ────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_ReadsVibeStateAndMessages()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hailmary_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var vibePath = Path.Combine(tmp, "VIBE_STATE.md");
            File.WriteAllText(vibePath,
                "# VIBE STATE\n\n```json\n" +
                "{\"task\":\"My task\",\"state\":\"My state\",\"next\":\"My next\",\"files\":\"a.cs,b.cs\",\"updatedUtc\":\"2026-01-01T00:00:00Z\"}\n" +
                "```\n");

            var messagesPath = Path.Combine(tmp, "session.messages.json");
            File.WriteAllText(messagesPath,
                "{\"version\":1,\"messages\":[" +
                "{\"id\":\"m1\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"first user\"}]}," +
                "{\"id\":\"m2\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"first assistant\"}]}," +
                "{\"id\":\"m3\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"last user\"}]}" +
                "]}");

            var source = new VibeStatePlusMessagesFidelitySource();
            var ctx = new CompactionContext
            {
                VibeStatePath = vibePath,
                ClineMessagesPath = messagesPath,
            };

            var snapshot = await source.BuildAsync(ctx);

            Assert.Equal("My task", snapshot.Goal);
            Assert.Equal("My state", snapshot.State);
            Assert.Equal("My next", snapshot.Next);
            Assert.Equal("a.cs,b.cs", snapshot.Files);
            Assert.Equal(3, snapshot.SourceMessageCount);
            Assert.Equal("m3", snapshot.SourceLastMessageKey);
            Assert.Equal(3, snapshot.RecentTurns.Count);
            Assert.Contains("[user] last user", snapshot.RecentTurns);
            Assert.False(snapshot.IsEmpty);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_MissingFiles_ReturnsEmpty()
    {
        var source = new VibeStatePlusMessagesFidelitySource();
        var ctx = new CompactionContext
        {
            VibeStatePath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N") + ".md"),
            ClineMessagesPath = null,
        };

        var snapshot = await source.BuildAsync(ctx);
        Assert.True(snapshot.IsEmpty);
    }

    [Fact]
    public async Task BuildAsync_DenoisesRepeatedToolCalls()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hailmary_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var messagesPath = Path.Combine(tmp, "session.messages.json");
            File.WriteAllText(messagesPath,
                "{\"version\":1,\"messages\":[" +
                "{\"id\":\"m1\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"do the thing\"}]}," +
                "{\"id\":\"m2\",\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"run\",\"input\":{\"cmd\":\"ls\"}},{\"type\":\"text\",\"text\":\"running\"}]}," +
                "{\"id\":\"m3\",\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"run\",\"input\":{\"cmd\":\"ls\"}},{\"type\":\"text\",\"text\":\"retrying\"}]}," +
                "{\"id\":\"m4\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"the root cause was the config\"}]}" +
                "]}");

            var source = new VibeStatePlusMessagesFidelitySource();
            var ctx = new CompactionContext
            {
                VibeStatePath = string.Empty,
                ClineMessagesPath = messagesPath,
            };

            var snapshot = await source.BuildAsync(ctx);
            Assert.Contains("root cause", snapshot.KeyDecisions, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, snapshot.SourceMessageCount);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ExtractsGoalEvolution()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hailmary_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var messagesPath = Path.Combine(tmp, "session.messages.json");
            File.WriteAllText(messagesPath,
                "{\"version\":1,\"messages\":[" +
                "{\"id\":\"m1\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"please fix the login bug in the auth module\"}]}," +
                "{\"id\":\"m2\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"actually now also add rate limiting to the API gateway\"}]}" +
                "]}");

            var source = new VibeStatePlusMessagesFidelitySource();
            var ctx = new CompactionContext
            {
                VibeStatePath = string.Empty,
                ClineMessagesPath = messagesPath,
            };

            var snapshot = await source.BuildAsync(ctx);
            Assert.Contains("login bug", snapshot.GoalEvolution, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rate limiting", snapshot.GoalEvolution, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── Essence-preference (wider-mission context) ────────────────────────────────
    // When essence.json is present and has content, the fidelity source should prefer it over
    // the VIBE_STATE current block + .messages.json extraction, because the essence carries the
    // full goal-evolution / decisions / dead-ends ("wider mission") that the compacted
    // .messages.json and the VIBE_STATE latest-focus block do not.

    [Fact]
    public async Task BuildAsync_PrefersEssence_WhenPresent()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hailmary_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // VIBE_STATE says "side quest" (the latest focus).
            var vibePath = Path.Combine(tmp, "VIBE_STATE.md");
            File.WriteAllText(vibePath,
                "# VIBE STATE\n\n```json\n" +
                "{\"task\":\"Side quest task\",\"state\":\"Side quest state\",\"next\":\"Side quest next\",\"files\":\"\",\"updatedUtc\":\"2026-01-01T00:00:00Z\"}\n" +
                "```\n");

            // Essence says "wider mission" with goal evolution + decisions + dead ends.
            var essencePath = Path.Combine(tmp, "essence.json");
            File.WriteAllText(essencePath,
                "{" +
                "\"Current\":{\"Task\":\"Wider mission task\",\"State\":\"Wider mission state\",\"Next\":\"Wider mission next\",\"Files\":\"\"}," +
                "\"Evolution\":[{\"From\":\"original ask\",\"To\":\"pivoted to wider mission\",\"Why\":\"user pivoted\",\"AtUtc\":\"2026-01-02T00:00:00Z\"}]," +
                "\"IntentShifts\":[]," +
                "\"Decisions\":[\"chose approach X over Y\"]," +
                "\"DeadEnds\":[\"approach Z failed\"]," +
                "\"Files\":[\"MainViewModel.cs\"]," +
                "\"Version\":1" +
                "}");

            var source = new VibeStatePlusMessagesFidelitySource();
            var ctx = new CompactionContext
            {
                VibeStatePath = vibePath,
                EssencePath = essencePath,
                ClineMessagesPath = null,
            };

            var snapshot = await source.BuildAsync(ctx);

            // The essence's Current snapshot wins over VIBE_STATE.
            Assert.Equal("Wider mission task", snapshot.Goal);
            Assert.Equal("Wider mission state", snapshot.State);
            Assert.Equal("Wider mission next", snapshot.Next);
            // The essence's durable history wins over .messages.json extraction.
            Assert.Contains("pivoted to wider mission", snapshot.GoalEvolution, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("chose approach X over Y", snapshot.KeyDecisions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("approach Z failed", snapshot.DeadEnds, StringComparison.OrdinalIgnoreCase);
            // The essence's Files list wins.
            Assert.Contains("MainViewModel.cs", snapshot.Files, StringComparison.OrdinalIgnoreCase);
            // The source label reflects that the essence was used.
            Assert.StartsWith("ESSENCE", snapshot.SourceLabel);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_FallsBackToVibeState_WhenEssenceMissing()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hailmary_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var vibePath = Path.Combine(tmp, "VIBE_STATE.md");
            File.WriteAllText(vibePath,
                "# VIBE STATE\n\n```json\n" +
                "{\"task\":\"Vibe task\",\"state\":\"Vibe state\",\"next\":\"Vibe next\",\"files\":\"a.cs\",\"updatedUtc\":\"2026-01-01T00:00:00Z\"}\n" +
                "```\n");

            var source = new VibeStatePlusMessagesFidelitySource();
            var ctx = new CompactionContext
            {
                VibeStatePath = vibePath,
                EssencePath = Path.Combine(tmp, "does_not_exist.json"), // missing
                ClineMessagesPath = null,
            };

            var snapshot = await source.BuildAsync(ctx);

            // Falls back to the VIBE_STATE current block.
            Assert.Equal("Vibe task", snapshot.Goal);
            Assert.Equal("Vibe state", snapshot.State);
            Assert.Equal("Vibe next", snapshot.Next);
            Assert.Equal("a.cs", snapshot.Files);
            Assert.StartsWith("VIBE_STATE", snapshot.SourceLabel);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_EssenceStillPullsRecentTurnsFromMessages()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hailmary_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var essencePath = Path.Combine(tmp, "essence.json");
            File.WriteAllText(essencePath,
                "{" +
                "\"Current\":{\"Task\":\"Wider mission\",\"State\":\"state\",\"Next\":\"next\",\"Files\":\"\"}," +
                "\"Evolution\":[],\"IntentShifts\":[],\"Decisions\":[],\"DeadEnds\":[],\"Files\":[],\"Version\":1" +
                "}");

            var messagesPath = Path.Combine(tmp, "session.messages.json");
            File.WriteAllText(messagesPath,
                "{\"version\":1,\"messages\":[" +
                "{\"id\":\"m1\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"recent user turn\"}]}" +
                "]}");

            var source = new VibeStatePlusMessagesFidelitySource();
            var ctx = new CompactionContext
            {
                VibeStatePath = string.Empty,
                EssencePath = essencePath,
                ClineMessagesPath = messagesPath,
            };

            var snapshot = await source.BuildAsync(ctx);

            // Recent turns are ALWAYS pulled from the .messages.json, even when the essence is used.
            Assert.Contains("[user] recent user turn", snapshot.RecentTurns);
            Assert.Equal(1, snapshot.SourceMessageCount);
            Assert.StartsWith("ESSENCE", snapshot.SourceLabel);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── Headroom policy ────────────────────────────────────────────────────────────

    [Fact]
    public void HeadroomPolicy_TargetFreeFraction()
    {
        Assert.Equal(0.20, HailMaryHeadroomPolicy.TargetFreeFraction(HailMaryHeadroom.PreserveMore));
        Assert.Equal(0.40, HailMaryHeadroomPolicy.TargetFreeFraction(HailMaryHeadroom.Balanced));
        Assert.Equal(0.60, HailMaryHeadroomPolicy.TargetFreeFraction(HailMaryHeadroom.LeaveMoreRoom));
    }

    // ── Headroom radio-button converter (regression: unchecked radios must not clobber) ──
    // All three headroom radio buttons are two-way bound to the SAME HailMaryHeadroom property.
    // When the user checks one, the other two are unchecked and each fires ConvertBack(false).
    // The unchecked case must return the last-selected value (a no-op), NOT a hardcoded default.
    // Otherwise the last unchecked radio clobbers the property with the default (Balanced/40%),
    // making "PreserveMore (20%)" and "LeaveMoreRoom (60%)" silently collapse to 40%.

    [Fact]
    public void HeadroomConverter_Checked_PreserveMore_ReturnsPreserveMore()
    {
        var converter = new global::FluxMux.Avalonia.ViewModels.HailMaryHeadroomToBoolConverter();
        var result = converter.ConvertBack(true, typeof(HailMaryHeadroom), "PreserveMore", null);
        Assert.Equal(HailMaryHeadroom.PreserveMore, result);
    }

    [Fact]
    public void HeadroomConverter_Checked_LeaveMoreRoom_ReturnsLeaveMoreRoom()
    {
        var converter = new global::FluxMux.Avalonia.ViewModels.HailMaryHeadroomToBoolConverter();
        var result = converter.ConvertBack(true, typeof(HailMaryHeadroom), "LeaveMoreRoom", null);
        Assert.Equal(HailMaryHeadroom.LeaveMoreRoom, result);
    }

    [Fact]
    public void HeadroomConverter_Unchecked_DoesNotClobberLastSelection()
    {
        // Simulate the user checking PreserveMore, then the other two radios firing
        // ConvertBack(false) as they get unchecked. The unchecked events must NOT overwrite
        // the PreserveMore selection with the default (Balanced).
        var converter = new global::FluxMux.Avalonia.ViewModels.HailMaryHeadroomToBoolConverter();
        converter.ConvertBack(true, typeof(HailMaryHeadroom), "PreserveMore", null); // user picks 20%
        var afterUncheckBalanced = converter.ConvertBack(false, typeof(HailMaryHeadroom), "Balanced", null);
        var afterUncheckLeaveMore = converter.ConvertBack(false, typeof(HailMaryHeadroom), "LeaveMoreRoom", null);
        Assert.Equal(HailMaryHeadroom.PreserveMore, afterUncheckBalanced);
        Assert.Equal(HailMaryHeadroom.PreserveMore, afterUncheckLeaveMore);
    }

    [Fact]
    public void HeadroomConverter_SwitchingOptions_UpdatesSelection()
    {
        // The user can change their mind: checking a different option updates the selection.
        var converter = new global::FluxMux.Avalonia.ViewModels.HailMaryHeadroomToBoolConverter();
        converter.ConvertBack(true, typeof(HailMaryHeadroom), "PreserveMore", null);
        var switched = converter.ConvertBack(true, typeof(HailMaryHeadroom), "LeaveMoreRoom", null);
        Assert.Equal(HailMaryHeadroom.LeaveMoreRoom, switched);
        // And the now-unchecked PreserveMore radio does not revert it.
        var afterUncheck = converter.ConvertBack(false, typeof(HailMaryHeadroom), "PreserveMore", null);
        Assert.Equal(HailMaryHeadroom.LeaveMoreRoom, afterUncheck);
    }

    [Fact]
    public void HeadroomConverter_Convert_MatchesCurrentSelection()
    {
        var converter = new global::FluxMux.Avalonia.ViewModels.HailMaryHeadroomToBoolConverter();
        Assert.True((bool)converter.Convert(HailMaryHeadroom.PreserveMore, typeof(bool), "PreserveMore", null)!);
        Assert.False((bool)converter.Convert(HailMaryHeadroom.PreserveMore, typeof(bool), "Balanced", null)!);
        Assert.True((bool)converter.Convert(HailMaryHeadroom.Balanced, typeof(bool), "Balanced", null)!);
    }

    // ── AlreadyHasHeadroom detection ───────────────────────────────────────────────

    [Fact]
    public void AlreadyHasHeadroom_True_WhenCurrentFreeMeetsTarget()
    {
        // 1000-token window, 500 used → 50% free. Target 40% → already have it.
        var snapshot = new FidelitySnapshot
        {
            ContextLimit = 1000,
            PromptTokens = 500,
            AppliedFreeFraction = 0.40,
        };
        Assert.True(snapshot.AlreadyHasHeadroom);
    }

    [Fact]
    public void AlreadyHasHeadroom_False_WhenCurrentFreeBelowTarget()
    {
        // 1000-token window, 800 used → 20% free. Target 40% → does NOT have it.
        var snapshot = new FidelitySnapshot
        {
            ContextLimit = 1000,
            PromptTokens = 800,
            AppliedFreeFraction = 0.40,
        };
        Assert.False(snapshot.AlreadyHasHeadroom);
    }

    [Fact]
    public void AlreadyHasHeadroom_False_WhenContextUnknown()
    {
        // No context limit → can't judge → don't warn.
        var snapshot = new FidelitySnapshot
        {
            ContextLimit = 0,
            PromptTokens = 0,
            AppliedFreeFraction = 0.40,
        };
        Assert.False(snapshot.AlreadyHasHeadroom);
    }

    [Fact]
    public void AlreadyHasHeadroom_Epsilon_AvoidsRazorThinWarning()
    {
        // 1000-token window, 600 used → 40% free. Target 40% → exactly at target.
        // With the 1% epsilon, 40% >= 39% → true (we're at/above target, so warn).
        var atTarget = new FidelitySnapshot
        {
            ContextLimit = 1000,
            PromptTokens = 600,
            AppliedFreeFraction = 0.40,
        };
        Assert.True(atTarget.AlreadyHasHeadroom);

        // 1000-token window, 615 used → 38.5% free. Target 40% → below target (38.5 < 39).
        var belowTarget = new FidelitySnapshot
        {
            ContextLimit = 1000,
            PromptTokens = 615,
            AppliedFreeFraction = 0.40,
        };
        Assert.False(belowTarget.AlreadyHasHeadroom);
    }

    [Fact]
    public async Task BuildAsync_HeadroomBudget_DropsRecentTurnsFirst()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hailmary_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var messagesPath = Path.Combine(tmp, "session.messages.json");
            var msgs = new System.Text.StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                if (i > 0) msgs.Append(",");
                msgs.Append("{\"id\":\"m" + i + "\",\"role\":\"" + (i % 2 == 0 ? "user" : "assistant") +
                            "\",\"content\":[{\"type\":\"text\",\"text\":\"turn number " + i + " with some content to make it long enough to matter\"}]}");
            }

            File.WriteAllText(messagesPath, "{\"version\":1,\"messages\":[" + msgs + "]}");

            var source = new VibeStatePlusMessagesFidelitySource();
            var ctx = new CompactionContext
            {
                VibeStatePath = string.Empty,
                ClineMessagesPath = messagesPath,
                TargetFreeFraction = 0.60,
                PromptTokens = 900,
                ContextLimit = 1000,
            };

            var snapshot = await source.BuildAsync(ctx);
            Assert.True(snapshot.RecentTurns.Count < 8, "Expected recent turns to be dropped under a tight headroom budget, got " + snapshot.RecentTurns.Count);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── ExternalRescueStrategy (prepare) ───────────────────────────────────────────

    [Fact]
    public async Task ExternalRescue_Prepare_DoesNotThrow()
    {
        var strategy = new ExternalRescueStrategy();
        var ctx = new CompactionContext
        {
            VibeStatePath = string.Empty,
            ClineSessionId = null,
            ClineMessagesPath = null,
        };

        // Must return a result (success or fail) without throwing, regardless of whether a
        // Cline session exists on this machine.
        var result = await strategy.ExecuteAsync(ctx);
        Assert.NotNull(result.Message);
    }
}
