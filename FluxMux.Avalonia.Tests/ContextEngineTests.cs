using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.ContextEngine;
using Xunit;
using ContextEngineType = FluxMux.Avalonia.Services.ContextEngine.ContextEngine;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// In-memory <see cref="IContextIo"/> for tests: no filesystem, just a dictionary. Lets the
/// <see cref="ContextEngine"/> be exercised in isolation.
/// </summary>
internal sealed class FakeContextIo : IContextIo
{
    public Dictionary<string, string> Files { get; } = new();

    public bool Exists(string path) => Files.ContainsKey(path);

    public string? ReadAllText(string path) => Files.TryGetValue(path, out var value) ? value : null;

    public void WriteAllTextResilient(string path, string contents) => Files[path] = contents;
}

public class VibeStateTests
{
    [Fact]
    public void JsonRoundTrip_PreservesAllFields()
    {
        var original = new VibeState
        {
            Task = "Refactor the config service",
            State = "Step 0 done; Step 1 in progress",
            Next = "Create the ContextEngine module",
            Files = "Services/FluxMuxConfigService.cs, Services/AtomicFile.cs",
            UpdatedUtc = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero),
        };

        var json = original.ToJson();
        var parsed = VibeState.FromJson(json);

        Assert.Equal(original.Task, parsed.Task);
        Assert.Equal(original.State, parsed.State);
        Assert.Equal(original.Next, parsed.Next);
        Assert.Equal(original.Files, parsed.Files);
        Assert.Equal(original.UpdatedUtc, parsed.UpdatedUtc);
    }

    [Fact]
    public void FromJson_NullOrBlank_ReturnsEmpty()
    {
        Assert.True(VibeState.FromJson(null).IsEmpty);
        Assert.True(VibeState.FromJson("").IsEmpty);
        Assert.True(VibeState.FromJson("   ").IsEmpty);
    }

    [Fact]
    public void IsEmpty_TrueOnlyWhenAllFieldsBlank()
    {
        Assert.True(VibeState.Empty.IsEmpty);
        Assert.False(new VibeState { Task = "x" }.IsEmpty);
        Assert.False(new VibeState { Next = "y" }.IsEmpty);
    }

    [Fact]
    public void WithTimestamp_SetsUpdatedUtc()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var stamped = VibeState.Empty.WithTimestamp(now);
        Assert.Equal(now, stamped.UpdatedUtc);
        Assert.Null(VibeState.Empty.UpdatedUtc); // original unchanged (record copy)
    }
}

public class VibeStateMarkdownTests
{
    [Fact]
    public void Render_ContainsJsonBlockAndSummary()
    {
        var state = new VibeState { Task = "T", State = "S", Next = "N", Files = "F" };
        var md = VibeStateMarkdown.Render(state);

        Assert.Contains("```json", md);
        Assert.Contains("\"task\":\"T\"", md);
        Assert.Contains("**Task:** T", md);
        Assert.Contains("**Next:** N", md);
    }

    [Fact]
    public void ExtractJsonBlock_RoundTripsThroughRender()
    {
        var state = new VibeState
        {
            Task = "Refactor",
            State = "In progress",
            Next = "Test it",
            Files = "a.cs, b.cs",
        };
        var md = VibeStateMarkdown.Render(state);
        var json = VibeStateMarkdown.ExtractJsonBlock(md);

        Assert.NotNull(json);
        var parsed = VibeState.FromJson(json);
        Assert.Equal(state.Task, parsed.Task);
        Assert.Equal(state.State, parsed.State);
        Assert.Equal(state.Next, parsed.Next);
        Assert.Equal(state.Files, parsed.Files);
    }

    [Fact]
    public void ExtractJsonBlock_NoFence_ReturnsNull()
    {
        Assert.Null(VibeStateMarkdown.ExtractJsonBlock("just some prose, no json"));
        Assert.Null(VibeStateMarkdown.ExtractJsonBlock(""));
    }

    [Fact]
    public void BuildReinjectionBlock_IncludesTaskStateNext()
    {
        var state = new VibeState { Task = "T", State = "S", Next = "N", Files = "F" };
        var block = VibeStateMarkdown.BuildReinjectionBlock(state);

        Assert.Contains("Task: T", block);
        Assert.Contains("State: S", block);
        Assert.Contains("Next: N", block);
        Assert.Contains("Key files: F", block);
    }

    [Fact]
    public void BuildReinjectionBlock_OmitsFilesWhenBlank()
    {
        var state = new VibeState { Task = "T", State = "S", Next = "N" };
        var block = VibeStateMarkdown.BuildReinjectionBlock(state);
        Assert.DoesNotContain("Key files:", block);
    }
}

public class ContextEngineTests
{
    private static ContextEngineType CreateEngine(
        FakeContextIo io,
        bool vibeEnabled = true,
        bool pagingEnabled = true)
    {
        var options = new ContextEngineOptions
        {
            IsVibeStateEnabled = () => vibeEnabled,
            IsContextPagingEnabled = () => pagingEnabled,
        };
        return new ContextEngineType(io, options);
    }

    [Fact]
    public void IsVibeStateEnabled_ReflectsDelegate()
    {
        var io = new FakeContextIo();
        Assert.True(CreateEngine(io, vibeEnabled: true).IsVibeStateEnabled());
        Assert.False(CreateEngine(io, vibeEnabled: false).IsVibeStateEnabled());
    }

    [Fact]
    public void IsContextPagingEnabled_ReflectsDelegate()
    {
        var io = new FakeContextIo();
        Assert.True(CreateEngine(io, pagingEnabled: true).IsContextPagingEnabled());
        Assert.False(CreateEngine(io, pagingEnabled: false).IsContextPagingEnabled());
    }

    [Fact]
    public void LoadVibeState_MissingFile_ReturnsEmpty()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io);
        Assert.True(engine.LoadVibeState("/tmp/vibe.md").IsEmpty);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThroughMarkdown()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io);
        var path = "/tmp/vibe.md";

        var state = new VibeState { Task = "T", State = "S", Next = "N", Files = "F" };
        engine.SaveVibeState(path, state);

        Assert.True(io.Exists(path));
        var loaded = engine.LoadVibeState(path);
        Assert.Equal("T", loaded.Task);
        Assert.Equal("S", loaded.State);
        Assert.Equal("N", loaded.Next);
        Assert.Equal("F", loaded.Files);
        Assert.NotNull(loaded.UpdatedUtc); // stamped on save
    }

    [Fact]
    public void SaveVibeState_WritesHumanReadableMarkdown()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io);
        var path = "/tmp/vibe.md";

        engine.SaveVibeState(path, new VibeState { Task = "T", State = "S", Next = "N" });

        var written = io.Files[path];
        Assert.Contains("# VIBE STATE", written);
        Assert.Contains("```json", written);
        Assert.Contains("**Task:** T", written);
    }

    [Fact]
    public void BuildReinjectionBlock_EmptyWhenDisabled()
    {
        var io = new FakeContextIo();
        var path = "/tmp/vibe.md";
        io.Files[path] = VibeStateMarkdown.Render(new VibeState { Task = "T", State = "S", Next = "N" });

        var engine = CreateEngine(io, vibeEnabled: false);
        Assert.Equal(string.Empty, engine.BuildReinjectionBlock(path));
    }

    [Fact]
    public void BuildReinjectionBlock_EmptyWhenNoState()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io);
        Assert.Equal(string.Empty, engine.BuildReinjectionBlock("/tmp/does-not-exist.md"));
    }

    [Fact]
    public void BuildReinjectionBlock_ReturnsBlockWhenStatePresent()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io);
        var path = "/tmp/vibe.md";
        io.Files[path] = VibeStateMarkdown.Render(new VibeState { Task = "T", State = "S", Next = "N" });

        var block = engine.BuildReinjectionBlock(path);
        Assert.Contains("Task: T", block);
        Assert.Contains("Next: N", block);
    }

    [Fact]
    public void PageContext_NoOpWhenDisabled()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io, pagingEnabled: false);
        var payload = new JsonObject { ["messages"] = new JsonArray() };

        var result = engine.PageContext(payload, contextTokens: 1000);
        Assert.False(result.Pruned);
    }

    [Fact]
    public void PageContext_DelegatesToForwardPrune_WhenEnabled()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io, pagingEnabled: true);

        // A payload already under the window: forward-prune short-circuits to None.
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "hi" },
            },
        };

        var result = engine.PageContext(payload, contextTokens: 1_000_000);
        Assert.False(result.Pruned); // pipeline ran but found no waste
    }

    [Fact]
    public void LoadVibeState_CorruptJson_ReturnsEmpty()
    {
        var io = new FakeContextIo();
        var engine = CreateEngine(io);
        var path = "/tmp/vibe.md";
        io.Files[path] = "# VIBE STATE\n```json\n{ not valid json }\n```\n";

        Assert.True(engine.LoadVibeState(path).IsEmpty);
    }
}

public class ContextSwapStoreTests
{
    private const string Path = "/tmp/context_swap.json";

    [Fact]
    public void Spill_ReturnsStableIdAndPersistsBody()
    {
        var io = new FakeContextIo();
        var store = new ContextSwapStore(io, Path);

        var id = store.Spill("hello world body");
        Assert.StartsWith("swap_", id);
        Assert.True(io.Exists(Path));

        var (text, total, _) = store.ReadPage(id, page: 0);
        Assert.Equal("hello world body", text);
        Assert.Equal(16, total);
    }

    [Fact]
    public void Spill_IsIdempotentForSameBody()
    {
        var io = new FakeContextIo();
        var store = new ContextSwapStore(io, Path);

        var id1 = store.Spill("same body");
        var id2 = store.Spill("same body");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Spill_EmptyBody_ReturnsEmptyString()
    {
        var io = new FakeContextIo();
        var store = new ContextSwapStore(io, Path);
        Assert.Equal(string.Empty, store.Spill(""));
        Assert.Equal(string.Empty, store.Spill(null));
    }

    [Fact]
    public void ReadPage_PaginatesLargeBody()
    {
        var io = new FakeContextIo();
        var store = new ContextSwapStore(io, Path);

        var body = new string('a', 100);
        var id = store.Spill(body);

        var page0 = store.ReadPage(id, page: 0, pageSize: 40);
        Assert.Equal(40, page0.Text.Length);
        Assert.True(page0.HasMore);
        Assert.Equal(100, page0.TotalBytes);

        var page1 = store.ReadPage(id, page: 1, pageSize: 40);
        Assert.Equal(40, page1.Text.Length);
        Assert.True(page1.HasMore);

        var page2 = store.ReadPage(id, page: 2, pageSize: 40);
        Assert.Equal(20, page2.Text.Length);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public void ReadPage_UnknownId_ReturnsEmpty()
    {
        var io = new FakeContextIo();
        var store = new ContextSwapStore(io, Path);
        var (text, total, hasMore) = store.ReadPage("swap_doesnotexist");
        Assert.Equal(string.Empty, text);
        Assert.Equal(0, total);
        Assert.False(hasMore);
    }

    [Fact]
    public void PurgeOlderThan_RemovesOldEntries()
    {
        var io = new FakeContextIo();
        var store = new ContextSwapStore(io, Path);

        var idOld = store.Spill("old body");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(io.Files[Path]) as System.Text.Json.Nodes.JsonObject;
        doc["entries"][idOld]["createdUtc"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
        io.Files[Path] = doc.ToJsonString();

        var idNew = store.Spill("new body");

        var removed = store.PurgeOlderThan(DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(1, removed);

        Assert.Equal(string.Empty, store.ReadPage(idOld).Text);
        Assert.Equal("new body", store.ReadPage(idNew).Text);
    }

    [Fact]
    public void EnsureExists_CreatesEmptyFile()
    {
        var io = new FakeContextIo();
        var store = new ContextSwapStore(io, Path);
        Assert.False(io.Exists(Path));

        store.EnsureExists();
        Assert.True(io.Exists(Path));
    }

    [Fact]
    public void CorruptFile_ReadPage_ReturnsEmpty()
    {
        var io = new FakeContextIo();
        io.Files[Path] = "{ not valid json }";
        var store = new ContextSwapStore(io, Path);
        var (text, total, _) = store.ReadPage("swap_anything");
        Assert.Equal(string.Empty, text);
        Assert.Equal(0, total);
    }
}

public class McpControlPlaneTests
{
    private sealed class FakeGit : IGitRunner
    {
        public bool ShouldSucceed { get; set; } = true;
        public string? LastMessage { get; private set; }
        public string? LastWorkDir { get; private set; }

        public (bool Ok, string Message) CreateCheckpoint(string workDir, string message)
        {
            LastWorkDir = workDir;
            LastMessage = message;
            return ShouldSucceed ? (true, "checkpoint created") : (false, "git failed");
        }
    }

    private static (McpControlPlane Plane, FakeContextIo Io, FakeGit Git) CreatePlane()
    {
        var io = new FakeContextIo();
        var engine = new ContextEngineType(io, new ContextEngineOptions());
        var store = new ContextSwapStore(io, "/tmp/context_swap.json");
        var git = new FakeGit();
        var plane = new McpControlPlane(engine, store, git, "/tmp/vibe.md", "/work");
        return (plane, io, git);
    }

    [Fact]
    public void UpdateVibeState_PersistsAndReturnsFields()
    {
        var (plane, io, _) = CreatePlane();
        var body = new JsonObject
        {
            ["tool"] = "update_vibe_state",
            ["args"] = new JsonObject
            {
                ["task"] = "Refactor",
                ["state"] = "In progress",
                ["next"] = "Test it",
            },
        };

        var result = plane.Handle(body);
        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Equal("Refactor", result["task"]?.ToString());
        Assert.Equal("In progress", result["state"]?.ToString());
        Assert.Equal("Test it", result["next"]?.ToString());
        Assert.True(io.Exists("/tmp/vibe.md"));
    }

    [Fact]
    public void UpdateVibeState_MergesWithExisting()
    {
        var (plane, io, _) = CreatePlane();
        io.Files["/tmp/vibe.md"] = VibeStateMarkdown.Render(new VibeState { Task = "Old", State = "S", Next = "N" });

        var body = new JsonObject
        {
            ["tool"] = "update_vibe_state",
            ["args"] = new JsonObject { ["next"] = "New next" },
        };

        var result = plane.Handle(body);
        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Equal("Old", result["task"]?.ToString());
        Assert.Equal("New next", result["next"]?.ToString());
    }

    [Fact]
    public void ReadSwapLog_UnknownId_ReturnsError()
    {
        var (plane, _, _) = CreatePlane();
        var body = new JsonObject
        {
            ["tool"] = "read_swap_log",
            ["args"] = new JsonObject { ["swap_id"] = "swap_missing" },
        };

        var result = plane.Handle(body);
        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("unknown swap_id", result["error"]?.ToString());
    }

    [Fact]
    public void ReadSwapLog_MissingSwapId_ReturnsError()
    {
        var (plane, _, _) = CreatePlane();
        var body = new JsonObject
        {
            ["tool"] = "read_swap_log",
            ["args"] = new JsonObject(),
        };

        var result = plane.Handle(body);
        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("missing 'swap_id'", result["error"]?.ToString());
    }

    [Fact]
    public void GitCheckpoint_Success()
    {
        var (plane, _, git) = CreatePlane();
        var body = new JsonObject
        {
            ["tool"] = "git_checkpoint",
            ["args"] = new JsonObject { ["message"] = "my checkpoint" },
        };

        var result = plane.Handle(body);
        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Equal("my checkpoint", git.LastMessage);
        Assert.Equal("/work", git.LastWorkDir);
    }

    [Fact]
    public void GitCheckpoint_Failure()
    {
        var (plane, _, git) = CreatePlane();
        git.ShouldSucceed = false;

        var body = new JsonObject
        {
            ["tool"] = "git_checkpoint",
            ["args"] = new JsonObject(),
        };

        var result = plane.Handle(body);
        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("git failed", result["error"]?.ToString());
    }

    [Fact]
    public void UnknownTool_ReturnsError()
    {
        var (plane, _, _) = CreatePlane();
        var result = plane.Handle(new JsonObject { ["tool"] = "nope" });
        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("unknown tool", result["error"]?.ToString());
    }

    [Fact]
    public void MissingTool_ReturnsError()
    {
        var (plane, _, _) = CreatePlane();
        var result = plane.Handle(new JsonObject());
        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("missing 'tool'", result["error"]?.ToString());
    }

    [Fact]
    public void ListTools_ReturnsThreeTools()
    {
        var (plane, _, _) = CreatePlane();
        var tools = plane.ListTools();
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t["name"]?.ToString() == "update_vibe_state");
        Assert.Contains(tools, t => t["name"]?.ToString() == "read_swap_log");
        Assert.Contains(tools, t => t["name"]?.ToString() == "git_checkpoint");
    }
}