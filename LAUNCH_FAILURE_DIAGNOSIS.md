# Launch Failure Diagnosis — "sometimes cannot launch" (11/6)

## Symptom
FluxMux "cannot launch" intermittently. The terminal log shows:
- `daemon port 5002 is up; setting local_phase=warming` (repeated)
- `IsModelActuallyReadyAsync(5002) = False` (repeated, ~59s for the 11/6 23:42 launch)
- `Probe request timed out after 30.0 s waiting for chat completion.`
- `launch: daemon port ready, checking public endpoint` (repeated for 470+ s)
- "Your model is still loading … It's been Ns so far." (N climbs to 470+)
- Eventually gives up / user sees "cannot launch".

## Root cause (confirmed via logs)
The **public-endpoint readiness probe** is a *real, non-streaming chat completion*
(`SendChatProbeAsync`, `FluxMuxRuntimeService.cs` ~4822) with a **hard 30 s timeout**
(`PublicEndpointReadyProbeTimeoutMs = 30000`, line 244). It sends a trivial
"Reply with the single word ready." prompt (`max_tokens=16`).

On the **YaRN GPU+CPU (RAM-spill) profile** (`Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf`,
`n_ctx_slot=393216`, offload=GPU+CPU), the *first* chat completion after a cold start
pays a large fixed overhead (weight load into GPU + KV-cache init + MTP draft-context
build). llama-server log evidence (`llama_server_output.log`):
- Even a **3,422-token** prompt took **17.7 s** to prefill (~193 tok/s).
- A **62,774-token** prompt took **169.8 s** (~370 tok/s).
- Generation itself is fine (~62 tok/s).

So the 30 s probe timeout is **too short** for the cold-start first completion on this
profile. The probe times out → `IsTransientWarmupProbeFailure` treats "timed out" as
transient → the launch loop keeps re-probing every ~30 s until the **launch deadline**
(`launchBudgetMs`, ~10–13 min) is hit. If the cold-start first completion is slower than
the deadline, the launch is reported as failed even though llama-server is healthy and
the model is loaded.

Why it's *intermittent*: cold-start first-completion time varies with GPU/RAM state
(after a Windows restart, disk cache is cold; if VRAM is busy from a prior server, the
settle step adds time). When the first completion lands under 30 s the launch succeeds
fast (2–4 s, see `llama_launch_telemetry.log` 10/15–10/17 entries: `prompt_tokens=10343`,
`latency=2166–5898 ms`). When it lands over 30 s (or the deadline is reached first), the
launch "fails".

## Why the daemon-port readiness check is NOT the problem
`IsModelActuallyReadyAsync` (line 4643) is just a lightweight `GET /v1/models` (200 =
ready, 503 = loading). It returned `False` for ~59 s on 11/6 because llama-server was
still loading the model (503). That part is correct and expected. The *actual* blocker is
the public-endpoint chat probe timing out at 30 s.

## The fix (prevent recurrence)
Make the public-endpoint probe timeout **context-aware** instead of a fixed 30 s, so a
slow-but-healthy cold start is not misreported as a failure. The codebase already has a
context-aware timeout helper: `GetContextAwareLocalRequestTimeoutMs` (line 5259):
`30000 + estimatedTokens*2 + maxCompletionTokens*100`, clamped to [60000, 300000].

Options (pick one; recommend A):
- **A. Raise the probe timeout for the launch path.** In
  `TimeShortLocalReplyThroughPublicEndpointAsync` (line 3047), pass a longer
  `requestTimeoutMs` (e.g. 120 s) for the *launch* probe specifically, OR compute it from
  the profile's known cold-start budget. The probe is non-streaming, so a longer timeout
  only costs wall-clock time on a genuinely slow cold start (which is already taking
  minutes); it does not affect warm launches (2–4 s).
- **B. Make the probe streaming.** A streaming probe gets its first byte as soon as the
  model starts generating (well under 30 s even on cold start), so the 30 s timeout
  becomes a *stall* detector rather than a *total-time* detector. This is the more
  correct fix but touches more code (`SendChatProbeAsync` builds a non-streaming payload).
- **C. Trust the daemon port.** Since `IsModelActuallyReadyAsync` (GET /v1/models = 200)
  already confirms the model is loaded, the public-endpoint probe could be relaxed to a
  lighter check (e.g. just confirm the gateway forwards to the daemon and gets *any*
  response, even a 503-turned-200) rather than a full chat completion.

## Verification (manual, after deploy)
1. Close app → `build-fluxmux.bat clean` → restart.
2. Launch the YaRN profile right after a Windows restart (coldest case).
3. Watch `launch_monitor.log`: the public-endpoint probe should either succeed within the
   new (longer) timeout, or the launch should report a *clear* "cold start is slow, still
   waiting" status rather than a hard failure.
4. Confirm `llama_launch_telemetry.log` shows `probe:done success=True` with a reasonable
   latency, and `ready total=` is well under the deadline.

## Files involved
- `Services/FluxMuxRuntimeService.cs`
  - line 244: `PublicEndpointReadyProbeTimeoutMs = 30000` (the too-short timeout)
  - line 3047: `TimeShortLocalReplyThroughPublicEndpointAsync` (launch probe entry)
  - line 3052: `TimeShortReadyReplyAsync` (passes the 30 s timeout)
  - line 3063: `CreateShortReadyProbeMessages` (the trivial "ready" prompt)
  - line 3987: launch loop calls `ProbePublicEndpointWithShortRetriesAsync`
  - line 4822: `SendChatProbeAsync` (non-streaming, 30 s `CancelAfter`)
  - line 5259: `GetContextAwareLocalRequestTimeoutMs` (existing context-aware helper)
  - line 5267: `IsTransientWarmupProbeFailure` (treats "timed out" as transient → retry loop)
