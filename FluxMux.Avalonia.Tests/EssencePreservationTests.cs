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

    // ── DistillTask (concise reflection of user instructions) ───────────────────

    [Fact]
    public void DistillTask_StripsLeadingConversationalFiller()
    {
        var result = FluxMuxGatewayHost.DistillTask("ok, can you please also read examples of the essence used at last compaction");
        Assert.Equal("read examples of the essence used at last compaction", result);
    }

    [Fact]
    public void DistillTask_StripsTrailingConversationalFiller()
    {
        var result = FluxMuxGatewayHost.DistillTask("bump the version to 0.4.0 please");
        Assert.Equal("bump the version to 0.4.0", result);
    }

    [Fact]
    public void DistillTask_CollapsesWhitespaceAndNewlines()
    {
        var result = FluxMuxGatewayHost.DistillTask("  fix   the\n\n  bug  ");
        Assert.Equal("fix the bug", result);
    }

    [Fact]
    public void DistillTask_TruncatesToBound()
    {
        var longInput = "implement " + new string('x', 300);
        var result = FluxMuxGatewayHost.DistillTask(longInput, maxChars: 50);
        Assert.True(result.Length <= 51); // 50 chars + ellipsis
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void DistillTask_PreservesSubstantiveContent()
    {
        var result = FluxMuxGatewayHost.DistillTask("check the llama.cpp FlashAttention quant patch for q4_0/q8_0 K/V types");
        Assert.Contains("llama.cpp", result);
        Assert.Contains("FlashAttention", result);
        Assert.Contains("q4_0/q8_0", result);
    }

    [Fact]
    public void DistillTask_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FluxMuxGatewayHost.DistillTask(""));
        Assert.Equal(string.Empty, FluxMuxGatewayHost.DistillTask("   "));
    }

    [Fact]
    public void DistillTask_AllFiller_FallsBackToOriginal()
    {
        var result = FluxMuxGatewayHost.DistillTask("ok");
        Assert.Equal("ok", result);
    }

    // ── LooksLikePivot (goal-pivot detection) ───────────────────────────────────

    [Fact]
    public void LooksLikePivot_ShortConfirmation_IsNotPivot()
    {
        Assert.False(FluxMuxGatewayHost.LooksLikePivot("implement the essence-preserving compaction system", "ok rebuilt"));
    }

    [Fact]
    public void LooksLikePivot_SimilarMessage_IsNotPivot()
    {
        Assert.False(FluxMuxGatewayHost.LooksLikePivot(
            "implement the essence-preserving compaction system for cline",
            "continue implementing the essence-preserving compaction system"));
    }

    [Fact]
    public void LooksLikePivot_DivergentMessage_IsPivot()
    {
        Assert.True(FluxMuxGatewayHost.LooksLikePivot(
            "implement the essence-preserving compaction system for cline",
            "investigate why the local model load is slow and check the vram usage"));
    }

    [Fact]
    public void LooksLikePivot_EmptyStoredTask_IsNotPivot()
    {
        Assert.False(FluxMuxGatewayHost.LooksLikePivot("", "do something completely different here"));
    }

    // ── ExtractDecisions (decision capture) ─────────────────────────────────────

    [Fact]
    public void ExtractDecisions_CapturesExplicitDecision()
    {
        var text = "After analysis, I'll summarize.\nDecision: use symmetric q8_0/q8_0 KV cache for now to avoid the f16 fallback.\nMore text.";
        var results = FluxMuxGatewayHost.ExtractDecisions(text);
        Assert.Single(results);
        Assert.Contains("q8_0/q8_0", results[0]);
    }

    [Fact]
    public void ExtractDecisions_IgnoresMarkerListingLines()
    {
        var text = "- \"Decision:\", \"I decided\", \"We'll use\", \"Going with\", \"Chose to\", \"Opted for\", \"Decided to\"";
        var results = FluxMuxGatewayHost.ExtractDecisions(text);
        Assert.Empty(results);
    }

    [Fact]
    public void ExtractDecisions_IgnoresMarkerAlone()
    {
        var text = "Decision:";
        var results = FluxMuxGatewayHost.ExtractDecisions(text);
        Assert.Empty(results);
    }

    [Fact]
    public void ExtractDecisions_NoMarkers_ReturnsEmpty()
    {
        var text = "This is just a normal paragraph with no decision markers at all.";
        var results = FluxMuxGatewayHost.ExtractDecisions(text);
        Assert.Empty(results);
    }

    // ── ExtractDeadEnds (dead-end capture) ──────────────────────────────────────

    [Fact]
    public void ExtractDeadEnds_CapturesExplicitDeadEnd()
    {
        var text = "I tried approach A.\nDead end: approach A doesn't work because the kernel is missing.\nMoving on.";
        var results = FluxMuxGatewayHost.ExtractDeadEnds(text);
        Assert.Single(results);
        Assert.Contains("kernel is missing", results[0]);
    }

    [Fact]
    public void ExtractDeadEnds_IgnoresMarkerListingLines()
    {
        var text = "- \"dead end\", \"doesn't work\", \"failed\", \"abandoned\", \"gave up on\", \"not viable\", \"no good\"";
        var results = FluxMuxGatewayHost.ExtractDeadEnds(text);
        Assert.Empty(results);
    }

    [Fact]
    public void ExtractDeadEnds_NoMarkers_ReturnsEmpty()
    {
        var text = "This is just a normal paragraph with no dead-end markers at all.";
        var results = FluxMuxGatewayHost.ExtractDeadEnds(text);
        Assert.Empty(results);
    }
}