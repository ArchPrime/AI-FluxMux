using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.ContextEngine;
using Xunit;
using ContextEngineType = FluxMux.Avalonia.Services.ContextEngine.ContextEngine;

namespace FluxMux.Avalonia.Tests.ContextEngine;

/// <summary>
/// Tests for the <see cref="McpControlPlane"/>: the three tools the model can call to manage the
/// paged-context capability (update_vibe_state, read_swap_log, git_checkpoint).
/// </summary>
public sealed class McpControlPlaneTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _vibeStatePath;
    private readonly string _swapStorePath;
    private readonly FakeContextIo _io;
    private readonly ContextEngineType _engine;
    private readonly ContextSwapStore _swapStore;
    private readonly FakeGitRunner _git;
    private readonly McpControlPlane _plane;

    public McpControlPlaneTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fluxmux-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _vibeStatePath = Path.Combine(_tempDir, "VIBE_STATE.md");
        _swapStorePath = Path.Combine(_tempDir, "context_swap.json");
        _io = new FakeContextIo();
        _engine = new ContextEngineType(_io, new ContextEngineOptions());
        _swapStore = new ContextSwapStore(_io, _swapStorePath);
        _git = new FakeGitRunner();
        _plane = new McpControlPlane(_engine, _swapStore, _git, _vibeStatePath, _tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // ── update_vibe_state ──────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateVibeState_WritesAllFields()
    {
        var body = new JsonObject
        {
            ["tool"] = "update_vibe_state",
            ["args"] = new JsonObject
            {
                ["task"] = "Fix the bug",
                ["state"] = "In progress",
                ["next"] = "Run tests",
                ["files"] = "src/main.cs",
            }
        };

        var result = _plane.Handle(body);

        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Equal("Fix the bug", result["task"]?.GetValue<string>());
        Assert.Equal("In progress", result["state"]?.GetValue<string>());
        Assert.Equal("Run tests", result["next"]?.GetValue<string>());
        Assert.Equal("src/main.cs", result["files"]?.GetValue<string>());

        // Verify the file was written.
        var loaded = _engine.LoadVibeState(_vibeStatePath);
        Assert.Equal("Fix the bug", loaded.Task);
        Assert.Equal("In progress", loaded.State);
        Assert.Equal("Run tests", loaded.Next);
        Assert.Equal("src/main.cs", loaded.Files);
    }

    [Fact]
    public void UpdateVibeState_PreservesExistingFieldsWhenOmitted()
    {
        // Seed the state.
        _engine.SaveVibeState(_vibeStatePath, new VibeState
        {
            Task = "Original task",
            State = "Original state",
            Next = "Original next",
            Files = "original.cs",
        });

        // Update only the task.
        var body = new JsonObject
        {
            ["tool"] = "update_vibe_state",
            ["args"] = new JsonObject
            {
                ["task"] = "New task",
            }
        };

        var result = _plane.Handle(body);

        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Equal("New task", result["task"]?.GetValue<string>());
        Assert.Equal("Original state", result["state"]?.GetValue<string>());
        Assert.Equal("Original next", result["next"]?.GetValue<string>());
        Assert.Equal("original.cs", result["files"]?.GetValue<string>());
    }

    [Fact]
    public void UpdateVibeState_EmptyArgs_ReturnsCurrentState()
    {
        // Seed the state.
        _engine.SaveVibeState(_vibeStatePath, new VibeState
        {
            Task = "Task",
            State = "State",
            Next = "Next",
            Files = "files.cs",
        });

        var body = new JsonObject
        {
            ["tool"] = "update_vibe_state",
            ["args"] = new JsonObject()
        };

        var result = _plane.Handle(body);

        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Equal("Task", result["task"]?.GetValue<string>());
        Assert.Equal("State", result["state"]?.GetValue<string>());
    }

    // ── read_swap_log ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadSwapLog_ReturnsPageContent()
    {
        // Create a swap entry.
        var largeText = new string('x', 500);
        var swapId = _swapStore.Spill(largeText);

        var body = new JsonObject
        {
            ["tool"] = "read_swap_log",
            ["args"] = new JsonObject
            {
                ["swap_id"] = swapId,
                ["page"] = 0,
                ["page_size"] = 100,
            }
        };

        var result = _plane.Handle(body);

        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Equal(swapId, result["swap_id"]?.GetValue<string>());
        Assert.Equal(0, result["page"]?.GetValue<int>());
        Assert.Equal(100, result["text"]?.GetValue<string>()?.Length);
        Assert.True(result["has_more"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public void ReadSwapLog_UnknownSwapId_ReturnsError()
    {
        var body = new JsonObject
        {
            ["tool"] = "read_swap_log",
            ["args"] = new JsonObject
            {
                ["swap_id"] = "swp_unknown",
            }
        };

        var result = _plane.Handle(body);

        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("unknown swap_id", result["error"]?.GetValue<string>());
    }

    [Fact]
    public void ReadSwapLog_MissingSwapId_ReturnsError()
    {
        var body = new JsonObject
        {
            ["tool"] = "read_swap_log",
            ["args"] = new JsonObject()
        };

        var result = _plane.Handle(body);

        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("missing 'swap_id'", result["error"]?.GetValue<string>());
    }

    // ── git_checkpoint ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GitCheckpoint_CallsGitRunner()
    {
        var body = new JsonObject
        {
            ["tool"] = "git_checkpoint",
            ["args"] = new JsonObject
            {
                ["message"] = "test checkpoint",
            }
        };

        var result = _plane.Handle(body);

        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Contains("test checkpoint", result["message"]?.GetValue<string>());
        Assert.Equal(1, _git.CheckpointCount);
        Assert.Equal("test checkpoint", _git.LastMessage);
    }

    [Fact]
    public void GitCheckpoint_DefaultMessage_UsesFallback()
    {
        var body = new JsonObject
        {
            ["tool"] = "git_checkpoint",
            ["args"] = new JsonObject()
        };

        var result = _plane.Handle(body);

        Assert.True(result["ok"]?.GetValue<bool>() ?? false);
        Assert.Contains("context checkpoint", result["message"]?.GetValue<string>());
    }

    [Fact]
    public void GitCheckpoint_GitFailure_ReturnsError()
    {
        _git.FailNext = true;

        var body = new JsonObject
        {
            ["tool"] = "git_checkpoint",
            ["args"] = new JsonObject
            {
                ["message"] = "test",
            }
        };

        var result = _plane.Handle(body);

        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("git failed", result["error"]?.GetValue<string>());
    }

    // ── Unknown tool / missing tool ────────────────────────────────────────────────────

    [Fact]
    public void UnknownTool_ReturnsError()
    {
        var body = new JsonObject
        {
            ["tool"] = "unknown_tool",
            ["args"] = new JsonObject()
        };

        var result = _plane.Handle(body);

        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("unknown tool", result["error"]?.GetValue<string>());
    }

    [Fact]
    public void MissingTool_ReturnsError()
    {
        var body = new JsonObject
        {
            ["args"] = new JsonObject()
        };

        var result = _plane.Handle(body);

        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Contains("missing 'tool'", result["error"]?.GetValue<string>());
    }

    // ── ListTools ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ListTools_ReturnsAllThreeTools()
    {
        var tools = _plane.ListTools();

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t?["name"]?.GetValue<string>() == "update_vibe_state");
        Assert.Contains(tools, t => t?["name"]?.GetValue<string>() == "read_swap_log");
        Assert.Contains(tools, t => t?["name"]?.GetValue<string>() == "git_checkpoint");
    }

    // ── Fake Git runner for tests ──────────────────────────────────────────────────────

    private sealed class FakeGitRunner : IGitRunner
    {
        public int CheckpointCount { get; private set; }
        public string? LastMessage { get; private set; }
        public bool FailNext { get; set; }

        public (bool Ok, string Message) CreateCheckpoint(string workDir, string message)
        {
            CheckpointCount++;
            LastMessage = message;
            if (FailNext)
            {
                FailNext = false;
                return (false, "git failed: simulated error");
            }

            return (true, "Created checkpoint: " + message);
        }
    }
}