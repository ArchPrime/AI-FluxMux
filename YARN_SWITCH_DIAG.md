# YaRN mid-chat switch failure — DIAGNOSIS HANDOFF

> **Active investigation.** If a new chat resumes this, read this file first, then pull the log
> from the offset below. The gateway log is written by the RUNNING APP, so it persists even if
> the chat that triggered the failure drops.

## Symptom
Switching to a YaRN (context-extended) profile **part-way through a chat** now fails / times out.
This "never used to happen" — it regressed. Full-VRAM profiles work fine.

## How to capture the failure (the user can trigger it; the agent may not be alive during it)
1. The gateway log is `c:\AI_Workbench\Logs\Proxy_Cloud_Handshake.log` (written by the app, ~60MB).
2. **Offset marker:** everything from byte **60,256,015** onward (marked 01:54:18, 10/27) is from
   the user's upcoming test. Read ONLY the new bytes:
   ```powershell
   $l='c:\AI_Workbench\Logs\Proxy_Cloud_Handshake.log'
   $b=[System.IO.File]::ReadAllBytes($l)
   $text=[System.Text.Encoding]::UTF8.GetString($b[60256015..($b.Length-1)])
   $text | Select-String -Pattern 'long-load detect|ContextDiag|route decide|503|400|exceed_context|too large|first byte|FirstByteDeadline|hang clock|stream hang|switch|launch|spilled|overflow'
   ```
   (If the user re-triggers later, re-mark the offset first: `(Get-Item $l).Length`.)

## What to look for in the captured log (right after the switch)
- `long-load detect: ctx=...` — is it the YaRN context (e.g. 393216 / 1048576) or still the old
  full-VRAM value (262144)? A wrong/stale value here is the prime suspect.
- `503` retries (cold load in progress) vs `400 exceed_context` (prompt too big for new ctx).
- `ttfb: first byte after N ms` — is it approaching the 600s `FirstByteDeadline`?
- The hang-clock abort message and the final error/timeout text.
- `route decide ... n_prompt=... n_fill=...` vs `ContextDiag ... local_context=...` — is the
  prompt already a large fraction of (or larger than) the post-switch context?

## Working hypotheses (verify against the log, in priority order)
- **H1 (most likely):** Mid-chat switch to YaRN triggers a **cold load** of the larger context that
  exceeds the timeout, OR the in-flight prompt is now large relative to the new context so prefill
  is slow → hang clock aborts at 600s.
- **H2:** My **Step A YaRN switch refresh** (`FluxMuxRuntimeService.cs:8106-8127`) overwrites a
  correct `local_context`/`local_vram_context` with a stale managed value on the switch, so the
  blocking/timeout logic misbehaves.
- **H3:** The YaRN profile's `OverrideContext` is set too low (known bug, see AGENTS.md
  "Known Bug: Auto-Compaction Triggering Too Early"), so the switch lands on a context too small
  for the current chat.
- **H4:** A genuinely huge prompt (150K+ tokens) on the new profile makes prefill slow enough to
  hit the first-byte deadline regardless of YaRN.

## Key code locations (already identified)
- Step A switch refresh: `Services/FluxMuxRuntimeService.cs:8106-8127` (refreshes local_context,
  local_extend_context_into_ram, local_vram_context each turn when they differ).
- Managed context source: `FluxMuxRuntimeService.cs:3624` (`_managedLocalContext =
  GetProfileInt(profile, "OverrideContext", 0)`).
- Long-load detect + timeout: `Services/FluxMuxGatewayHost.cs:1334-1372`
  (`IsLongLoadProfile`, `EffectiveMaxTotalWaitSeconds`, `FirstByteDeadline`).
- Launch `-c` (context) arg: `FluxMuxRuntimeService.cs:5448-5453`
  (`OverrideContext` → `-c <profileInt>`).
- `--ctx-shift` (YaRN rope scaling) arg: `FluxMuxRuntimeService.cs:5605-5609`.
- `IsLongLoadProfile` / `EffectiveMaxTotalWaitSeconds`: `Services/LocalLoadingRetryPolicy.cs`.

## Status
- [x] User triggered the failure (full-VRAM chat → switch to YaRN mid-chat → send next turn).
- [x] Agent read log from offset 60,256,015 and confirmed root cause.
- [ ] Propose + implement the fix (plan → act).
- [ ] Update STATUS.md + PLAN_CONTEXT_FIXES.md with the outcome.

## CONFIRMED ROOT CAUSE (from log, 01:54–02:06, 10/27)
The YaRN switch **works correctly** — context flips to 393216, variant "YaRN test with vision",
offload "GPU + CPU", model reloads (503 → 200 at 01:58:31). It is NOT a failure/bug in the
switch. The "timeout" the user sees is a **~7.3-minute prefill** of the in-flight chat:

Timeline:
- 01:54–01:58: full-VRAM profile (ctx=262144, GPU only) handling ~155K-token prompts (30s first-byte).
- 01:58:19–01:58:29: SWITCH. llama-server 503 (cold load). ctx→393216, offload→GPU+CPU.
- 01:58:31: model ready (200).
- 01:58:52: real turn sent: n_prompt=157270, n_fill=314541, **ram_spill=True**.
- 01:59:23: first byte after 30,245 ms.
- 02:06:10: **stream complete after 437,187 ms (~7.3 min total).**

Why so slow:
1. The in-flight prompt is huge (~157K tokens / ~314K fill) — already near the limit on the
   full-VRAM profile.
2. The YaRN profile uses **GPU + CPU offload** (vs full-VRAM's GPU only) → CPU prefill is far slower.
3. **ram_spill=True** → KV cache spills to RAM → memory-bandwidth latency.

So the model is not failing; it's just extremely slow on a large prompt with CPU offload + RAM
spill. Cline/the user perceives 7 min with no first token as a "timeout/hang".

Why it "never used to happen" (to confirm with user):
- (A) The chat was smaller when they used to switch → smaller prompt → faster prefill.
- (B) The YaRN profile's offload mode changed to GPU+CPU (slower for large prompts).

## Proposed fix directions (discuss with user before implementing)
- **F1 (UX, recommended):** On a mid-chat switch to a profile with a *slower* offload (GPU+CPU)
  or *smaller* effective context, show a warning: "This profile will prefill the current ~157K
  prompt slowly (CPU offload + RAM spill). Consider starting a new chat or using Hail Mary to
  trim first." This sets expectations instead of a silent 7-min hang.
- **F2 (auto-trim):** On switch, if the in-flight prompt is large relative to the new profile's
  context AND the new profile is slower-offload, offer to auto-run a Hail Mary trim before the
  next turn so the prompt is smaller and prefill is faster.
- **F3 (raise first-byte deadline):** The 600s FirstByteDeadline is fine here (it didn't abort —
  the turn completed at 437s). No change needed unless the user wants a longer patience window.
- **F4 (offload guidance):** Surface the offload-mode difference on switch so the user knows the
  YaRN profile is CPU-offloaded and will be slower for large prompts.

## Decision needed from user
Which fix direction? (F1 warning is the safest/cheapest; F2 auto-trim is more aggressive.)
Also confirm: was the chat smaller before, or did the YaRN profile's offload mode change?

## RESOLVED (10/27) — Three fixes implemented, tested, NOT deployed

The user confirmed the scope: **three fixes**, preserving continuity (system prompt + initial
user task + most recent user task + recent turns), with a prefill progress indicator (with ETA).
The "aggressive trim / re-anchor on press" idea was **dropped** — it's auto-compact in disguise
and doesn't solve the "big next turn" problem. No llama-server KV-cache trimming (not possible
with `--no-slots`).

### Fix 1 — Hail Mary middle-cut drops the OLDEST turns (the actual bug)
**File:** `Services/HailMaryCompaction.cs` (`MiddleCutOlderTurns`).
- **Was:** the loop walked BACKWARD from `lastRealUser - 1` down to `lastRealUser - minKeepTurns + 1`,
  dropping only the `minKeepTurns - 1` turns immediately before the last task. It never touched the
  oldest turns — exactly where the bloat lives — so the prune was a no-op when the prompt was
  genuinely over the limit.
- **Now:** drops from the OLDEST end (just after the initial brief) upward, until the prompt fits
  under the target or the protected floor is reached. Protected floor (never dropped):
  - index 0 (system prompt)
  - the **initial brief** (first real user task) — the original request
  - the **last real user task** + the most recent `minKeepTurns` turns before it
- Tool-pair safety preserved: a tool result is only dropped if its matching assistant call is also
  in the drop range (a kept call's result is never orphaned).
- Bounds-safe: the loop guards with `i < messages.Count` and recomputes the keep-floor each
  iteration (the array shrinks as turns are dropped).
- New `MiddleCutStillOver` flag on `HailMaryReport`/`HailMaryResult`: true when the prune dropped
  turns down to the protected floor but the prompt STILL exceeds the target (the turn itself is too
  big). Feeds Fix 2.

### Fix 2 — Honest "turn too large" messaging
**Files:** `Services/FluxMuxGatewayRouting.cs` + `Services/FluxMuxGatewayHost.cs`.
- New `LocalTurnTooLargeAfterTrimMessage` constant: tells the user Hail Mary already trimmed the
  older context (keeping system + initial brief + recent turns) but the turn is still too large to
  fit, and points to the real options (bigger-context model, or a smaller turn).
- `FormatFillingBlockedMessage` / `FormatBlockedLocalTurnMessage` now take a `hailMaryStillOver`
  param and append the message when true.
- `FluxMuxGatewayHost` captures `hailMaryStillOver` from the `RearContextPrune` result and passes
  it to the block message.

### Fix 3 — Prefill progress indicator (with ETA)
**Files:** `Services/FluxMuxGatewayHost.cs`, `Services/FluxMuxRuntimeService.cs`,
`ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml`.
- Gateway fires `ReportPrefillStarted(promptTokens)` when a local turn is sent to llama-server
  (prefill begins) and `ReportPrefillFirstByte(elapsedMs)` when the first byte arrives (prefill
  complete). Only for local routes.
- `FluxMuxRuntimeService` forwards these to `PrefillStartedNotice` / `PrefillFirstByteNotice` events.
- `MainViewModel` shows a live "Prefilling context… elapsed Xs · ~N tokens · ETA ~Y" label (updates
  every second via a timer) while prefill is in progress; clears it on first byte. The ETA uses a
  conservative ~200 tokens/sec baseline (slow GPU+CPU offload with RAM spill) and is labeled "~"
  (a guess, not a promise).
- XAML: a `TextBlock` bound to `PrefillStatusText`, visible when `PrefillInProgress`, in the
  diagnostics row (Grid.Column 5, next to the Hail Mary outcome).

### Tests
**File:** `FluxMux.Avalonia.Tests/HailMaryCompactionTests.cs`.
- Updated `RearContextPrune_drops_oldest_turns_to_headroom_and_preserves_brief_task_and_recent`
  (was `..._preserves_task_and_toolpair`): asserts oldest turns dropped, system + initial brief +
  last task + recent turns preserved.
- Updated `Stage5_middle_cut_is_button_gated_and_tool_pair_safe`: tool-pair now in the recent floor
  (protected), oldest turns dropped.
- Added `MiddleCut_drops_oldest_turns_first_not_recent`, `MiddleCut_preserves_initial_brief_even_when_many_middle_turns_dropped`,
  `MiddleCut_reports_still_over_when_a_single_turn_alone_exceeds_window`,
  `MiddleCut_tool_pair_safety_across_drop_boundary`.
- **Full suite: 1717 passed, 0 failed.**

### Deploy state
- **Compiled/tested in `artifacts\testrun\`, NOT deployed.** User must: close app →
  `build-fluxmux.bat clean` → restart.

## Do NOT
- Do NOT close/kill the running app to test (see AGENTS.md "Testing FluxMux Changes").
- Do NOT use the `AvoidLockedAppHost` escape hatch for new changes.
- Deploy is a user action: close app → `build-fluxmux.bat clean` → restart.
