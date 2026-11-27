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
    public void BuildReinjectionBlock_IncludesTaskStateFilesButNotNext()
    {
        var state = new VibeState { Task = "T", State = "S", Next = "N", Files = "F" };
        var block = VibeStateMarkdown.BuildReinjectionBlock(state);

        Assert.Contains("Task: T", block);
        Assert.Contains("State: S", block);
        Assert.DoesNotContain("Next:", block);
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
        bool vibeEnabled = true)
    {
        var options = new ContextEngineOptions
        {
            IsVibeStateEnabled = () => vibeEnabled,
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
        Assert.Contains("State: S", block);
        // Next came from the model's reply and is not handed back (27 Nov).
        Assert.DoesNotContain("Next:", block);
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
