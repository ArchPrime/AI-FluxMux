using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.ContextEngine;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Tests for the essence-preserving compaction feature: the durable essence log, the
/// compaction-edge detector, the essence reassurer, and the rich Cline compaction writer.
/// </summary>
public sealed class EssencePreservationTests
{
    // ── EssenceLog ───────────────────────────────────────────────────────────────

    [Fact]
    public void EssenceLog_RoundTrip_PreservesAllSections()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        log.UpdateCurrent("Fix the bug", "Done: read code", "Run tests", "a.cs,b.cs");
        log.RecordEvolution("Old goal", "New goal", "user pivoted");
        log.RecordDecision("Chose approach X");
        log.RecordDeadEnd("Approach Y failed");
        log.RecordFile("c.cs");

        var reloaded = log.Load();
        Assert.Equal("New goal", reloaded.Current.Task);
        Assert.Equal("Done: read code", reloaded.Current.State);
        Assert.Equal("Run tests", reloaded.Current.Next);
        Assert.Single(reloaded.Evolution);
        Assert.Equal("Old goal", reloaded.Evolution[0].From);
        Assert.Equal("New goal", reloaded.Evolution[0].To);
        Assert.Contains("Chose approach X", reloaded.Decisions);
        Assert.Contains("Approach Y failed", reloaded.DeadEnds);
        Assert.Contains("c.cs", reloaded.Files);
    }

    [Fact]
    public void EssenceLog_RecordFile_IsDistinct()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        log.RecordFile("a.cs");
        log.RecordFile("a.cs"); // duplicate — should not add again
        log.RecordFile("b.cs");
        Assert.Equal(2, log.Load().Files.Count);
    }

    [Fact]
    public void EssenceLog_MissingFile_ReturnsEmpty()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/missing.json");
        Assert.True(log.Load().Current.IsEmpty);
        Assert.False(log.HasContent());
    }

    [Fact]
    public void EssenceLog_UpdateCurrent_BlankArgsPreserveExisting()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        log.UpdateCurrent("Task", "State", "Next", "Files");
        log.UpdateCurrent(null, "NewState", null, null);
        var e = log.Load();
        Assert.Equal("Task", e.Current.Task);
        Assert.Equal("NewState", e.Current.State);
        Assert.Equal("Next", e.Current.Next);
        Assert.Equal("Files", e.Current.Files);
    }

    [Fact]
    public void Essence_ToClineSummary_IncludesEvolutionAndDeadEnds()
    {
        var e = Essence.Empty;
        e.Current = new EssenceCurrent("Goal", "State", "Next", "f.cs");
        e.Evolution.Add(new EssenceEvolutionEntry("A", "B", "why", DateTimeOffset.UtcNow));
        e.DeadEnds.Add("don't retry X");
        e.Decisions.Add("chose Y");
        var summary = e.ToClineSummary();
        Assert.Contains("## Goal", summary);
        Assert.Contains("## Goal evolution", summary);
        Assert.Contains("A → B", summary);
        Assert.Contains("## Key decisions", summary);
        Assert.Contains("chose Y", summary);
        Assert.Contains("## Dead ends", summary);
        Assert.Contains("don't retry X", summary);
        Assert.Contains("## Files", summary);
        Assert.Contains("f.cs", summary);
    }

    [Fact]
    public void Essence_ToReinjectionBlock_IsTerse()
    {
        var e = Essence.Empty;
        e.Current = new EssenceCurrent("Goal", "State", "Next", "f.cs");
        e.Evolution.Add(new EssenceEvolutionEntry("A", "B", "why", DateTimeOffset.UtcNow));
        var block = e.ToReinjectionBlock();
        Assert.Contains("[ESSENCE", block);
        Assert.Contains("Goal", block);
        Assert.Contains("Last goal change: A → B", block);
        Assert.DoesNotContain("## Key decisions", block);
    }

    // ── CompactionEdgeDetector ───────────────────────────────────────────────────

    [Fact]
    public void EdgeDetector_FirstTurn_IsEdge()
    {
        var d = new CompactionEdgeDetector();
        Assert.True(d.Observe(5000, 128000, "s1", "m"));
    }

    [Fact]
    public void EdgeDetector_SteadyRise_NotEdge()
    {
        var d = new CompactionEdgeDetector();
        d.Observe(4000, 128000, "s1", "m");
        Assert.False(d.Observe(5000, 128000, "s1", "m"));
        Assert.False(d.Observe(6000, 128000, "s1", "m"));
    }

    [Fact]
    public void EdgeDetector_SharpDrop_IsEdge()
    {
        var d = new CompactionEdgeDetector();
        d.Observe(100000, 128000, "s1", "m");
        Assert.True(d.Observe(30000, 128000, "s1", "m")); // 70% drop
    }

    [Fact]
    public void EdgeDetector_SmallEarlyDrop_NotEdge()
    {
        var d = new CompactionEdgeDetector();
        d.Observe(1000, 128000, "s1", "m"); // below MinPreviousTokens
        Assert.False(d.Observe(100, 128000, "s1", "m"));
    }

    [Fact]
    public void EdgeDetector_NewSession_IsEdge()
    {
        var d = new CompactionEdgeDetector();
        d.Observe(5000, 128000, "s1", "m");
        Assert.True(d.Observe(5000, 128000, "s2", "m")); // different session
    }

    [Fact]
    public void EdgeDetector_ContextLimitJump_IsEdge()
    {
        var d = new CompactionEdgeDetector();
        d.Observe(5000, 128000, "s1", "m");
        Assert.True(d.Observe(5000, 32000, "s1", "m")); // window shrank 75%
    }

    [Fact]
    public void EdgeDetector_SameSessionStable_NotEdgeAfterFirst()
    {
        var d = new CompactionEdgeDetector();
        Assert.True(d.Observe(5000, 128000, "s1", "m")); // first → edge
        Assert.False(d.Observe(5200, 128000, "s1", "m")); // steady → not edge
        Assert.False(d.Observe(5400, 128000, "s1", "m"));
    }

    // ── EssenceReasserter ────────────────────────────────────────────────────────

    [Fact]
    public void Reasserter_NoEssence_ReturnsNullForCline()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        var r = new EssenceReasserter(log);
        Assert.Null(r.ReassertClineCompaction("sess123", null));
        Assert.Equal(string.Empty, r.BuildLocalInjectionBlock());
    }

    [Fact]
    public void Reasserter_WithEssence_BuildsInjectionBlock()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        log.UpdateCurrent("Goal", "State", "Next", "f.cs");
        var r = new EssenceReasserter(log);
        var block = r.BuildLocalInjectionBlock();
        Assert.Contains("[ESSENCE", block);
        Assert.Contains("Goal", block);
    }

    // ── ClineCompactionWriter rich format ────────────────────────────────────────

    [Fact]
    public void WriteCompactionFileRich_IncludesSummaryAndTail()
    {
        // Use a temp session id to avoid clobbering real Cline data.
        var sessionId = "test_" + Guid.NewGuid().ToString("N");
        var summary = "## Goal\nTest goal";
        var tail = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hi" },
        };

        var path = ClineCompactionWriter.WriteCompactionFileRich(sessionId, summary, 42, "id:msg_x", tail);
        try
        {
            Assert.NotNull(path);
            var json = File.ReadAllText(path!);
            var doc = JsonNode.Parse(json) as JsonObject;
            Assert.NotNull(doc);
            Assert.Equal(42, doc?["source_message_count"]?.GetValue<int>());
            Assert.Equal("id:msg_x", doc?["source_last_message_key"]?.ToString());
            var msgs = doc?["messages"] as JsonArray;
            Assert.NotNull(msgs);
            Assert.Equal(2, msgs!.Count); // summary + 1 tail
            Assert.Equal("compaction_summary", msgs[0]?["metadata"]?["kind"]?.ToString());
            Assert.Contains("Test goal", msgs[0]?["content"]?[0]?["text"]?.ToString());
        }
        finally
        {
            var dir = ClineCompactionWriter.GetSessionDir(sessionId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void WriteCompactionFileRich_NullTail_SummaryOnly()
    {
        var sessionId = "test_" + Guid.NewGuid().ToString("N");
        var path = ClineCompactionWriter.WriteCompactionFileRich(sessionId, "## Goal\nG", 7, "id:m", null);
        try
        {
            Assert.NotNull(path);
            var doc = JsonNode.Parse(File.ReadAllText(path!)) as JsonObject;
            var msgs = doc?["messages"] as JsonArray;
            Assert.Equal(1, msgs?.Count); // summary only
        }
        finally
        {
            var dir = ClineCompactionWriter.GetSessionDir(sessionId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}