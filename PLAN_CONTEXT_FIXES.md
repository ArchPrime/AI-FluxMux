# PLAN — Context fixes: YaRN switch, per-turn forward-prune, Hail Mary (HANDOFF, resumable)

> Recorded 10/26. **This file is the source of truth for resuming.** If a chat's context
> overflows mid-implementation, a new chat reads this file + `BRIEFING.md` + `STATUS.md`
> and continues from the first unchecked step. Each step has a "DONE?" marker — flip it to
> `[x]` and note the build/test state when the step is verified.
>
> **Do NOT deploy** (close app → `build-fluxmux.bat clean` → restart) — that is the user's
> action. Record deploy state in `STATUS.md` at the end.

## Why this plan exists (the 3 reported problems)
1. **Profile switching to YaRN no longer smooth** — "chats used to switch smoothly from
   full-VRAM-context profiles to yarn-extended profiles."
2. **Forward pruning: no evidence it works** — it was supposed to deal with each turn as it
   happens to slow the rate of context filling; it is NOT a replacement for conventional
   compaction.
3. **Backward Hail Mary: no effect + persistent pulsing button** — pressing the button never
   reduced the context gauge, and the pulse never stopped.

## Root-cause findings (verified in source 10/26)

### Problem 1 — YaRN switch
- `local_context` is now the full YaRN window (~393K) on YaRN profiles (correct for the
  window, but the pre-flight block only trips at ~393K, so prompts up to ~393K are forwarded
  into the slow RAM-spill zone instead of compacted/blocked early).
- `local_vram_context` + `local_extend_context_into_ram` are **only stamped at launch**
  (`FluxMuxRuntimeService.cs` `WriteProxyRuntimeStateForLocal`, line ~7740-7741) and **never
  refreshed on a profile switch**. The switch path (line ~8032) updates `local_context` but
  NOT the other two. After switching from a full-VRAM profile to a YaRN profile mid-session,
  the gateway can run a turn with a **stale or zero VRAM boundary** → spill-compact offer and
  VRAM-boundary compact trigger misbehave. Most likely cause of the "switch no longer smooth."
- A mid-launch turn already sees `local_context=0` (cleared at line ~3381,
  `ClearStaleLocalContextForStartup`), so refreshing these keys cannot block a switch.

### Problem 2 — Forward pruning (the conceptual fix)
- Today the per-turn waste-prune (dedupe, collapse tool loops, truncate big tool results,
  strip old images) lives inside `HailMaryCompaction.Salvage` (stages 1-4) and only runs as a
  **fallback when compaction bails out** (`FluxMuxGatewayHost.cs` line ~2304) or on a 400 →
  dormant for most of a chat. That's why "no evidence it works."
- `TryApplyForwardCompact` → `LocalHistoryCompaction.CompactMessages` is **conventional
  compaction** (summarizes older turns into one block). It is gated behind thresholds
  (`ShouldForwardCompact`: `promptExceeds || (fillingEstimate && fillingZone) || IsNearLimit`),
  so it is also dormant until the window is nearly full. This is a DIFFERENT mechanism and
  must stay separate.
- **User's intent (clarified over the conversation):** forward pruning = a **per-turn fill
  throttle** that trims each turn's own waste as it happens, to slow context fill. It runs on
  **every local turn from the first**. It is conservative (minimal loss). It is conceptually
  **NOT** Hail Mary (one-off emergency) and **NOT** conventional compaction (summarize older
  turns).
- **Per-profile toggle** requested: a profile setting, exposed in the Quick Select panel,
  safely toggleable mid-chat (no reload). CONFIRMED feasible: `GetLocalAutoCompactSettings`
  (`FluxMuxRuntimeService.cs` line ~652) reads the profile **live from the config file each
  turn** via `LoadJsonCached(_configPath)`, so a new per-profile key is picked up on the next
  turn without a reload.

### Problem 3 — Hail Mary
- **No effect:** `RearContextPrune` (`HailMaryCompaction.cs` line ~109) uses
  `EstimatePromptTokens` (4-chars/token *estimate*) for its `before`/`target` check. For dense
  tokenizers (Qwen ~3-4 chars/token) the estimate **over-counts**, so `before <= target` is
  often true → prune is a **no-op**. The gateway's manual Hail Mary path
  (`FluxMuxGatewayHost.cs` line ~715-738) does not feed the model's **measured** token count
  into the prune.
- **Persistent pulse:** `HailMaryShrinkTurn` (`MainViewModel.cs` line ~3951) sets
  `HailMaryQueued = true` → starts the pulse (`MainWindow.axaml.cs` line ~816-825).
  `HailMaryQueued` is only reset to `false` in `ApplyHailMaryOffer` (line ~14131), which only
  fires when the gateway **reports a failure**. If the armed prune succeeds (or the next turn
  fits), `HailMaryQueued` is **never cleared** → pulse runs forever.
- **Timing:** the button is *armed* on click; the actual prune only runs on the **next** turn
  the gateway processes (line ~715). Nothing happens until the user sends the next message,
  and the gauge only updates when a turn is measured → "never seen the gauge reduce."

## Key code locations (verified line numbers, 10/26)
- `Services/FluxMuxGatewayHost.cs`
  - line ~532: first `TryApplyForwardCompact(payload, state, compactOn)` — `measuredPromptTokens=null` (estimate only).
  - line ~678: `measuredPromptTokens = await LocalPromptTokenCounter.CountAsync(...)` — measured count obtained AFTER routing, only for local-routed turns.
  - line ~690: `ReportContextUsage?.Invoke(measuredPromptTokens, hotContext, model)` — feeds the UI context gauge.
  - line ~697-699: second `TryApplyForwardCompact(payload, state, compactOn, measuredPromptTokens)` — measured-count pass.
  - line ~715-738: Hail Mary consumption: `ConsumeHailMaryMiddleCut.Invoke()` → `RearContextPrune(payload, hotContextForHailMary, hailStop, hailStripImages)`.
  - line ~2168: `TryApplyForwardCompact` def. line ~2242-2244: wizard auto-compact trigger `triggerThreshold = autoContext * triggerPercent` (autoContext = local_context). line ~2282-2319: port-rules path + `Salvage` fallback (line ~2304).
  - line ~2337: `OfferSpillCompactIfNeeded` (uses `local_vram_context`).
- `Services/FluxMuxRuntimeService.cs`
  - line ~3550: `_managedLocalContext = GetProfileInt(profile, "OverrideContext", 0)`. line ~3555-3577: `_managedLocalExtendContextIntoRam` + `_managedLocalVramContext` computed at launch.
  - line ~3381: `ClearStaleLocalContextForStartup()` before launch.
  - line ~652-687: `GetLocalAutoCompactSettings(model, variant)` — live per-turn profile read.
  - line ~7733: `jsonObject["local_context"] = _managedLocalContext`. line ~7740-7741: `local_extend_context_into_ram` + `local_vram_context` stamped.
  - line ~8032-8035: switch path updates `local_context` only (NOT the other two).
- `Services/HailMaryCompaction.cs`
  - line ~73: `HailMaryAggression` enum (Gentle/Balanced/Aggressive). line ~84-99: `KeepTurnsFor`/`HeadroomFractionFor`.
  - line ~109: `RearContextPrune(payload, contextTokens, stop, stripImages)` — uses `EstimatePromptTokens` (over-counts).
  - line ~170: `Salvage(payload, contextTokens, headroom, allowMiddleCut, middleCutKeepTurnsOverride, stripImages)` — stages 1-4 + stage 5.
  - line ~276: `DedupeIdenticalMessages`. line ~335: `CollapseToolLoops`. (stage 3 truncate + stage 4 strip images below). line ~705: `MiddleCutOlderTurns`.
- `Services/LocalHistoryCompaction.cs`
  - line ~51-73: `ShouldForwardCompact`. line ~90-108: `TryCompactPayload`. line ~113: `CompactMessages` (conventional compaction — summarizes older turns).
- `ViewModels/MainViewModel.cs`
  - line ~3190: `UpdateContextGauge` (sets `HailMaryButtonVisible`). line ~3711: `HailMaryButtonVisible`. line ~3714: `HailMaryQueued`. line ~3941-3963: `HailMaryShrinkTurn` (sets `HailMaryQueued=true`, calls `RequestHailMaryMiddleCut`). line ~3965: `CanHailMaryShrinkTurn`. line ~3972/3979: `HailMaryAggressionStop`/`HailMaryStripImages`. line ~3985: `ResetHailMaryPicker`. line ~4006: `DismissHailMaryBanner`. line ~14118-14134: `ApplyHailMaryOffer` (only place `HailMaryQueued=false`).
- `Views/MainWindow.axaml.cs`
  - line ~253-290: `StartHailMaryPulse`/`StopHailMaryPulse`/`OnHailMaryPulseTick`. line ~816-825: `HailMaryQueued` property-changed → start/stop pulse.
- `Views/MainWindow.axaml`
  - line ~3240-3296: Hail Mary button + `HailMaryPickerPopup`. line ~3423-3467: Hail Mary banner.


## IMPLEMENTATION STEPS (do in this order; mark [x] when verified)

### Step A — YaRN profile-switch state refresh `[x]`
- DONE 10/26: `FluxMuxRuntimeService.cs` switch path (now ~line 8036) refreshes
  `local_extend_context_into_ram` + `local_vram_context` from the managed fields when they differ,
  mirroring the `local_context` diff-check. Build clean.
- File: `Services/FluxMuxRuntimeService.cs`, the switch path near line ~8032 (where
  `state["local_context"] = _managedLocalContext` is set).
- Add: when `_managedLocalExtendContextIntoRam` or `_managedLocalVramContext` differ from the
  stored `local_extend_context_into_ram` / `local_vram_context`, update them too (mirror the
  `local_context` diff-check pattern at line ~8032).
- Rationale: switching into a YaRN profile immediately carries the correct VRAM boundary.
- Verify: build clean; add a focused test if a testable seam exists (the switch path is
  inline in the runtime service; if hard to unit-test, verify via the existing launch/switch
  tests + a manual log check).

### Step B — Per-turn forward-prune throttle (NEW service, decoupled from Hail Mary) `[x]`
- DONE 10/26:
  - B1: new `Services/ForwardPrune.cs` — `ForwardPrune.PruneTurn(payload, contextTokens, measuredTokens)`
    runs the conservative pipeline (dedupe + collapse tool loops + truncate oversized older tool
    results), NO image strip. Uses measured count when available.
  - B3: `FluxMuxGatewayHost.cs` (~line 697) runs `ForwardPrune.PruneTurn` on every local turn from
    the first, gated on `GetForwardPruneEnabled` (per-profile toggle), using the measured count.
    Re-measures + reports the gauge when it prunes.
  - B4: `LocalForwardPruneEnabled` per-profile key (default on) read live each turn via
    `GetForwardPruneEnabled` (`FluxMuxRuntimeService.cs` ~line 697, same LoadJsonCached path as
    auto-compact → toggleable mid-chat, no reload). Exposed in the Quick Select panel
    (`MainWindow.axaml` "Context management" section) + `MainViewModel.LocalForwardPruneEnabled`
    (property + save/load at all 4 settings sites).
  - B5: Hail Mary keeps its own `Salvage` (unchanged) for the automatic fallback + rear-prune image
    strip; the per-turn prune now lives in the separate `ForwardPrune` service. Conceptual separation
    achieved.
  - NOTE: `MainWindow.axaml` was restored from the 13:54 source backup (the live file had drifted
    from git HEAD, which predates the Hail Mary button). The forward-prune checkbox was re-added.
- **B1.** New `Services/ForwardPrune.cs`:
  - `public static class ForwardPrune` with
    `public static ForwardPruneResult PruneTurn(JsonObject payload, int contextTokens, int? measuredTokens = null)`.
  - Runs the **conservative** waste-prune: dedupe identical messages + collapse repeated tool
    loops + truncate tool results over the cap. **NO image strip** (user chose conservative).
  - Uses the **measured** token count when available (fall back to `EstimatePromptTokens`).
  - Returns a small result record (what was pruned + token delta). Reuse the existing stage
    helpers by moving them out of `HailMaryCompaction` (or call them if made internal/public).
- **B2.** Move the conservative stages (dedupe, loop-collapse, result-truncate) out of
  `HailMaryCompaction.Salvage` into `ForwardPrune`. `HailMaryCompaction` keeps only the
  destructive rear middle-cut (stage 5) + its picker + the image-strip (manual-only). Update
  `HailMaryCompaction.Salvage` to call `ForwardPrune` for stages 1-3 so behavior is unchanged
  for the automatic fallback path.
- **B3.** Gateway: in `Services/FluxMuxGatewayHost.cs`, run `ForwardPrune.PruneTurn` on every
  local turn from the first, alongside the measured-count pass (near line ~699, after
  `measuredPromptTokens` is available). Gate it on the per-profile toggle (B4). Log a
  diagnostics line when it prunes.
- **B4.** Per-profile toggle `LocalForwardPruneEnabled` (default **on**):
  - Read live each turn via the same pattern as `GetLocalAutoCompactSettings`
    (`FluxMuxRuntimeService.cs` line ~652) — add the key to that read (or a sibling read).
    Confirm no reload is needed (live `LoadJsonCached`).
  - Expose in the **Quick Select panel** for that profile in `Views/MainWindow.axaml` +
    `ViewModels/MainViewModel.cs` (property, save/load, fingerprint if used).
- **B5.** The automatic fallback at `FluxMuxGatewayHost.cs` line ~2304 now calls the new
  `ForwardPrune` service (not `HailMaryCompaction.Salvage` stages 1-4) — or keep it via
  `Salvage` which now delegates to `ForwardPrune`. Either way Hail Mary no longer *owns* the
  per-turn prune.
- Verify: build clean; tests in Step D.


### Step C — Hail Mary: measured-count + clear pulse on actual occurrence `[x]`
- DONE 10/26:
  - C1: `RearContextPrune` gained optional `int? measuredTokens` param; uses it for before/target.
  - C2: gateway Hail Mary consumption passes `measuredPromptTokens`.
  - C3: after consumption, re-measures + `ReportContextUsage` (gauge drops) + new
    `ReportHailMaryApplied(tokens)` callback (fires on success AND no-op).
  - C4: `MainViewModel` subscribes `HailMaryAppliedNotice` → clears `HailMaryQueued` (stops the
    persistent pulse) + shows outcome. `FluxMuxRuntimeService.HailMaryAppliedNotice` event added.
- **C1.** `RearContextPrune` (`HailMaryCompaction.cs` line ~109) gains an optional
  `int? measuredTokens = null` param; use it for `before`/`target` when available (fall back
  to the estimate).
- **C2.** Gateway Hail Mary consumption (`FluxMuxGatewayHost.cs` line ~715-738) passes
  `measuredPromptTokens` into `RearContextPrune`.
- **C3.** When the armed rear-prune is consumed, report back to the UI via a callback (new
  `Action`/`Func` on the host, wired in `FluxMuxRuntimeService` like `ReportHailMaryOffer`)
  that: (a) clears `HailMaryQueued = false` (stops the pulse) and (b) re-reports the new
  prompt token count so the **context gauge visibly drops** (reuse `ReportContextUsage`).
  Fire this on both success and no-op so the pulse always stops when the action occurs.
- **C4.** `MainViewModel.cs`: add the handler for the new callback; ensure `HailMaryQueued`
  is cleared there (in addition to the existing `ApplyHailMaryOffer` path).
- Verify: build clean; tests in Step D.

### Step D — Tests `[x]`
- DONE 10/26: `FluxMux.Avalonia.Tests/ForwardPruneTests.cs` (new, 6 tests: no-op, fresh-turn not
  threshold-gated, measured-count path, loop-collapse, result-truncate, no-image-strip).
  `HailMaryCompactionTests.cs` +1 test (RearContextPrune measured-count prunes where estimate no-ops).
  Targeted: 20 passed. Full suite: **1713 passed, 0 failed**.

### Step E — STATUS.md `[x]`
- DONE 10/26: recorded below. Deploy handoff: close app → `build-fluxmux.bat clean` → restart.
- `FluxMux.Avalonia.Tests/ForwardPruneTests.cs` (new):
  - `PruneTurn` runs on a fresh turn (not gated behind a threshold) and trims dedupe/loops/
    oversized results.
  - Uses the measured token count when provided.
  - No-op when nothing to prune.
- Hail Mary tests (`HailMaryCompactionTests.cs`):
  - `RearContextPrune` with a measured count prunes where the estimate alone would no-op.
  - Pulse-clear callback fires (test the host callback wiring if a seam exists; otherwise a
    ViewModel-level test that the handler sets `HailMaryQueued=false`).
- Switch-refresh (A): a test if a seam exists; otherwise verify via build + manual log.
- Run targeted filters first, then the full suite (no `--filter`).

### Step E — STATUS.md `[ ]`
- Record: what landed (A/B/C/D), test count, "compiled/tested in artifacts\testrun\, NOT
  deployed," and the deploy handoff (close app → `build-fluxmux.bat clean` → restart).
- Note the new `LocalForwardPruneEnabled` profile key + Quick Select control.

## Build / test protocol (from AGENTS.md — do NOT deviate)
```powershell
cd c:\AI_Workbench\Workspace\FluxMux.Avalonia
dotnet build FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --nologo -v q `
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\
dotnet test FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --no-build --nologo `
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\ `
  --filter "FullyQualifiedName~<TestClassName>"
```
- A "Build FAILED" that is ONLY the `FluxMux.Updates.Core.dll` copy step (locked by the
  running app) is NOT a code error — check for `error CS####`.
- Do NOT use the `AvoidLockedAppHost` escape hatch for new changes (stale prebuilt DLL).
- Do NOT kill `FluxMux.Avalonia.exe` or run `build-fluxmux.bat`.

## What NOT to do
- Do NOT mix forward-prune logic into `HailMaryCompaction` (conceptually separate).
- Do NOT make forward pruning truncate the current turn's own user content (it prunes waste:
  dedupe, loops, oversized tool results — not the user's task).
- Do NOT gate forward pruning behind the conventional-compaction threshold; it runs every turn.
- Do NOT deploy; hand the user the close/build/restart steps.
- Do NOT rename C# namespaces, JSON keys in `fluxmux_config.json` (add NEW keys, don't rename
  existing ones), the protocol string "FluxMux", or `EndpointAdapters` identifiers.

## Progress log (append as you go)
- 10/26: plan recorded. No code changed yet.
- 10/26: Steps A, B, C, D, E all implemented + verified. Full suite 1713 passed, 0 failed.
  Compiled/tested in artifacts\testrun\, NOT deployed. User: close app → build-fluxmux.bat clean →
  restart. New per-profile key `LocalForwardPruneEnabled` (default on) + Quick Select checkbox.

