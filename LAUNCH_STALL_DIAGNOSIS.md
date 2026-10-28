# Launch Stall Diagnosis (dev build) — 10/28

## Symptom
Dev build (`Workspace\FluxMux.Avalonia\bin\Debug\net10.0`) launch **stalls indefinitely** waiting for a
response from llama-server. Backup build works fine. Appeared after code changes around
**launch telemetry and sequence**.

## Root cause (established from source diff)
The dev `FluxMuxRuntimeService.cs` (393,174 bytes, mod 10/28 03:03) added a **readiness gate** that the
backup (383,527 bytes, mod 10/27 01:07) does NOT have. 202 lines differ. The new members:

- `_localModelReadyTcs` (TaskCompletionSource) + `_localModelReady` (volatile bool)
- `LocalModelReadyWaitCapMs = 20000`
- `MarkLocalModelReady()` / `MarkLocalModelNotReady()` / `WaitForLocalModelReadyAsync()`
- Wired into the gateway: `FluxMuxGatewayHost.cs` L1492-1494 awaits `WaitForLocalModelReady` on every
  local chat turn BEFORE sending the request to llama-server.

### The gate's intended behavior
- `MarkLocalModelNotReady()` is called at the start of `StopLocalCoreAsync` (L3403) — resets the gate.
- `MarkLocalModelReady()` is called on the launch-success path (L4037) and the reuse path (L3715).
- `WaitForLocalModelReadyAsync` is BOUNDED to 20s (`LocalModelReadyWaitCapMs`); on timeout it proceeds
  anyway so the gateway's 503-retry loop takes over.

### Why it still stalls
The 20s cap is a **per-request** bound, not a launch bound. The launch-wait loop in
`LaunchLocalAsync` (L3934 `while (DateTime.UtcNow < deadline)`) is bounded by `launchBudgetMs`
(~78s for the active "YaRN test with vision" variant: median 17,972ms + 60s headroom, clamped to
[60s,180s]). Inside that loop:
- `IsLlamaEndpointReadyAsync(localBackendPort)` (L3971) returns true as soon as the daemon port opens
  and `/health` or `/v1/models` returns 2xx/503.
- Then `ProbePublicEndpointWithShortRetriesAsync` (L3978) POSTs through the **public** port (5001).
  That POST hits the gateway, which now **awaits the readiness gate** (L1494). The gate is only
  released by `MarkLocalModelReady()` (L4037) — which only runs when the probe SUCCEEDS.

So during the cold-load window the probe can fail (503 "Loading model") and the loop keeps polling
until `deadline`. That is bounded (~78s) and should return "warming up" / "still in progress".
**If the user sees an INDEFINITE stall, the loop is not reaching its success branch** — most likely
because the probe's 503 is being retried by the gateway's long-load 503-retry loop
(`LocalLoadingRetryPolicy`: 150 retries × 2s = up to 300s for a YaRN profile), so each probe attempt
itself can hang up to 300s, far exceeding the launch budget. The launch loop's `deadline` check only
runs between probe attempts, so a single probe that rides out the gateway's 300s 503-retry window
makes the whole launch appear to stall for minutes with no progress.

## What to verify next
1. Is the stall truly indefinite, or ~3-5 minutes then "warming up"? (Distinguishes the 300s
   gateway 503-retry from a true hang.)
2. Does the dev build write to `C:\AI_Workbench\Logs\Proxy_Cloud_Handshake.log`? Grep for
   `local loading retry` lines during a launch attempt — if they appear and keep incrementing,
   the gateway's 503-retry loop is the culprit.
3. Check `Local_Runtime_State.json` `phase` during the stall: "warming" vs "transitioning".

## Candidate fixes (not yet applied)
- **A (preferred):** In `SendChatProbeAsync` / `TimeShortReadyReplyAsync`, force the probe to BYPASS
  the gateway's long-load 503-retry window. The probe already has a 5s `requestTimeoutMs`, but the
  gateway's `timeoutCts` is extended to 300s for long-load profiles (FluxMuxGatewayHost L1455-1462),
  so the 5s probe timeout is overridden by the gateway's longer window. Pass a header or flag that
  tells the gateway to use a short timeout for probe requests.
- **B:** Reduce `LongLoadMaxTotalWaitSeconds` (300) or `LongLoadRetryCount` (150) so a stuck probe
  fails faster.
- **C:** In the launch-wait loop, check `deadline` MORE aggressively (the loop currently only checks
  it at the top of each iteration; a single long probe blocks the check).

## APPLIED FIX (10/28, compiled in `artifacts\stallfix\`, 0 errors, NOT deployed)
Two-part fix implementing option A:
1. **`FluxMuxRuntimeService.cs` `SendChatProbeAsync` (L4789):** Now sets
   `CloudEndpointValidationProbe.ProbeRequestHeader` ("X-FluxMux-Probe-Request: true") on every probe
   request. This tells the gateway the request is a probe, not a real user chat turn.
2. **`FluxMuxGatewayHost.cs` 503-retry loop (L1517-1519):** The retry condition now includes
   `&& !isProbeRequest`. Probe requests skip the long-load 503-retry window entirely, so they fail
   fast (within the probe's own 5s timeout) instead of riding out the gateway's 300s retry loop.

**Why this works:** The launch-wait loop polls the public endpoint repeatedly. Each probe now fails
fast (~5s) instead of hanging up to 300s, so the loop's `deadline` check runs every ~5s and the
launch progresses normally. When llama-server is actually ready, the probe succeeds and
`MarkLocalModelReady()` releases the gate. Real user chat turns (not probes) still get the full
long-load 503-retry window, so cold loads for real conversations still work.

**Deploy:** User must close app → `build-fluxmux.bat clean` → restart. Record in STATUS.md.

## Files
- `Services/FluxMuxRuntimeService.cs` — readiness gate (L342-350, L1140-1193, L3403, L3715, L4037),
  launch-wait loop (L3934-4133), probe (L3104-3148, L4763-4847).
- `Services/FluxMuxGatewayHost.cs` — gate await (L1492-1494), 503-retry loop (L1507-1552),
  long-load timeout extension (L1455-1462).
- `Services/LocalLoadingRetryPolicy.cs` — retry budget (150 × 2s = 300s for long-load profiles).
