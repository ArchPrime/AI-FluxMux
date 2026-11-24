using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.HailMary;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The general "can't continue — rebuild from outside" rescue: per-Client-app adapters as data,
/// one engine (find → stop → back up → steps → reopen).
/// </summary>
public sealed class RescueAdapterTests
{
    private static string ProjectRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-rescue-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Built_ins_cover_Cline_and_Harness_and_only_their_own_Client_app()
    {
        var adapters = RescueAdapterCatalog.Resolve(null);
        var cline = RescueAdapterCatalog.ForClientApp(adapters, "Cline");
        var harness = RescueAdapterCatalog.ForClientApp(adapters, "DeepSeek Harness");
        Assert.NotNull(cline);
        Assert.True(cline!.IsBuiltInCline);
        Assert.NotNull(harness);
        Assert.False(harness!.IsBuiltInCline);
        Assert.Equal(RescueAdapterCatalog.HarnessWebAction, harness.Stop.Kind);
        Assert.Equal(RescueAdapterCatalog.HarnessWebAction, harness.Reopen.Kind);
        Assert.Null(RescueAdapterCatalog.ForClientApp(adapters, "Other"));
    }

    [Fact]
    public void Listed_adapter_replaces_a_built_in_and_can_turn_it_off_or_add_a_Client_app()
    {
        var listed = new List<JsonObject>
        {
            new() { ["id"] = RescueAdapterCatalog.HarnessId, ["enabled"] = false },
            new()
            {
                ["id"] = "my-client",
                ["label"] = "Mine can't continue",
                ["clientApps"] = new JsonArray("Other"),
                ["steps"] = new JsonArray(new JsonObject { ["kind"] = "delete", ["path"] = "x" }),
            },
        };
        var adapters = RescueAdapterCatalog.Resolve(listed);
        Assert.Null(RescueAdapterCatalog.ForClientApp(adapters, "DeepSeek Harness"));
        Assert.Equal("Mine can't continue", RescueAdapterCatalog.ForClientApp(adapters, "other")!.Label);
        Assert.NotNull(RescueAdapterCatalog.ForClientApp(adapters, "Cline"));
    }

    [Fact]
    public void Example_file_parses_and_its_Harness_entry_matches_the_built_in()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectRoot(), "rescue-adapters.example.json")))!;
        var listed = ((JsonArray)root[RescueAdapterCatalog.ConfigKey]!).OfType<JsonObject>().ToList();
        var parsed = listed.Select(RescueAdapterCatalog.Parse).ToList();
        var example = parsed.Single(a => a.Id == RescueAdapterCatalog.HarnessId);
        var builtIn = RescueAdapterCatalog.BuiltIns.Single(a => a.Id == RescueAdapterCatalog.HarnessId);
        Assert.Equal(builtIn.Label, example.Label);
        Assert.Equal(builtIn.Find, example.Find);
        Assert.Equal(builtIn.Steps.Count, example.Steps.Count);
        Assert.Equal(builtIn.Steps[0].Args, example.Steps[0].Args);
        Assert.Equal(builtIn.Steps[1].Path, example.Steps[1].Path);
        Assert.False(parsed.Single(a => a.Id == "example-file-client").Enabled);
    }

    [Fact]
    public void Harness_helper_script_ships_with_the_app()
    {
        Assert.True(File.Exists(Path.Combine(ProjectRoot(), "Rescue", "dsh-rescue.mjs")));
        Assert.Contains("Rescue\\**", File.ReadAllText(Path.Combine(ProjectRoot(), "FluxMux.Avalonia.csproj")), StringComparison.Ordinal);
    }

    [Fact]
    public void Find_picks_the_newest_chat_file_through_nested_folders()
    {
        var root = TempDir();
        try
        {
            var older = Directory.CreateDirectory(Path.Combine(root, "ws-a", "session-old")).FullName;
            var newer = Directory.CreateDirectory(Path.Combine(root, "ws-b", "session-new")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "ws-b", "not-a-session"));
            File.WriteAllText(Path.Combine(older, "chat.log"), "a");
            File.WriteAllText(Path.Combine(newer, "chat.log"), "b");
            File.WriteAllText(Path.Combine(newer, "chat.log.bak"), "c");
            File.SetLastWriteTimeUtc(Path.Combine(older, "chat.log"), DateTime.UtcNow.AddMinutes(-5));
            var found = RescueEngine.FindSession(new RescueFind { Root = root, Dirs = @"*\session-*", File = "chat.log" });
            Assert.Equal("session-new", found!.SessionId);
            Assert.Equal(Path.Combine(newer, "chat.log"), found.SessionFile);
            Assert.Null(RescueEngine.FindSession(new RescueFind { Root = Path.Combine(root, "missing") }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Placeholders_and_environment_variables_expand()
    {
        var adapter = new RescueAdapterDefinition { KeepRecent = 9 };
        var session = new RescueSession("s1", @"C:\chats\s1", @"C:\chats\s1\chat.log");
        var values = RescueEngine.Placeholders(adapter, session, "say \"hi\"", @"C:\t\sum.md", @"C:\scripts");
        Assert.Equal(@"C:\scripts\x.mjs s1 9", RescueEngine.Expand("{rescueScripts}\\x.mjs {sessionId} {keepRecent}", values));
        Assert.Equal("say \"hi\"", JsonNode.Parse(RescueEngine.Expand("{summaryJson}", values))!.ToString());
        Assert.Equal(Environment.GetEnvironmentVariable("USERPROFILE") + @"\a", RescueEngine.Expand(@"%USERPROFILE%\a", values));
    }

    private sealed class FixedNote : IFidelitySource
    {
        public string Name => "fixed";

        public Task<FidelitySnapshot> BuildAsync(CompactionContext ctx)
            => Task.FromResult(new FidelitySnapshot { Goal = "ship the rescue", State = "engine written", Next = "test it" });
    }

    private sealed class FakeHost : IRescueHost
    {
        public bool StopResult { get; init; } = true;
        public List<string> Calls { get; } = new();

        public Task<bool> RunNamedActionAsync(string kind, bool reopen, RescueSession session, CancellationToken cancellationToken)
        {
            Calls.Add(kind + (reopen ? ":reopen" : ":stop"));
            return Task.FromResult(reopen || StopResult);
        }
    }

    private static (string Dir, RescueSession Session) MakeChat()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "chat.log");
        File.WriteAllText(file, "original");
        File.WriteAllText(Path.Combine(dir, "cache.json"), "stale");
        return (dir, new RescueSession("chat-1", dir, file));
    }

    [Fact]
    public async Task Steps_run_in_order_after_stop_and_backup_then_reopen()
    {
        var (dir, session) = MakeChat();
        try
        {
            var adapter = new RescueAdapterDefinition
            {
                Id = "t",
                Stop = new RescueAction { Kind = "my-app" },
                Steps =
                [
                    new RescueStep { Kind = RescueStep.WriteFileKind, Path = "{sessionDir}\\resume.json", Content = "{ \"id\": \"{sessionId}\", \"summary\": {summaryJson} }" },
                    new RescueStep { Kind = RescueStep.DeleteKind, Path = "{sessionDir}\\cache.json" },
                    new RescueStep { Kind = RescueStep.DeleteKind, Path = "{sessionDir}\\never-there.json" },
                ],
                Reopen = new RescueAction { Kind = "my-app" },
                DoneMessage = "done!",
            };
            var host = new FakeHost();
            var result = await RescueEngine.RunAsync(adapter, session, "line 1\nline \"2\"", host);

            Assert.True(result.Success, result.Message);
            Assert.Equal("done!", result.Message);
            Assert.Equal(new[] { "my-app:stop", "my-app:reopen" }, host.Calls);
            var resume = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resume.json")))!;
            Assert.Equal("chat-1", resume["id"]!.ToString());
            Assert.Equal("line 1\nline \"2\"", resume["summary"]!.ToString());
            Assert.False(File.Exists(Path.Combine(dir, "cache.json")));
            var backup = Assert.Single(Directory.GetFiles(dir, "chat.log.hailmary-*.bak"));
            Assert.Equal("original", File.ReadAllText(backup));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Nothing_is_touched_when_the_Client_app_cannot_be_stopped()
    {
        var (dir, session) = MakeChat();
        try
        {
            var adapter = new RescueAdapterDefinition
            {
                Stop = new RescueAction { Kind = "my-app" },
                Steps = [new RescueStep { Kind = RescueStep.DeleteKind, Path = "{sessionFile}" }],
            };
            var result = await RescueEngine.RunAsync(adapter, session, "s", new FakeHost { StopResult = false });
            Assert.False(result.Success);
            Assert.True(File.Exists(session.SessionFile));
            Assert.Empty(Directory.GetFiles(dir, "*.bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_step_stops_the_rescue_and_reopens_the_Client_app()
    {
        var (dir, session) = MakeChat();
        try
        {
            var adapter = new RescueAdapterDefinition
            {
                Stop = new RescueAction { Kind = "my-app" },
                Steps =
                [
                    new RescueStep { Kind = "teleport" },
                    new RescueStep { Kind = RescueStep.DeleteKind, Path = "{sessionDir}\\cache.json" },
                ],
                Reopen = new RescueAction { Kind = "my-app" },
            };
            var host = new FakeHost();
            var result = await RescueEngine.RunAsync(adapter, session, "s", host);
            Assert.False(result.Success);
            Assert.Contains("teleport", result.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir, "cache.json")));
            Assert.Equal(new[] { "my-app:stop", "my-app:reopen" }, host.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Command_step_must_report_ok_when_asked_to()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (dir, session) = MakeChat();
        try
        {
            RescueAdapterDefinition Echo(string json)
            {
                var script = Path.Combine(dir, "helper-" + Guid.NewGuid().ToString("N") + ".cmd");
                File.WriteAllText(script, "@echo " + json + "\r\n");
                return new()
                {
                    Backup = false,
                    Steps = [new RescueStep { Kind = RescueStep.CommandKind, Exe = "cmd.exe", Args = ["/c", script], ExpectJsonOk = true, TimeoutSeconds = 30 }],
                };
            }

            var ok = await RescueEngine.RunAsync(Echo("{\"ok\":true}"), session, "s", null);
            Assert.True(ok.Success, ok.Message);

            var refused = await RescueEngine.RunAsync(Echo("{\"ok\":false,\"error\":\"chat too short\"}"), session, "s", null);
            Assert.False(refused.Success);
            Assert.Contains("chat too short", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// End to end on a COPY of a real Harness chat: the built-in Harness steps (Node helper using
    /// Harness's own code) rebuild it, and the helper re-validates the result with Harness's
    /// loader. Skipped where Harness or a long enough chat is not installed.
    /// </summary>
    [Fact]
    public async Task Harness_steps_rebuild_a_copy_of_a_real_chat_when_Harness_is_installed()
    {
        var dshLib = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? string.Empty,
            "npm", "node_modules", "@deepseek-ai", "dsh", "node_modules", "@deepseek-ai", "dsh-session", "lib", "index.js");
        var sessions = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty, ".dsh", "sessions");
        if (!File.Exists(dshLib) || !Directory.Exists(sessions))
        {
            return;
        }

        var source = Directory.EnumerateFiles(sessions, "session.v4.jsonl.zstd", SearchOption.AllDirectories)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault(f => new FileInfo(f).Length > 50_000);
        if (source is null)
        {
            return;
        }

        var root = TempDir();
        try
        {
            var chatDir = Directory.CreateDirectory(Path.Combine(root, "ws", Path.GetFileName(Path.GetDirectoryName(source)!))).FullName;
            var copy = Path.Combine(chatDir, "session.v4.jsonl.zstd");
            File.Copy(source, copy);
            var originalLength = new FileInfo(copy).Length;
            var builtIn = RescueAdapterCatalog.BuiltIns.Single(a => a.Id == RescueAdapterCatalog.HarnessId);
            var adapter = builtIn with
            {
                Find = builtIn.Find with { Root = root },
                Stop = new RescueAction(),
                Reopen = new RescueAction(),
                Steps = [builtIn.Steps[0]],
            };

            var session = RescueEngine.FindSession(adapter.Find);
            Assert.NotNull(session);
            var result = await RescueEngine.RunAsync(adapter, session!, "## Primary Request and Intent\n- Rescue test summary.", null, Path.Combine(ProjectRoot(), "Rescue"));

            Assert.True(result.Success, result.Message + " | " + string.Join(" | ", result.Log));
            Assert.True(new FileInfo(copy).Length > originalLength);
            Assert.Single(Directory.GetFiles(chatDir, "*.hailmary-*.bak"));
            Assert.Equal(new FileInfo(source).Length, originalLength);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Harness_lock_name_matches_Harness_own_formula()
    {
        // Expected value computed with Harness's own expression (Node: sha256 of
        // path.resolve(join(dir, "session.lock")).toLowerCase()).
        var dir = @"C:\Users\paulk\.dsh\sessions\--C-AI_Workbench-Workspace--\session-955b5086-a410-475b-b3c1-a579140c1c1e";
        Assert.Equal(
            @"Local\dsh-session-lock-4cc8d086e8e1b33c3e99e7f2ae58342c75082615d0e77b2c95ba12c2f2a389ec",
            DeepSeekHarnessSetup.HarnessSessionLockName(dir));
    }

    [Fact]
    public void Harness_chat_counts_as_locked_while_its_semaphore_exists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = TempDir();
        try
        {
            Assert.False(DeepSeekHarnessSetup.IsHarnessSessionLocked(dir));
            using (new Semaphore(1, 1, DeepSeekHarnessSetup.HarnessSessionLockName(dir)))
            {
                Assert.True(DeepSeekHarnessSetup.IsHarnessSessionLocked(dir));
            }

            Assert.False(DeepSeekHarnessSetup.IsHarnessSessionLocked(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Configured_strategy_prepares_from_the_session_note_and_finds_the_chat()
    {
        var root = TempDir();
        try
        {
            var chat = Directory.CreateDirectory(Path.Combine(root, "session-x")).FullName;
            File.WriteAllText(Path.Combine(chat, "chat.log"), "x");
            var adapter = new RescueAdapterDefinition
            {
                Id = "t",
                Label = "T can't continue",
                ClientApps = ["Other"],
                Find = new RescueFind { Root = root, Dirs = "session-*", File = "chat.log" },
                Steps = [new RescueStep { Kind = RescueStep.WriteFileKind, Path = "{sessionDir}\\resume.md", Content = "{summary}" }],
            };
            var strategy = new ConfiguredRescueStrategy(adapter, null, new FixedNote());
            var prepared = await strategy.ExecuteAsync(new CompactionContext());
            Assert.True(prepared.Success, prepared.Message);
            Assert.Equal("session-x", strategy.Session!.SessionId);
            Assert.False(string.IsNullOrWhiteSpace(prepared.SummaryPreview));

            var committed = await strategy.CommitAsync(new CompactionContext(), prepared);
            Assert.True(committed.Success, committed.Message);
            Assert.Equal(prepared.SummaryPreview, File.ReadAllText(Path.Combine(chat, "resume.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
