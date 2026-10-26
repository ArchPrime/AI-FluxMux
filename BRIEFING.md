# New-Chat Briefing — AI-FluxMux (10/20/2026)

## What this project is
Avalonia desktop app that launches local GGUF models via llama-server, routes cloud models, and exposes one OpenAI-compatible endpoint (`http://127.0.0.1:5001`) to client apps (Cline, Harness).

## Read these first (in order)
1. `AGENTS.md` — product facts, naming rules, "do not change" list, file map
2. `STATUS.md` — full open-item table + backlog + invariants
3. `PLAN_CONTEXT_FIXES.md` — **ACTIVE HANDOFF (10/26):** the resumable plan for the YaRN
   switch / per-turn forward-prune / Hail Mary fixes. Read this + its "Progress log" to pick
   up exactly where implementation left off.
4. This file — the specific work to pick up

## Critical invariants (violating these breaks the app or the chat)
- **NEVER kill `FluxMux.Avalonia.exe`** — it serves this chat. If a build fails on a locked DLL, stop and tell the user to close the app manually.
- **Do NOT rely on git history.** The repo's git log is very old and does not reflect recent work. Use `STATUS.md` and `BRIEFING.md` as the source of truth for what's been done and what's next.

- **NEVER run `build-fluxmux.bat`** — it kills the app.
- **Test in a separate output dir** (see "How to test" below). Never build to the default `bin\` path.
- **No hardcoded pixel dimensions** for layout. Use star sizing, `Auto`, `MinWidth`/`MaxWidth` floors.
- **Do not rename** C# namespaces, JSON keys in `fluxmux_config.json`, protocol string `"FluxMux"`, or `EndpointAdapters` identifiers.
- **User-facing names:** "Port" (public endpoint), "daemon port" (llama-server private port), "Client app" (Cline/Harness), "Compact" (shorten older turns forwarded to llama-server).

## How to test (without disconnecting the chat)
```powershell
cd c:\AI_Workbench\Workspace\FluxMux.Avalonia
dotnet build FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --nologo -v q `
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\
dotnet test FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --no-build --nologo `
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\ `
  --filter "FullyQualifiedName~<TestClassName>"
```
- Run targeted filter first, then full suite (no `--filter`).
- A "Build FAILED" that is ONLY the `FluxMux.Updates.Core.dll` copy step (locked by app) is NOT a code error — check for `error CS####` to distinguish.
- **Do NOT use the `AvoidLockedAppHost` escape hatch** for new changes (tests stale prebuilt DLL).

## How to deploy (user action, NOT agent action)
User must: close app → `build-fluxmux.bat clean` → restart. Agent hands over these steps and stops. Record deploy state in `STATUS.md`.

## Where the logic lives (key files)
| Area | File |
|------|------|
| UI commands, profiles, Health, Diagnostics | `ViewModels/MainViewModel.cs` |
| llama-server launch, args, idle park/wake | `Services/FluxMuxRuntimeService.cs` |
| Public :5001 gateway, Compact, errors | `Services/FluxMuxGatewayHost.cs` |
| Routing (which model takes a turn) | `Services/FluxMuxGatewayRouting.cs`, `LocalReloadRouting.cs`, `LocalRequestOverlayRouting.cs` |
| Layout | `Views/MainWindow.axaml` |
| Stress test / context fill | `Services/ContextFillStressProbe.cs`, `ViewModels/TelemetryGraphViewModel.cs` |

## Open items to work on (prioritized)

### 1. YaRN context limit → gateway `local_context` (FIXED, needs deploy)
- **What was wrong:** Gateway's `local_context` was hardcoded to profile's `OverrideContext` (262K) instead of the YaRN-extended limit (393K). This caused auto-compaction to trigger too early (~25K tokens instead of ~222K).
- **Fix applied:** `FluxMuxGatewayHost.cs` line ~3245 now uses `LocalYarnMaxContext` when YaRN is active instead of raw `OverrideContext`.
- **Status:** Compiled + tested in `artifacts\testrun\`, NOT deployed.
- **Next:** User deploys. Then verify in running app that `local_context` reports 393K (check gateway state or terminal output).

### 2. Stress test issues (OPEN, 10/20)
- **Countdown still wrong after deploy** — the window-aware `CountdownMs` strategy was deployed but the countdown still shows incorrect values. Investigate whether the 8-sample window is being populated correctly, or whether the "historical slowest" fallback is masking the real issue.
- **TTFT results fluctuate up/down unusually while chat is active** — background chat traffic is interfering with stress-test measurements. Options: (a) stop fill early if a background chat is active, (b) suspend chat + clear context before test begins.
- **Key files:** `Services/ContextFillStressProbe.cs`, `ViewModels/TelemetryGraphViewModel.cs`
- **Tests:** `FluxMux.Avalonia.Tests/ContextFillStressProbeTests.cs`

### 3. Flexible model-profile migration for running chats (OPEN)
- **Problem:** When a user switches the model profile mid-chat, the running chat keeps the old profile's context settings (YaRN, compact thresholds, etc.) until restart.
- **Desired behavior:** Chat migrates to new profile's settings without losing conversation state.
- **Key files:** `Services/FluxMuxGatewayHost.cs` (profile switching), `Services/FluxMuxGatewayRouting.cs` (routing decisions), `ViewModels/MainViewModel.cs` (profile selection UI)
- **Approach:** When profile changes, update the gateway's active context limit, compact thresholds, and YaRN settings in-place. The conversation history stays; only the "how much context is available" and "when to compact" parameters change.

### 4. Reconcile port-rules vs per-profile compact terminology (OPEN)
- **Problem:** UI has both "Port-rules compaction" (global defaults) and "per-profile compact" settings. The naming is confusing — "port-rules" is an internal term that leaked into the UI.
- **Key files:** `ViewModels/MainViewModel.cs` (settings UI), `Services/FluxMuxGatewayRouting.cs` (compact logic)
- **Approach:** Rename "Port-rules compaction" to something user-facing like "Global context compaction defaults". Ensure per-profile settings clearly override the global ones.

### 5. Cline context display in UI (OPEN)
- **Problem:** The UI doesn't show the current context usage for Cline sessions.
- **Approach:** Read context usage from the gateway state and display it in the Diagnostics panel.

### 6. YaRN context in terminal output + endpoint metrics (OPEN)
- **Problem:** Terminal output and endpoint metrics don't reflect the YaRN-extended context limit.
- **Key files:** `Services/FluxMuxGatewayHost.cs` (terminal output), `Services/FluxMuxRuntimeService.cs` (endpoint metrics)

### 7. Model-statistics info text to reflect YaRN (OPEN)
- **Problem:** The model statistics info text shows the base context limit, not the YaRN-extended one.
- **Key files:** `ViewModels/MainViewModel.cs` (info text), `Services/FluxMuxRuntimeService.cs` (statistics)

### 8. Test YaRN RAM-spill end-to-end with real model (OPEN)
- **Problem:** YaRN extends context into RAM, but this hasn't been tested end-to-end with a real model.
- **Approach:** Load a model with YaRN enabled, run a conversation that exceeds the VRAM context limit, verify it spills to RAM and continues working.

### 9. Deferred specs (OPEN, low priority)
- MTP draft, health/validate, tool-call heal — see `STATUS.md` "Open backlog" item 9 for details.

### 10. Release doc follow-up 0.2.2-beta (OPEN, low priority)
- See `STATUS.md` "Open backlog" item 10.

### 11. God-file split (OPEN, low priority)
- `MainWindow.axaml` and `MainViewModel.cs` are very large. Extract slices only after behaviour settles. See `AGENTS.md` for the pattern.

## Known bug: Auto-Compaction Triggering Too Early
- **Symptom:** Auto-compaction triggers at ~20-25K tokens when nominal context is 360K (YaRN) or 262K.
- **Root cause:** `local_context` in gateway state was `OverrideContext` (262K) instead of YaRN-extended (393K). The 85% watermark of 262K = ~222K, but the 2-chars/token pessimistic estimate in `PromptFillsLocalContextForCompact` made it trigger even earlier.
- **Fix:** Item 1 above (YaRN context limit → gateway `local_context`).
- **Key code paths:**
  - `FluxMuxGatewayRouting.cs` line 189: `EvaluatePromptFillsLocalContext`
  - `FluxMuxGatewayRouting.cs` line 229: `PromptFillsLocalContextForCompact` (2-chars/token)
  - `FluxMuxGatewayRouting.cs` line 255: `MaxTokensForTurn`
  - `LocalRequestOverlayRouting.cs` line 153: `EstimatePromptTokens` (4-chars/token default, 2 for filling)
  - `LocalRequestOverlayRouting.cs` line 239: `PromptExceedsContext`
  - `FluxMuxGatewayHost.cs` line 3245: `_managedLocalContext` (NOW uses `LocalYarnMaxContext`)
  - `FluxMuxGatewayHost.cs` line 7240: `jsonObject["local_context"] = _managedLocalContext`

## Context budget awareness
- This session runs on a local model with a LIMITED context window.
- Read surgically (use `start_line`/`end_line`). Prefer `search_codebase` over `read_files` for pattern finding.
- After reading a file, note key facts. Don't re-quote content in later replies.
- If context is >70% used, tell the user to compact.
- If context is >85%, stop and summarize.

## Cline output halt workaround
- Keep replies short and tool-call-heavy.
- If explaining something, write it to a file instead of outputting in the reply.
- Don't end a reply with a question or incomplete thought.

## STATUS.md convention
- Update `STATUS.md` when a feature/fix lands and is verified.
- Update when scope shifts or a new direction is chosen.
- Update when about to compact or start a new task.
- Keep it current, not exhaustive. Readable in under 2 minutes.
- Spot-check your own writes after updating.

