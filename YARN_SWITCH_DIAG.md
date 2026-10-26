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


## Do NOT
- Do NOT close/kill the running app to test (see AGENTS.md "Testing FluxMux Changes").
- Do NOT use the `AvoidLockedAppHost` escape hatch for new changes.
- Deploy is a user action: close app → `build-fluxmux.bat clean` → restart.
