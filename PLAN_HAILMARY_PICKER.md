# PLAN — Hail Mary "rear-context prune" with one-off aggression picker (READY TO PICK UP)

> Recorded 10/26. This is the next task after the GPU-settings-tab investigation.
> The A+B+D round (YaRN compact trigger + image-strip manual-only + post-mortem label) is
> compiled/tested in artifacts\testrun\ and (per user) recompiled + relaunched.

## Context: what A+B+D did (deployed by user)
- **A.** Older-turn compact trigger moved to the VRAM boundary for YaRN profiles
  (`FluxMuxGatewayHost.cs` `TryApplyForwardCompact` + Port-rules `ShouldForwardCompact` mirror).
- **B.** Image-strip is manual-only: `HailMaryCompaction.Salvage` gained `stripImages` param
  (default true); automatic sites pass `stripImages: false`, the button keeps `true`.
- **D.** Post-mortem labels a 600s first-byte deadline as "Long-load first-byte" (not
  "Max think first-byte (limit 180)").

## THIS TASK: Hail Mary rear-context prune + one-off aggression picker

### Concept (agreed with user)
- The Hail Mary button is a **manual, one-off** emergency for a turn that FAILED on context.
- It is a **rear-context prune** (drop older conversational turns), NOT a forward prune.
  Goal: let the **full forward turn** complete **with room to spare**.
- A **one-off 3-stop aggression picker** + an **independent "also strip older images" toggle**,
  on **BOTH** Hail Mary surfaces. NOT persisted — resets each time the control appears.
- If the user has a cloud model or larger local model, they'd use those first; this is the
  last-resort for the current (too-small) local model.

### The 3 stops
| Stop        | Rear turns kept | Headroom left for forward turn | Intent |
|-------------|-----------------|--------------------------------|--------|
| Gentle      | ~8 recent       | just fit under window          | "nudge it under" |
| Balanced    | ~4 recent       | ~20% of window spare           | "comfortable margin" |
| Aggressive  | ~2 recent       | ~40% of window spare           | "drop most of the rear, lots of room" |

Always preserved regardless of stop: system prompt, last real user task, active tool-pair
(tool-pair-safe). The stop only governs how many OLDER conversational turns are dropped + how
much headroom the forward turn gets.

### Layout (agreed)
```
[ Context warning: ... ]
[ Gentle ] [ Balanced ] [ Aggressive ]   [ ] also strip older images
[ Hail Mary: shrink this turn ]   [ Dismiss ]
```
- 3-stop segmented picker (one highlighted); default highlight = Balanced.
- "also strip older images" = checkbox, default OFF (independent of the stop).
- Main button arms the cut with (stop, stripImages). Outcome text reflects both.
- **BOTH surfaces need it:**
  1. Banner button — `Views/MainWindow.axaml` ~3299 ("Hail Mary: shrink this turn" in the
     context-warning banner).
  2. Diagnostics-bar button — `Views/MainWindow.axaml` ~3155 (always-visible Hail Mary button
     in Diagnostics when a local model is loaded).
  Both currently call `HailMaryShrinkTurnCommand` → `RequestHailMaryMiddleCut()`.

### Implementation touch-points
1. **`Services/HailMaryCompaction.cs`**
   - New enum `HailMaryAggression { Gentle, Balanced, Aggressive }` with
     `(keepTurns, headroomFraction)` = `(8, 0.0)`, `(4, 0.20)`, `(2, 0.40)`.
   - New method `RearContextPrune(payload, contextTokens, aggression, stripImages)`:
     - `target = contextTokens * (1 - headroomFraction)`.
     - Runs the existing middle-cut (stage 5) with `minKeepTurns = keepTurns`, aimed at the
       headroom target (not "just fit").
     - Runs stage 4 (image-strip) only when `stripImages` true.
     - Does NOT run stages 1-3 as the primary action (those are forward-prune/waste).
       (Optionally dedupe first — pure waste — but the PRUNE is the middle-cut.)
     - Returns report (turns dropped, tokens freed, headroom achieved, images stripped).
   - Keep `Salvage` unchanged so automatic paths (rejoin ~558, compact fallback ~2388) are untouched.

2. **`Services/FluxMuxRuntimeService.cs`**
   - Change `_hailMaryMiddleCutRequested` (bool) → `_hailMaryAggression` (enum + bool stripImages),
     or a small struct. Default = none sentinel.
   - `RequestHailMaryMiddleCut(HailMaryAggression stop, bool stripImages)` sets it.
   - `ConsumeHailMaryMiddleCutRequest()` returns (stop, stripImages) and clears.

3. **`Services/FluxMuxGatewayHost.cs`** ~768 (where `ConsumeHailMaryMiddleCut?.Invoke()` is consumed)
   - Call `HailMaryCompaction.RearContextPrune(payload, hotContext, stop, stripImages)` instead of
     the current `Salvage(..., allowMiddleCut: true)`.

4. **`Views/MainWindow.axaml`** — add the picker + checkbox to BOTH surfaces (~3155 and ~3299).
   Consider a shared control so both stay in sync.

5. **`ViewModels/MainViewModel.cs`**
   - Add properties: selected stop (enum, default Balanced), stripImages (bool, default false).
   - Change `HailMaryShrinkTurn()` to pass (selectedStop, stripImages) to
     `RequestHailMaryMiddleCut`.
   - Outcome text reflects both ("Armed (Balanced, strip images) — ...").
   - Reset the picker to defaults each time the banner/button becomes visible (one-off, not persisted).

6. **Tests** (`FluxMux.Avalonia.Tests/HailMaryCompactionTests.cs`)
   - `RearContextPrune`: for each stop verify (a) correct older turns dropped, (b) system prompt +
     last user task + tool-pair preserved, (c) result under `contextTokens * (1 - headroomFraction)`.
   - Verify automatic `Salvage` paths unchanged.

### What NOT to do
- No persistence (one-off).
- No change to automatic junk-clearing (`ClearOlderResults`) or automatic `Salvage` paths.
- No exposure of per-stage constants as separate sliders — the 3-stop picker captures intent.
- Image-strip is INDEPENDENT of the stop (separate checkbox), default off.

### Status
IMPLEMENTED (10/26) — compiled + tested in artifacts\testrun\, NOT deployed.
User must: close app → `build-fluxmux.bat clean` → restart.

Implemented:
- `HailMaryCompaction.HailMaryAggression` enum (Gentle/Balanced/Aggressive) + `KeepTurnsFor` /
  `HeadroomFractionFor` + `RearContextPrune(payload, contextTokens, stop, stripImages)`.
- `FluxMuxRuntimeService`: `_hailMaryAggression` + `_hailMaryStripImages` fields;
  `RequestHailMaryMiddleCut(stop, stripImages)`; `ConsumeHailMaryMiddleCutRequest()` returns
  `(Stop, StripImages)`.
- `FluxMuxGatewayHost`: `ConsumeHailMaryMiddleCut` delegate now returns the tuple; call site ~769
  uses `RearContextPrune` (not `Salvage`).
- `MainViewModel`: `HailMaryAggressionStop` (default Balanced) + `HailMaryStripImages` (default
  false) observable props; `HailMaryShrinkTurn` passes (stop, stripImages); `ResetHailMaryPicker()`
  called when banner/button becomes visible (one-off, not persisted).
- `MainWindow.axaml`: banner (~3299) has inline 3-stop RadioButtons + "also strip older images"
  CheckBox; diagnostics-bar button (~3146) opens a Flyout picker with the same controls.
- `HailMaryStopToBoolConverter` (new) registered as `HailMaryStopToBool` resource.
- `MainWindow.axaml.cs`: `HailMaryFlyoutCancel_Click` closes the flyout without arming.
- Tests: 2 new (`RearContextPrune_drops_older_turns...`, `RearContextPrune_is_noop...`).
  Full suite: 1675 passed, 0 failed.

NOT deployed. Awaiting user recompile + relaunch to test as a live test case.
