using System;
using System.IO;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.HailMary;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HailMaryStrategyTests
{
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

    // ── Headroom policy ────────────────────────────────────────────────────────────

    [Fact]
    public void HeadroomPolicy_TargetFreeFraction()
    {
        Assert.Equal(0.20, HailMaryHeadroomPolicy.TargetFreeFraction(HailMaryHeadroom.PreserveMore));
        Assert.Equal(0.40, HailMaryHeadroomPolicy.TargetFreeFraction(HailMaryHeadroom.Balanced));
        Assert.Equal(0.60, HailMaryHeadroomPolicy.TargetFreeFraction(HailMaryHeadroom.LeaveMoreRoom));
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
