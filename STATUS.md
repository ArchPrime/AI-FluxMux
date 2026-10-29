# AI-FluxMux — STATUS

**New chat? Read `BRIEFING.md` first** — it has the specific open items, key file map, and testing instructions.

Current version: **0.3.1 beta** (see `FluxMux.Updates.Core/AppVersionComparer.cs` → `FluxMuxAppInfo`).

## ✅ DONE: Don't park llama-server on first-byte hang for long-load/YaRN profiles (10/29)

**What was done:**

1. **Skip the park for long-load/YaRN profiles:**
   - `FluxMuxGatewayHost.cs` line 1819-1833: When a first-byte hang is detected for a long-load/YaRN profile (`_currentTurnIsLongLoad=true`), the `ParkLocalAfterHangAsync` call is skipped.
   - A first-byte timeout on a spill prefill is "slow", not "dead". Parking forces a cold reload on the next turn, which is even slower.
   - The server is kept warm; only park on a confirmed mid-stream stall (non-long-load profiles).
   - Log message: "local stream hang: skipping park for long-load/YaRN profile (first-byte timeout is slow, not dead)"

**Why this helps:**
- When you switch to a YaRN profile mid-chat, llama-server restarts with the new context size.
- The old KV cache is gone, so the new model has to prefill the entire conversation from scratch (takes minutes).
- Previously, the hang-watch would detect no first byte after 600s and park llama-server, forcing a cold reload on the next turn (even slower).
- Now, the server is kept warm, so the next turn is fast (KV cache is warm).

**Tests:** All 1728 tests pass.

**State:** COMPILED & TESTED — 0 build errors, 1728/1728 tests pass.

**Deploy:** Close the running app → `build-fluxmux.bat clean` → restart.

## ✅ DONE: Data-driven prefill progress indicator + generation phase (10/28)

**What was done:**

1. **Restored the full-width prefill progress indicator:**
   - The `PrefillPerformanceModel` (self-refining, persisted per model/variant) was already in `Services/` but the UI wiring was lost.
   - Added `PrefillProgress` (0–1, eased), `PrefillEtaText`, `GenerationPhaseText`, `GenerationDetailText`, `ShowGenerationDot`, and `IsIdle` ObservableProperties to `MainViewModel`.
   - Added a `DispatcherTimer` that ticks every 250ms during prefill to advance the progress bar and update the ETA.
   - The progress bar uses an eased curve: linear 0–95%, then exponential approach to 100% so it visibly decelerates near the end and only hits 100% on first byte.
   - `PrefillStartedNotice` now computes the estimate from the performance model and starts the timer.
   - `PrefillFirstByteNotice` now records the real sample into the model (refits + persists), completes the bar, and switches to the generation phase.

2. **Added the generation phase indicator:**
   - Reuses the existing `GenerationSpeedTracker` (already wired into the gateway) for live token count + speed.
   - A second `DispatcherTimer` ticks every 500ms during generation to update the elapsed clock + token count.
   - The timer self-terminates when the stream ends (snapshot no longer active) and resets the indicator to idle.
   - Visual: thin baseline (empty progress bar) + static dot + "Generating…" + elapsed + token count + speed.

3. **Removed the top-bar "Reply speed" toggle:**
   - The `GenerationSpeedTelemetryEnabled` toggle is now always `true` (generation tracking is a core UX element, not optional).
   - Removed the CheckBox and TextBlock from the top bar in `MainWindow.axaml`.
   - The generation indicator now lives in the bottom bar only.

4. **Bottom-bar indicator (always visible):**
   - One full-width `ProgressBar` (white fill, transparent background) that spans the graph panel's column.
   - Three states: Idle (0%, "Ready · <speed>"), Prefill (0→95% eased, "~Xs remaining · N tok/s"), Generation (thin baseline + dot, "Generating… · Xs · N tokens · M tok/s").
   - No layout shift between states (same bar, different fill/text).

**Tests:** All 1728 tests pass.

**State:** COMPILED & TESTED — 0 build errors, 1728/1728 tests pass.

**Deploy:** Close the running app → `build-fluxmux.bat clean` → restart.

## ✅ DONE: Force-compact for the Compact button + reconciled messaging (10/28)

**What was done:**

1. **Force-compact for the Compact button:**
   - `FluxMuxGatewayHost.cs` `TryApplyForwardCompact` now accepts a `forceCompact` parameter.
   - When `forceCompact=true` (user clicked Compact), bypasses all threshold checks and compacts immediately.
   - Uses the same rules as auto-compact (per-profile settings if available).
   - Clears the `forward_compact` flag after a successful compact (one-off action).
   - If the compact fails, logs a clear message explaining why and what the user should do.
   - Suppresses the compact offer for 15 minutes after a failed compact to prevent the button from reappearing on every turn.
   - Call site passes `forceCompact: compactOn` (true when the user explicitly clicked Compact).
   - Added `ClearForwardCompactFlag()` and `SuppressForwardCompactFor()` helper methods.

2. **Reconciled messaging:**
   - `LocalReloadRouting.cs`: Removed Cline-specific reference. Now says "If your endpoint offers manual compaction, you can use it to shorten the history before the next turn."
   - Hail Mary banner tooltip: Added "When to use: this is typically a last resort to reanimate a dead chat — use it when the current turn became large enough to kill the chat before auto-compact could trigger."
   - Compact button tooltip: Updated to explain it works even below the auto-compact threshold.
   - Compact status message: Updated to "AI-FluxMux will compress context on the next turn as requested. The Client app still has the full chat."

3. **Persistent prefill status indicator:**
   - Added `PrefillInProgress` and `PrefillStatusText` ObservableProperties to `MainViewModel`.
   - `PrefillStartedNotice` now sets the persistent status (shown in bottom bar) AND emits a terminal line for ≥30s estimates.
   - `PrefillFirstByteNotice` now hides the persistent status (no terminal line on completion).
   - Added a TextBlock to the bottom bar in `MainWindow.axaml` that shows the prefill status while a local turn is in progress.

4. **Terminal wording fixes:**
   - Removed redundant `[prefill]` prefix from terminal lines.
   - Removed misleading "preparing a reply now" line.
   - "Warming up" line only shows once per session.

5. **Forward-prune diagnostic:**
   - Added log line for token savings when forward-prune is active.

**Tests:** All 1728 tests pass.

**State:** COMPILED & TESTED — 0 build errors, 1728/1728 tests pass.

**Deploy:** Close the running app → `build-fluxmux.bat clean` → restart.
