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

    // ── Intent-shift capture (verbatim, bounded) ─────────────────────────────────

    [Fact]
    public void EssenceLog_RecordIntentShift_PreservesVerbatimAndPriorTask()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        log.UpdateCurrent("Fix the launch retry", "In progress", "Run tests", "a.cs");
        log.RecordIntentShift("I don't believe that's a pivot, we're still on the same goal", "Fix the launch retry");

        var reloaded = log.Load();
        Assert.Single(reloaded.IntentShifts);
        Assert.Equal("I don't believe that's a pivot, we're still on the same goal", reloaded.IntentShifts[0].Verbatim);
        Assert.Equal("Fix the launch retry", reloaded.IntentShifts[0].PriorTask);
    }

    [Fact]
    public void EssenceLog_RecordIntentShift_IsBounded()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        for (var i = 0; i < 70; i++)
        {
            log.RecordIntentShift("shift " + i, "prior task " + i);
        }

        var reloaded = log.Load();
        // Bounded to 64 (MaxIntentShifts); oldest dropped first.
        Assert.Equal(64, reloaded.IntentShifts.Count);
        // The most recent should be "shift 69".
        Assert.Equal("shift 69", reloaded.IntentShifts[reloaded.IntentShifts.Count - 1].Verbatim);
        // The oldest should be "shift 6" (0-5 dropped).
        Assert.Equal("shift 6", reloaded.IntentShifts[0].Verbatim);
    }

    [Fact]
    public void EssenceLog_RecordIntentShift_EmptyVerbatim_IsIgnored()
    {
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        log.RecordIntentShift("", "prior task");
        log.RecordIntentShift("   ", "prior task");
        Assert.Empty(log.Load().IntentShifts);
    }

    [Fact]
    public void Essence_HasContent_TrueWhenOnlyIntentShifts()
    {
        var e = Essence.Empty;
        Assert.False(e.HasContent());
        e.IntentShifts.Add(new EssenceIntentShift("user said something", "prior task", DateTimeOffset.UtcNow));
        Assert.True(e.HasContent());
    }

    [Fact]
    public void Essence_ToClineSummary_IncludesIntentShifts_Bounded()
    {
        var e = Essence.Empty;
        e.Current = new EssenceCurrent("Goal", "State", "Next", "f.cs");
        // Add 10 intent-shifts; only the most recent 8 should appear in the Cline summary.
        for (var i = 0; i < 10; i++)
        {
            e.IntentShifts.Add(new EssenceIntentShift("shift " + i, "prior " + i, DateTimeOffset.UtcNow));
        }

        var summary = e.ToClineSummary();
        Assert.Contains("## Intent-shift candidates", summary);
        // The most recent (shift 9) should be present.
        Assert.Contains("shift 9", summary);
        // An older one (shift 1) should be dropped (only most recent 8 kept).
        Assert.DoesNotContain("shift 1", summary);
        // The second-oldest kept (shift 2) should be present.
        Assert.Contains("shift 2", summary);
    }

    [Fact]
    public void Essence_ToReinjectionBlock_IncludesOnlyRecentIntentShifts()
    {
        var e = Essence.Empty;
        e.Current = new EssenceCurrent("Goal", "State", "Next", "f.cs");
        // Add 5 intent-shifts; only the most recent 2 should appear in the reinjection block.
        for (var i = 0; i < 5; i++)
        {
            e.IntentShifts.Add(new EssenceIntentShift("shift " + i, "prior " + i, DateTimeOffset.UtcNow));
        }

        var block = e.ToReinjectionBlock();
        // The most recent (shift 4) should be present.
        Assert.Contains("shift 4", block);
        // The second-most-recent (shift 3) should be present.
        Assert.Contains("shift 3", block);
        // An older one (shift 1) should be dropped (only most recent 2 kept).
        Assert.DoesNotContain("shift 1", block);
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

    [Fact]
    public void Reasserter_ClineCompaction_IncludesRecentTail()
    {
        // Regression test for the JsonNode parent-conflict bug in ReadRecentTail: when a real
        // .messages.json exists, the recent-message tail must be included in the compaction file.
        // Before the fix, tail.Add(messages[i]) threw InvalidOperationException ("The node already
        // has a parent") because messages[i] already belonged to the parsed `messages` array, so
        // the entire tail was silently dropped and the compaction file had only the summary.
        var io = new FakeContextIo();
        var log = new EssenceLog(io, "/tmp/essence.json");
        log.UpdateCurrent("Goal", "State", "Next", "f.cs");

        // Create a temp session with a real .messages.json containing 3 messages.
        var sessionId = "test_reattacher_" + Guid.NewGuid().ToString("N");
        var sessionDir = ClineCompactionWriter.GetSessionDir(sessionId);
        var messagesPath = Path.Combine(sessionDir, sessionId + ".messages.json");
        Directory.CreateDirectory(sessionDir);
        var messagesJson = "{\"version\":1,\"sessionId\":\"" + sessionId + "\",\"messages\":[" +
            "{\"id\":\"m1\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"first\"}]}," +
            "{\"id\":\"m2\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"second\"}]}," +
            "{\"id\":\"m3\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"third\"}]}" +
            "]}";
        File.WriteAllText(messagesPath, messagesJson);

        var r = new EssenceReasserter(log);
        try
        {
            var path = r.ReassertClineCompaction(sessionId, messagesPath);
            Assert.NotNull(path);
            var doc = JsonNode.Parse(File.ReadAllText(path!)) as JsonObject;
            Assert.NotNull(doc);
            var msgs = doc?["messages"] as JsonArray;
            Assert.NotNull(msgs);
            // The compaction file should have the summary message + the 3-message tail = 4 total.
            // Before the fix, this would be 1 (summary only) because the tail was dropped.
            Assert.Equal(4, msgs!.Count);
            // The last message should be the most recent from the source (m3).
            Assert.Equal("m3", msgs[3]?["id"]?.ToString());
        }
        finally
        {
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
            }
        }
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

    // ── LooksLikeCommentary (commentary vs. new-instruction detection) ───────────

    [Fact]
    public void LooksLikeCommentary_FirstPersonObservation_IsCommentary()
    {
        // The user's actual message that was mis-flagged as a pivot.
        var msg = "Looking at the telemetry I see that the vibe state goal is taken as changed as a result of my last message. I don't believe that is true. I was offering commentary relevant to the pursuit of the same goal";
        Assert.True(FluxMuxGatewayHost.LooksLikeCommentary(msg));
    }

    [Fact]
    public void LooksLikeCommentary_NegationOfChange_IsCommentary()
    {
        Assert.True(FluxMuxGatewayHost.LooksLikeCommentary("I don't believe that's a pivot, we're still on the same goal"));
        Assert.True(FluxMuxGatewayHost.LooksLikeCommentary("no change to the task, just observing the output"));
    }

    [Fact]
    public void LooksLikeCommentary_MetaReference_IsCommentary()
    {
        Assert.True(FluxMuxGatewayHost.LooksLikeCommentary("this is relevant to the current goal"));
        Assert.True(FluxMuxGatewayHost.LooksLikeCommentary("the task hasn't changed, I'm just noting something"));
    }

    [Fact]
    public void LooksLikeCommentary_Question_IsCommentary()
    {
        Assert.True(FluxMuxGatewayHost.LooksLikeCommentary("why did the model fail to launch?"));
        Assert.True(FluxMuxGatewayHost.LooksLikeCommentary("can you explain what happened with the VRAM?"));
    }

    [Fact]
    public void LooksLikeCommentary_NewInstruction_IsNotCommentary()
    {
        // A genuine new instruction should NOT be flagged as commentary.
        Assert.False(FluxMuxGatewayHost.LooksLikeCommentary("now investigate why the local model load is slow and check the VRAM usage"));
        Assert.False(FluxMuxGatewayHost.LooksLikeCommentary("switch to the smaller model and retry the launch"));
        Assert.False(FluxMuxGatewayHost.LooksLikeCommentary("implement a retry mechanism for the stuck launch state"));
    }

    [Fact]
    public void LooksLikeCommentary_Empty_IsNotCommentary()
    {
        Assert.False(FluxMuxGatewayHost.LooksLikeCommentary(""));
        Assert.False(FluxMuxGatewayHost.LooksLikeCommentary("   "));
        Assert.False(FluxMuxGatewayHost.LooksLikeCommentary(null));
    }

    // ── LooksLikePivot with commentary guard ──────────────────────────────────────

    [Fact]
    public void LooksLikePivot_CommentaryWithLowOverlap_IsNotPivot()
    {
        // The user's actual scenario: the stored task is about the launch retry fix, but the
        // user's message is commentary about the vibe-state system (low token overlap).
        // Before the fix, this was mis-flagged as a pivot. Now it should be recognized as
        // commentary and NOT flagged.
        var storedTask = "Fix the autoloaded model fails to reach launched status issue by implementing a robust retry recovery mechanism in the model launch flow";
        var userMessage = "Looking at the telemetry I see that the vibe state goal is taken as changed as a result of my last message. I don't believe that is true. I was offering commentary relevant to the pursuit of the same goal";
        Assert.False(FluxMuxGatewayHost.LooksLikePivot(storedTask, userMessage));
    }

    [Fact]
    public void LooksLikePivot_GenuineNewInstruction_IsPivot()
    {
        // A genuine new instruction with low token overlap SHOULD still be flagged as a pivot.
        var storedTask = "Fix the autoloaded model fails to reach launched status issue by implementing a robust retry recovery mechanism in the model launch flow";
        var userMessage = "now investigate the VRAM attribution discrepancy between nvidia-smi and the Settings tab and check if Epic Games is stealing VRAM";
        Assert.True(FluxMuxGatewayHost.LooksLikePivot(storedTask, userMessage));
    }

    // ── HasImperativeSignal (directive detection) ─────────────────────────────────

    [Fact]
    public void HasImperativeSignal_DirectiveVerbs_Detected()
    {
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("investigate why the load is slow"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("switch to the smaller model"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("implement a retry mechanism"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("fix the bug in the parser"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("build the new feature"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("change the architecture"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("refactor the module"));
    }

    [Fact]
    public void HasImperativeSignal_DirectiveMarkers_Detected()
    {
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("instead, focus on the API layer"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("let's try a different approach"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("I want to change the direction"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("we should prioritize the backend"));
        Assert.True(FluxMuxGatewayHost.HasImperativeSignal("go ahead and deploy the fix"));
    }

    [Fact]
    public void HasImperativeSignal_NoDirective_NotDetected()
    {
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("I see that the goal changed"));
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("why did the model fail to launch?"));
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("this is relevant to the current goal"));
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("the task hasn't changed"));
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("ok looks good"));
    }

    [Fact]
    public void HasImperativeSignal_Empty_NotDetected()
    {
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal(""));
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("   "));
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal(null));
    }

    [Fact]
    public void HasImperativeSignal_SubstringNotMatched()
    {
        // "building" should not match "build" (whole-word boundary check).
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("the building is large"));
        // "prefix" should not match "fix" (whole-word boundary check).
        Assert.False(FluxMuxGatewayHost.HasImperativeSignal("the prefix is long"));
    }

    // ── LooksLikePivot with imperative-signal gate ────────────────────────────────

    [Fact]
    public void LooksLikePivot_LowOverlapNoImperative_IsNotPivot()
    {
        // A message with low token overlap but NO imperative signal should NOT be a pivot.
        // This is the key tightening: "the model seems to be behaving oddly today" has low
        // overlap with the stored task but is an observation, not a new directive.
        var storedTask = "implement the essence-preserving compaction system for cline";
        var userMessage = "the model seems to be behaving oddly today with the new context window";
        Assert.False(FluxMuxGatewayHost.LooksLikePivot(storedTask, userMessage));
    }

    [Fact]
    public void LooksLikePivot_ShortMessageBelowThreshold_IsNotPivot()
    {
        // A 3-4 token message is below the new 5-token threshold, even if it has an imperative
        // verb. "now fix the bug" is 4 tokens — too short to be a genuine pivot.
        var storedTask = "implement the essence-preserving compaction system for cline";
        var userMessage = "now fix the bug";
        Assert.False(FluxMuxGatewayHost.LooksLikePivot(storedTask, userMessage));
    }

    [Fact]
    public void LooksLikePivot_LongMessageWithImperative_IsPivot()
    {
        // A long message (≥5 tokens) with an imperative signal and low overlap IS a pivot.
        var storedTask = "implement the essence-preserving compaction system for cline";
        var userMessage = "switch to a completely different architecture for the gateway layer";
        Assert.True(FluxMuxGatewayHost.LooksLikePivot(storedTask, userMessage));
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

    // ── TruncateForLog (human-readable diagnostic log lines) ─────────────────────

    [Fact]
    public void TruncateForLog_ShortValue_ReturnsUnchanged()
    {
        Assert.Equal("use q8_0 KV cache", FluxMuxGatewayHost.TruncateForLog("use q8_0 KV cache"));
    }

    [Fact]
    public void TruncateForLog_CollapsesWhitespaceAndNewlines()
    {
        var result = FluxMuxGatewayHost.TruncateForLog("line one\nline two   line three");
        Assert.Equal("line one line two line three", result);
    }

    [Fact]
    public void TruncateForLog_LongValue_IsTruncatedWithEllipsis()
    {
        var longText = new string('a', 500);
        var result = FluxMuxGatewayHost.TruncateForLog(longText, maxChars: 100);
        Assert.Equal(101, result.Length); // 100 chars + ellipsis
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void TruncateForLog_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FluxMuxGatewayHost.TruncateForLog(null));
        Assert.Equal(string.Empty, FluxMuxGatewayHost.TruncateForLog("   "));
    }
}