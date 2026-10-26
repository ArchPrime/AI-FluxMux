# Next-Chat Brief — Three Remaining FluxMux Issues (C: auto-load, B: context-clear, A: graph axes)

**Project:** `c:\AI_Workbench\Workspace\FluxMux.Avalonia` (Avalonia 12.1.1, .NET 10). Read `STATUS.md` first. **Do NOT kill the running app** (`FluxMux.Avalonia.exe`) — it serves this chat and locks the build output. Build/test to a separate dir:
```
dotnet build FluxMux.Avalonia.csproj -c Debug --nologo -v q -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\
```

## Deploy state (from prior session — compiled, NOT deployed)
Four fixes are in `artifacts\testrun\` but not live:
1. **Bottom bar** (`Views/MainWindow.axaml` line ~3313): `MinHeight` 66→40 (kept all buttons).
2. **Auto-load** (`ViewModels/MainViewModel.cs` `AutoLoadSelectedProfileOnStartupAsync`, ~line 7188): now targets the **most-recently-launched** profile via the launch-time record (`FluxMuxMeta.LastLaunchedLocal`), falling back to the UI selection only when no launch has been recorded. See Issue C.
3. **Stress-test context-clear retry loop** (`ViewModels/MainViewModel.cs` `ClearLocalModelContextAfterStressTestAsync`, ~line 9403): retries reduced 10→3, backoff 5s→15s reduced to 2s→5s, softer final message. **NOTE: partial mitigation only** — the underlying "keeps retrying after instant clear" + bogus port error is still open (see Issue B).
4. **Stress test pops the Diagnostics panel open (~2x default)** (`Views/MainWindow.axaml` + `.axaml.cs`): clicking "Stress test" sets the Diagnostics row (Row 3 of `MainLayoutGrid`, default `0.55*`) to `2.4*` via `OnStressTestButtonClick`, so the log/results are easy to follow. Not auto-collapsed on test end.

To deploy: user closes app → `build-fluxmux.bat clean` → restart. Confirm with user before assuming live.

## Issue C — Auto-load STILL selects the wrong profile (FIXED 10/20 — compiled, NOT deployed)
- **STATUS: FIXED (compiled in `artifacts\testrun\`, NOT deployed).** Implemented the most-recent-launch-record approach the user specified.
- **What was done:**
  - `FluxMuxRuntimeService.RecordLastLaunchedLocal(model, variant)` writes `FluxMuxMeta.LastLaunchedLocal = { model, variant, utc }` to the config on the **launch-success path** (next to `RecordLocalLaunchTelemetry`, ~line 3652). Recording at launch (not close) because the app doesn't always exit gracefully.
  - `FluxMuxRuntimeService.GetLastLaunchedLocal()` reads it back (returns empty model when no record yet).
  - `MainViewModel.AutoLoadSelectedProfileOnStartupAsync` now targets the **last-launched** `(model, variant)`; falls back to the UI selection only when no launch has been recorded. The `EndpointValidatedUtc` safety guard is retained.
  - `autoload_diag.log` now records `source=last-launched|ui-selection` and the full `lastLaunched=(model / variant @ utc)` so targeting is auditable.
- **Files:** `Services/FluxMuxRuntimeService.cs` (`RecordLastLaunchedLocal`, `GetLastLaunchedLocal`, launch-success call site), `ViewModels/MainViewModel.cs` (`AutoLoadSelectedProfileOnStartupAsync` ~7188).
- **Tests:** build succeeded; full suite 1634 passed, 3 pre-existing `ContextFillStressResultTests.LimitBehavior_*` failures (unrelated). No unit test added — the `FluxMuxRuntimeService` constructor has heavy side effects (stops stale processes, port checks, creates the gateway host) that would risk the running app.
- **Verify after deploy:** launch variant A, then launch variant B, close (gracefully AND by kill), restart → auto-load must pick B regardless of Quick Select order or UI selection. Check `autoload_diag.log` for `source=last-launched`.

## Issue A — Stress-test graph axes do not auto-extend (STILL OPEN)
- **STATUS: OPEN — confirmed still in effect 10/20.** Two axis defects remain:
  - **X axis** fails to add extra intervals/markers suited to the **current model context** (e.g. a 393k YaRN profile needs intervals out to ~393k, not a fixed small range).
  - **Y axis** fails to add extra intervals when **TTFT results** exceed the available height (bars get clipped instead of the axis rescaling).
- **Key files:** `ViewModels/TelemetryGraphViewModel.cs` (axis range/tick computation, `BuildBarPath`, geometry), `Services/ContextFillStressProbe.cs` (fill-level computation).
- **Note:** the earlier "graph does not plot / wrong data" root cause (gateway auto-compaction shrinking probe prompts) was fixed by gating `TryApplyForwardCompact` on `isProbeRequest` — that part is deployed. The axis auto-extension is a separate, still-open defect.

## Issue B — "Clear context" keeps retrying after instant clear + bogus port error (STILL OPEN)
- **STATUS: PARTIALLY MITIGATED (retry loop reduced, compiled in `artifacts\testrun\`, NOT deployed) — underlying issue still open.**
- **Observed (10/20, see test log):** after aborting the stress test, the context clears **almost instantly**, but the code **keeps retrying** the clear, then strikes a **bogus port error**: *"port is not enabled for this profile, and no chat turn is lacking in context space."* That error is false — the port IS enabled and no chat turn is lacking context.
- **What was done this session:** reduced `maxAttempts` 10→3, initial `settleMs` 5000→2000, backoff cap 15000→5000, and softened the final message to "context clear failed (best-effort); the next chat will start with a fresh context." This only reduces the noise; it does NOT fix why the loop retries or the bogus error.
- **Still open:**
  - (a) **Why does it retry at all when the context already cleared instantly?** The first POST should return success and stop the loop. Investigate whether the gateway returns a non-2xx for the tiny `{"model":"local","max_tokens":1,"content":"Clear"}` POST even when the clear effectively succeeded (e.g. a port-rules / `ShouldBlockLocalForward` / `FormatBlockedLocalTurnMessage` path misfiring, or a 503 while llama-server is still releasing the aborted large KV cache).
  - (b) **The bogus "port is not enabled for this profile" error** — trace where that message originates. It was NOT found verbatim in source; likely a port-rules / routing message misfiring on the Clear POST. Check `FluxMuxGatewayHost.cs` (`HandleChatCompletionsAsync`, `ShouldBlockLocalForward`, `FormatBlockedLocalTurnMessage`), `FluxMuxGatewayRouting.cs`, and the port-rules post-mortem formatters.
- **Key files:** `MainViewModel.cs` (`ClearLocalModelContextAfterStressTestAsync` ~9403), `FluxMuxGatewayHost.cs`, `FluxMuxGatewayRouting.cs`.

## Suggested order
1. **Confirm deploy of the compiled fixes** (auto-load Issue C, context-clear retry Issue B-partial, bottom bar) with the user, then verify Issue C end-to-end (launch A → launch B → restart → auto-load picks B; check `autoload_diag.log` for `source=last-launched`).
2. **Issue B (context clear retry + bogus port error)** — trace the bogus port error origin and why the first Clear POST doesn't return success.
3. **Issue A (graph X/Y axis auto-extension)** — make the axes grow to fit the current model context (X) and TTFT results (Y).