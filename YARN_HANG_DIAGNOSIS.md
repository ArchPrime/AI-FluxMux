# YaRN "suddenly hangs" — root-cause analysis (10/26)

## IMPLEMENTED (10/26, A + B + D) — compiled + tested in artifacts\testrun\, NOT deployed
User must: close app → `build-fluxmux.bat clean` → restart.

- **A. Older-turn compact trigger moved to the VRAM boundary for YaRN profiles.**
  `Services/FluxMuxGatewayHost.cs` `TryApplyForwardCompact`: for a YaRN profile
  (`local_extend_context_into_ram && local_vram_context > 0`), the auto-compact trigger is now
  `min(0.80 × local_context, local_vram_context)` instead of `0.80 × local_context` (~315K, inside
  the spill zone). Mirrored in the Port-rules `ShouldForwardCompact` path (sets `filling=true` when
  the prompt has crossed `local_vram_context`). This delays spill + pulls the working set back onto
  VRAM. Never blocks the spill — only shortens older turns; the prompt is still forwarded.
- **B. Image-strip is now manual-only.** `Services/HailMaryCompaction.cs` `Salvage` gained a
  `stripImages` param (default true). Stage 4 (image-strip) is gated on it. The two automatic
  call sites (rejoin prune ~558, compact fallback ~2374) pass `stripImages: false`; the Hail Mary
  button (~773) keeps `stripImages: true`. Dedupe / loop-collapse / result-truncate stay always-on.
- **D. Misleading post-mortem label fixed.** `Services/PortRulesPostMortem.cs` `HangLimitName` now
  detects the long-load first-byte deadline (`>= ThinkingFirstByteSeconds`, default 600s) and labels
  it "Long-load first-byte", reporting the actual deadline (600s) instead of "Max think first-byte
  (limit 180)". `HangWhatHappened` / `HangChangeAdvice` updated to name the situation as a slow /
  RAM-spilled prefill.
- **Tests:** 3 new tests added (2 post-mortem long-load label, 1 image-strip gating). Full suite:
  1673 passed, 0 failed.

## NOT implemented (C) — deferred, awaiting user decision
- **C. Don't park llama-server on a first-byte hang for YaRN/long-load profiles.** A first-byte
  timeout on a spill prefill is "slow", not "dead"; parking forces a cold reload. Real-world
  tradeoff explained to user; not implemented yet.

## Symptom
YaRN-based profiles now abort with:
- `Probe request timed out after 5.0 s waiting for chat completion.`
- `This chat turn cannot continue: llama-server did not start a reply in time. ... stopped the turn when Max think first-byte hit 899s quiet (the limit is 180).`
- `Hang abort: parked llama-server ...`
- `Keeping the current model. This Client-app turn is over.`

## This is a regression, not a config problem
The entire "long-load / hang-watch" path is NEW in commit `59ac539`
(`v0.3.x: context-fill stress probe, hail-mary compaction, telemetry graph, port forwarding rules`, 10/24).
Before that commit, YaRN profiles used the **normal 25s first-byte** path and a 30s load window —
they did NOT sit 15 minutes and then park llama-server.

## What changed
1. `LocalLoadingRetryPolicy.IsLongLoadProfile(ctx, offload)` (NEW) returns true when
   `localContext > 131072` OR offload contains "cpu".
   - Because BRIEFING item 1 made `local_context` use the YaRN-extended value (~393216),
     **every YaRN profile now classifies as long-load** (and any GPU+CPU / CPU profile does too).
2. `FluxMuxGatewayHost` (NEW, lines ~1304-1342, 1521-1556):
   - When long-load, `hangClock.FirstByteDeadline = ThinkingFirstByteSeconds` (default 600s, clamp 60-900).
   - A new `WatchLocalStreamHangAsync` loop runs as long as `portRules.HangEnabled`.
   - On abort it calls `ParkLocalAfterHangAsync` → **llama-server is parked**.
3. `AskHangWaitAsync` (NEW, ~3779): when the deadline hits with no bytes, it offers
   "Wait longer / Resume / Switch to cloud". With **no user choice within 45s it defaults to
   "wait longer"** → `ResetForWaitLonger()` sets the deadline to `WaitLongerSeconds` (90s) and
   `ExtendCopyTimeout` (165s). This repeats, so the model can sit ~600s + several 90s windows
   before the final abort → matches the observed **899s**.

## Why the message is misleading
`PortRulesPostMortem.HangLimitName` labels the deadline by comparing it to caps:
`firstByteDeadlineSeconds (600) >= MaxThinkFirstByteSeconds (180)` → label "Max think first-byte",
and shows the limit as the **180 cap**, not the actual 600s deadline. So "hit 899s quiet (the limit
is 180)" really means: *the long-load 600s first-byte window elapsed, the model produced zero bytes,
and after the wait-longer ladder it finally parked.*

## The real problem (two parts)
A. **Behavioral regression:** YaRN profiles that used to just work now:
   - get a 600s+ hang clock,
   - auto-"wait longer" with no prompt when Port rules are off,
   - and **park llama-server on abort** ("The next local chat reloads it" → a cold reload every time).
B. **The model genuinely produced 0 bytes for ~15 min.** That is the actual hang. Most likely causes:
   - a genuinely slow/stuck cold load of the extended-context + RAM-spill profile (big KV alloc + paging), or
   - the model actually loaded but never started a reply (thinking, or stuck).
   The new TTFB telemetry (`Log("ttfb: first byte after ... ms")`) will tell which — check the gateway log.

## Recommended fixes (pick with user)
1. **Stop parking on a first-byte hang for long-load profiles.** A first-byte timeout on a cold YaRN
   load is usually "still loading / still thinking", not "server is broken". Parking forces a full
   reload next turn. Better: on `first_byte` abort, do NOT park; keep the server warm and let the
   next turn ride the already-loaded model (or only park on a confirmed stall mid-stream).
2. **Don't silently auto-"wait longer" forever when Port rules are off.** Either surface the prompt
   to the UI, or cap the total wait so a truly-stuck model fails fast instead of 15 min.
3. **Fix the misleading label.** Report the actual deadline (600s) and the real reason
   ("long-load first-byte window elapsed, 0 bytes"), not "Max think first-byte ... limit 180".
4. **Verify the cold load actually completes.** Add/inspect the `ttfb` + `long-load detect` +
   `local loading retry` log lines for the failing turn to confirm whether it's a load problem
   (A) or a no-reply problem (B).

## Key files / lines
- `Services/LocalLoadingRetryPolicy.cs` — `IsLongLoadProfile` (33), `LongLoadMaxTotalWaitSeconds=300` (25)
- `Services/FluxMuxGatewayHost.cs` — long-load detect (1304-1342), hang-clock setup (1521-1556),
  `WatchLocalStreamHangAsync` (3697), `AskHangWaitAsync` (3779), abort+park (1720-1757),
  `ReadThinkingBudget` (3120), `RequestTimeoutForPayload` (3144)
- `Services/LocalStreamHangPolicy.cs` — `ThinkingFirstByteSeconds=600` (16), `MaxThinkFirstByteSeconds=180` (15),
  `WaitLongerSeconds=90` (18), `ResetForWaitLonger` (197)
- `Services/PortRulesPostMortem.cs` — `HangLimitName` (431), `FormatHang` (270)
- `Services/FluxMuxRuntimeService.cs` — `_managedLocalContext` (3534), `local_context` write (8016),
  `local_gpu_offload` write (8031), "Probe request timed out" (4564), "Hang abort: parked" (915)
- `Services/PortForwardingRules.cs` — `ForForwarding()` sets `HangEnabled=false` when off (107-133)

## NOTE on "Port rules off"
The log says the safety check "runs even when Port rules are off". `ForForwarding()` sets
`HangEnabled=false` when the master switch is off, which would make `WatchLocalStreamHangAsync`
NOT run. So the observed hang likely means the master switch is ON (or the in-memory rules predate
the toggle). Confirm the actual Port-rules state when reproducing.

### CONFIRMED root cause (round 3) — forward-on-the-fly compact now triggers TOO LATE for YaRN

User's hypothesis was right: the regression is the **forward-on-the-fly auto-compact** system
(introduced in the same commit as Hail Mary, `59ac539`).

The auto-compact trigger is computed against `local_context` (`FluxMuxGatewayHost.cs` ~2258, ~2292):
    autoContext = local_context          // now the FULL YaRN window, ~393216
    triggerThreshold = autoContext * AutoCompactTriggerPercent   // default 0.80
    if (autoPromptTokens >= triggerThreshold) -> compact

Because `local_context` was raised to the full YaRN window (item 1), the trigger moved from
~0.80 × 213K ≈ **170K** (≈ the VRAM-resident context, where it used to fire and pull the prompt
back onto the fast card) to **0.80 × 393K ≈ 315K**.

Consequence: a prompt that spills past the VRAM-resident context (~213K) but is under ~315K is
**NOT compacted** — it is forwarded to llama-server into the slow RAM-spill zone. llama-server then
prefills it from RAM, produces no first byte for a long time, and the new hang-watch parks the
server after the 600s+ window. That is exactly the "never used to wait 15 min on a spill" symptom.

The Port-rules compaction path (`ShouldForwardCompact`, `LocalHistoryCompaction.cs` ~51) has the
same problem: `IsNearLimit` / `promptExceeds` are all computed against `local_context` (393K), so
they also don't fire until the prompt is near the full YaRN window.

So BOTH compact paths (auto-compact + port-rules) are keyed to the full YaRN window, not to the
VRAM-resident context. The fix is to make the VRAM-spill boundary (`local_vram_context`) a compact
trigger for YaRN profiles, so the prompt is pulled back onto the card before it is forwarded into
slow RAM.

### Fix (concrete, matches "used to work")
In `TryApplyForwardCompact` (FluxMuxGatewayHost.cs ~2217), for a YaRN profile
(`local_extend_context_into_ram == true` and `local_vram_context > 0`), ALSO trigger compact when
`promptTokens >= local_vram_context` (the VRAM-resident boundary), not only when
`promptTokens >= 0.80 × local_context`. I.e. use:
    effectiveTrigger = min(0.80 × local_context, local_vram_context)
so the prompt is compacted before it spills past the fast card. This restores the pre-regression
behavior where a spill-zone prompt got compacted and never reached llama-server as a slow RAM prefill.

(Optionally also fix the hang-watch so it does NOT park llama-server on a first-byte hang for
YaRN/long-load profiles, and fix the misleading post-mortem label — but the compact-trigger fix
above is the one that stops the 15-min waits.)


---

## Refined diagnosis (10/26, round 2) — VRAM-spill transition

User clarification: the hang is NOT on a plain cold load (which is fast). It happens when the
**YaRN/extended-context version** of a model is running and the **VRAM-resident context fills up
and the prompt spills into RAM**. "This used to work well" — so the spill path regressed.

### Two distinct code paths (important to keep separate)
1. **Pre-flight "filling" block** (`ShouldBlockLocalForward` → `EvaluatePromptFillsLocalContext`,
   `FluxMuxGatewayRouting.cs` ~189-291, called from `FluxMuxGatewayHost.cs` ~802). This returns a
   **400** ("too large to fit into the remaining Context space") BEFORE the prompt reaches
   llama-server. It blocks only when `prompt > local_context` (strict) or when within 10% headroom
   and `prompt+max_tokens > local_context`. It does NOT produce the hang-abort.
2. **The hang-abort the user sees** (`WatchLocalStreamHangAsync` → `ParkLocalAfterHangAsync`,
   `FluxMuxGatewayHost.cs` ~3697, ~1720-1757). This is what fires when the prompt **was sent** to
   llama-server and the model produces **0 bytes** for the first-byte window. THIS is the symptom.

So the user's prompt passed the pre-flight check (prompt < local_context = 393216) and was forwarded.
llama-server then had to process a prompt that is in the **VRAM-spill zone** (between the VRAM-resident
context and the full YaRN window), and it produced no first byte for ~15 min → hang-abort.

### Why this regressed (the two new things that interact badly)
- **`local_context` is now the full YaRN window (~393216)** (BRIEFING item 1; `FluxMuxRuntimeService.cs`
  ~3534, `OverrideContext` carries the extended value). The pre-flight block now only trips at ~393K,
  so prompts up to ~393K are **forwarded** to llama-server even when they are far past the VRAM-resident
  context (e.g. 213K). Before, with a smaller `local_context`, such a prompt would have been blocked/compacted
  earlier and never sent into the slow spill zone.
- **The spill-compact dialog is non-blocking** (`OfferSpillCompactIfNeeded`, `FluxMuxGatewayHost.cs`
  ~2386): it only *suggests* compacting when the prompt crosses the VRAM boundary (urgency 0/1/2). If the
  user declines (or it doesn't fire), the oversized prompt is forwarded to llama-server → slow RAM-spill
  prefill → 0 first bytes → the new hang-watch parks the server after the 600s+ window.

Net effect: a YaRN prompt that spills past VRAM now (a) gets forwarded instead of compacted/blocked early,
and (b) when llama-server can't prefill it fast enough, the new hang-watch kills the turn **and parks
llama-server**, forcing a cold reload next turn. That is the "never used to" behavior.

### The real question to settle (needs the gateway log)
Is the 15 min a **slow RAM-spill prefill** (llama-server genuinely working but slow) or a **stuck/dead
llama-server** (no progress at all)? The new telemetry answers it:
- `long-load detect: ctx=... offload=... isLongLoad=...` (FluxMuxGatewayHost ~1317)
- `local loading retry N/... after llama-server 503` (~1378) — present only if it was still loading
- `ttfb: first byte after ... ms` (~1581) — present only if a byte ever arrived
- `local stream hang: first_byte after ...s` (~3733)
Check the log for the failing turn to see which lines appear.

### Recommended fixes (updated for the spill case)
1. **Compact BEFORE forwarding into the spill zone.** When the prompt exceeds the VRAM-resident
   context (`local_vram_context`) on a YaRN profile, run the forward-compact so the prompt is pulled
   back onto the fast card instead of being sent into slow RAM. Today compaction triggers off
   `local_context` (393K) — far too late. Trigger it off the VRAM boundary (or the spill-compact
   urgency) so the prompt never enters the slow zone. This is the closest match to "used to work".
2. **Don't park llama-server on a first-byte hang for long-load/YaRN profiles.** A first-byte timeout
   on a spill prefill is "slow", not "dead". Parking forces a cold reload. Keep the server warm; only
   park on a confirmed mid-stream stall.
3. **Fix the misleading post-mortem label** (report the real 600s deadline + "0 bytes in spill prefill",
   not "Max think first-byte ... limit 180").
4. **Confirm with the log** (see above) whether it's slow-prefill or dead-server before choosing 1 vs 2.

