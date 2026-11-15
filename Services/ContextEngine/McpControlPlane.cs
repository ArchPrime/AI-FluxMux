using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The MCP control plane: the three tools the model can call to manage the paged-context
/// capability. Exposed over the gateway's <c>POST /mcp</c> route (wired in Step 4).
///
/// Tools:
///   * <c>update_vibe_state</c> — the model updates the session's task/state/next; the engine
///     persists it to <c>VIBE_STATE.md</c>.
///   * <c>read_swap_log</c> — the model reads back a page of a spilled tool-result body by
///     <c>swap_id</c> (the lossless counterpart to forward-prune's truncate).
///   * <c>git_checkpoint</c> — the model creates a git checkpoint at the working directory.
///
/// Every tool returns a JSON object of the form <c>{ "ok": true, ... }</c> or
/// <c>{ "ok": false, "error": "..." }</c>. No tool ever throws: any error is caught and returned
/// as <c>{ok:false, error}</c> so a bad tool call can never break a turn.
/// </summary>
public sealed class McpControlPlane
{
    private readonly IContextEngine _engine;
    private readonly ContextSwapStore _swapStore;
    private readonly IGitRunner _git;
    private readonly string _vibeStatePath;
    private readonly string _workDir;
    private readonly EssenceLog? _essenceLog;

    public McpControlPlane(
        IContextEngine engine,
        ContextSwapStore swapStore,
        IGitRunner git,
        string vibeStatePath,
        string workDir,
        EssenceLog? essenceLog = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _swapStore = swapStore ?? throw new ArgumentNullException(nameof(swapStore));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _vibeStatePath = vibeStatePath ?? throw new ArgumentNullException(nameof(vibeStatePath));
        _workDir = workDir ?? throw new ArgumentNullException(nameof(workDir));
        _essenceLog = essenceLog;
    }

    /// <summary>
    /// Handles an MCP request. <paramref name="body"/> is the parsed JSON body of
    /// <c>POST /mcp</c>, shaped as <c>{ "tool": "name", "args": { ... } }</c>. Returns the JSON
    /// response object. Never throws.
    /// </summary>
    public JsonObject Handle(JsonObject body)
    {
        try
        {
            var tool = body?["tool"]?.ToString();
            var args = body?["args"] as JsonObject ?? new JsonObject();

            return tool switch
            {
                "update_vibe_state" => HandleUpdateVibeState(args),
                "read_swap_log" => HandleReadSwapLog(args),
                "git_checkpoint" => HandleGitCheckpoint(args),
                null or "" => Error("missing 'tool'"),
                _ => Error("unknown tool: " + tool),
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Lists the available tools (for <c>tools/list</c>-style discovery). Returns a JSON array of
    /// tool descriptors.
    /// </summary>
    public JsonArray ListTools()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = "update_vibe_state",
                ["description"] = "Update the session's persistent task/state/next (VIBE_STATE.md). Args: task, state, next, files (all optional strings).",
            },
            new JsonObject
            {
                ["name"] = "read_swap_log",
                ["description"] = "Read back a page of a spilled tool-result body by swap_id. Args: swap_id (required), page (0-based, default 0), page_size (default 8000).",
            },
            new JsonObject
            {
                ["name"] = "git_checkpoint",
                ["description"] = "Create a git checkpoint (commit) at the working directory. Args: message (optional, default 'context checkpoint').",
            },
        };
    }

    private JsonObject HandleUpdateVibeState(JsonObject args)
    {
        var current = _engine.LoadVibeState(_vibeStatePath);
        var updated = current with
        {
            Task = FirstNonEmpty(args["task"]?.ToString(), current.Task),
            State = FirstNonEmpty(args["state"]?.ToString(), current.State),
            Next = FirstNonEmpty(args["next"]?.ToString(), current.Next),
            Files = FirstNonEmpty(args["files"]?.ToString(), current.Files),
        };

        _engine.SaveVibeState(_vibeStatePath, updated);

        // Mirror into the durable essence log (the source of truth for re-assertion after
        // compaction / cold resume). If the task changed, record a goal-evolution entry.
        if (_essenceLog is not null)
        {
            _essenceLog.UpdateCurrent(updated.Task, updated.State, updated.Next, updated.Files);
            if (!string.Equals(updated.Task, current.Task, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(current.Task)
                && !string.IsNullOrWhiteSpace(updated.Task))
            {
                _essenceLog.RecordEvolution(current.Task, updated.Task, "model update");
            }
        }

        return Ok(new JsonObject
        {
            ["task"] = updated.Task,
            ["state"] = updated.State,
            ["next"] = updated.Next,
            ["files"] = updated.Files,
        });
    }

    private JsonObject HandleReadSwapLog(JsonObject args)
    {
        var swapId = args["swap_id"]?.ToString();
        if (string.IsNullOrWhiteSpace(swapId))
        {
            return Error("missing 'swap_id'");
        }

        var page = int.TryParse(args["page"]?.ToString(), out var p) ? p : 0;
        var pageSize = int.TryParse(args["page_size"]?.ToString(), out var ps) ? ps : 0;

        var (text, totalBytes, hasMore) = _swapStore.ReadPage(swapId, page, pageSize);
        if (totalBytes == 0 && string.IsNullOrEmpty(text))
        {
            return Error("unknown swap_id: " + swapId);
        }

        return Ok(new JsonObject
        {
            ["swap_id"] = swapId,
            ["page"] = page,
            ["text"] = text,
            ["total_bytes"] = totalBytes,
            ["has_more"] = hasMore,
        });
    }

    private JsonObject HandleGitCheckpoint(JsonObject args)
    {
        // Optional "work_dir" override: lets the caller (e.g. the Settings-driven checkpoint button)
        // point the commit at a specific directory. When absent, the plane's default _workDir is used.
        var workDir = FirstNonEmpty(args["work_dir"]?.ToString(), _workDir);
        var message = FirstNonEmpty(args["message"]?.ToString(), "context checkpoint");
        var (ok, gitMessage) = _git.CreateCheckpoint(workDir, message);
        return ok
            ? Ok(new JsonObject { ["message"] = gitMessage, ["work_dir"] = workDir })
            : Error(gitMessage);
    }

    private static string FirstNonEmpty(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static JsonObject Ok(JsonObject? extra = null)
    {
        var result = new JsonObject { ["ok"] = true };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                // Re-parse the node so it can be re-parented into `result` without a
                // "node already has a parent" error.
                result[key] = JsonNode.Parse(value?.ToJsonString() ?? "null");
            }
        }

        return result;
    }

    private static JsonObject Error(string message)
    {
        return new JsonObject
        {
            ["ok"] = false,
            ["error"] = message,
        };
    }
}