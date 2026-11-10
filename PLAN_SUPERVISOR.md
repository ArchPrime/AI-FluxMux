# FluxMuxSupervisor — Plan & Design Document

**Created:** 2026-11-08
**Status:** PLANNED (not yet implemented)
**Version:** 0.3.4 (current) → 0.3.5 (target, post-supervisor)
**Backup:** `c:\AI_Workbench\Workspace\backups\FluxMux.Avalonia_0.3.4_2026-11-08_0058_source` (2,400 files, 4.3 GB)

## 1. Problem Statement

FluxMux has accumulated **111 fix entries** in `STATUS.md`, with the same 5 themes
recurring:

| Theme | Mentions | Pattern |
|---|---|---|
| Prefill | 300 | estimate too high → too low → too high for RAM-spill → too short for RAM-spill → stale → oscillation → 897s abort |
| Launch/probe | 241 | probe false-positive → timeout too short → 30s early-return misfire → daemon up but public endpoint down |
| Context/compact/reload | 211 | compact disabled for Cline → re-enabled → reload offer stale → not shown → shown too late |
| RAM/VRAM/spill | 213 | RAM-spill polluting warm speed → dedicated RAM-spill model → estimate too high → too low |
| Watchdog/hang/stall | 281 | hang dialog never appears → oscillates → fires too early → fires too late |

**Root cause:** The monitoring/prediction/recommendation logic is scattered across three
god objects (`MainViewModel.cs` 21,862 lines, `FluxMuxRuntimeService.cs` 9,724 lines,
`FluxMuxGatewayHost.cs` 4,243 lines). Each fix is a *local* patch that can't see the
*global* picture, so fixing one scenario breaks another.

## 2. Solution: FluxMuxSupervisor

A new `FluxMuxSupervisor.cs` entity (~1,800 lines) with a **single responsibility**:
*monitor the model's health, predict failures, and recommend interventions.*

It is **not** a new god object. It is a focused service that:
1. **Reads** the existing state files (no new I/O paths).
2. **Runs** the existing routing tests (`PromptFillsLocalContext`, `DecideOverflowFallback`) — it calls them, doesn't reimplement.
3. **Writes** one new file: `Supervisor_Health.json` (the correlated, client-aware, routing-aware verdict).
4. **Exposes** observable state that `MainViewModel.cs` binds to (replacing inline logic).
5. **Owns** the RAM watchdog (moved from `FluxMuxRuntimeService.cs`).
6. **Owns** the prefill estimate lifecycle (moved from `FluxMuxRuntimeService.cs` + `MainViewModel.cs`).
7. **Owns** the reload-recommend read/write (moved from `FluxMuxRuntimeService.cs`).

## 3. The ModelScenario Record

The key structural change: making the *scenario* explicit instead of implicit.

```csharp
public sealed record ModelScenario(
    bool IsClineLike,          // client reinflates context (UserClientStyle starts "Cline-like")
    bool IsRamSpill,           // prompt > VRAM context (isRamSpill check)
    bool IsLongLoadProfile,    // YaRN / extended context / GPU+CPU offload
    bool IsColdStart,          // first prefill after launch (no warm cache)
    bool IsPortRulesOn,        // PortForwardingRules.Enabled
    bool IsRoutingOn,          // routing_enabled in Proxy_Runtime_State.json
    bool IsLocalReady,         // local_phase == "ready"
    bool IsCloudReady,         // cloud_phase == "ready"
    string? CloudCapacity,     // cloud_routing_capacity (Normal/Hold/Low)
    int HotContext,            // local_context (YaRN-extended if active)
    int VramContext,           // local_vram_context
    int PromptTokens,          // from Local_Context_Usage.json or measured
    int PredictedPrefillSeconds, // from prefill_perf_*.json + scenario
    double RamUsagePercent,    // from WorkingSet64 / TotalAvailableMemoryBytes
    bool CloudBreakerOpen      // from Proxy_Cloud_Breaker.json
);
```

Every intervention becomes a **function of the scenario**, not a patch to a specific `if`.

## 4. Supervisor_Health.json Schema

```json
{
  "scenario": {
    "isClineLike": true,
    "isRamSpill": true,
    "isLongLoadProfile": true,
    "isColdStart": false,
    "isPortRulesOn": true,
    "isRoutingOn": false,
    "isLocalReady": true,
    "isCloudReady": false,
    "cloudCapacity": "Normal",
    "hotContext": 393216,
    "vramContext": 163840,
    "promptTokens": 203312,
    "predictedPrefillSeconds": 412,
    "ramUsagePercent": 65.2,
    "cloudBreakerOpen": false
  },
  "fillPercent": 52,
  "verdict": "healthy",
  "recommendations": [],
  "risks": {
    "staleEstimateRisk": false,
    "watchdogOscillationRisk": false,
    "ramThrashRisk": false,
    "contextOverflowRisk": false
  },
  "portRules": {
    "enabled": true,
    "compactEnabled": false,
    "reason": "client reinflates"
  },

## 6. Phase Plan

### Phase 1: Read-Only Correlator (ZERO RISK)

**Goal:** Build `FluxMuxSupervisor.cs` as a read-only correlator. No behavior change.

**What it does:**
- Reads all existing state files on a 5s timer (same interval as RAM watchdog).
- Computes `ModelScenario` from the state files.
- Runs `PromptFillsLocalContext` and `DecideOverflowFallback` (existing functions, called not reimplemented).
- Writes `Supervisor_Health.json` to `c:\AI_Workbench\Logs\`.
- Exposes `ObservableObject` properties for `MainViewModel.cs` to bind to (optional, can be deferred).

**Files created:**
- `Services/FluxMuxSupervisor.cs` (~800 lines in Phase 1)
- `FluxMux.Avalonia.Tests/FluxMuxSupervisorTests.cs` (~200 lines)

**Files modified:**
- `Services/FluxMuxRuntimeService.cs` — add a `StartSupervisor()` / `StopSupervisor()` call (thin, ~20 lines).
- `FluxMux.Avalonia.csproj` — no changes (new file is auto-included).

**Verification:**
- Build in separate output dir (`artifacts\testrun\`).
- Run `FluxMuxSupervisorTests` (scenario computation, verdict logic).
- Run full test suite (no regressions).
- Manually verify `Supervisor_Health.json` is written and matches the existing state files.

**Deploy:** User closes app → `build-fluxmux.bat clean` → restart. Verify `Supervisor_Health.json` appears in `c:\AI_Workbench\Logs\`.

### Phase 2: Prefill Prediction (ONE INTERVENTION)

**Goal:** Move the prefill estimate lifecycle to the supervisor. The god object's method becomes a thin proxy.

**What moves:**
- `SetPrefillEstimate` / `ClearPrefillEstimate` from `FluxMuxRuntimeService.cs` → `FluxMuxSupervisor.cs`.
- `StartPrefillTimer` / `EasePrefillProgress` / estimate growth from `MainViewModel.cs` → `FluxMuxSupervisor.cs` (or a `PrefillTelemetry` service).
- The `isRamSpill` / `isLongLoadProfile` check that selects the right speed model → `FluxMuxSupervisor.cs`.

**What stays:**
- The UI binding in `MainViewModel.cs` (the progress bar, the ETA text) — it just reads from the supervisor's observable state.

**Verification:**
- Existing prefill tests still pass.
- New tests: `PredictPrefillSeconds_ClineLike_RamSpill_YaRN_ColdStart_ReturnsColdSpeed`.
- Manual: start a long YaRN chat, verify the prefill estimate uses the cold speed model.

### Phase 3: Reload-Recommend + Hang Watchdog

**Goal:** Move the reload-recommend read/write and the hang watchdog to the supervisor.

**What moves:**
- `ReadLocalReloadRecommend` / `AnswerLocalReloadRecommend` / `ClearLocalReloadRecommend` from `FluxMuxRuntimeService.cs` → `FluxMuxSupervisor.cs`.
- `WatchLocalStreamHangAsync` from `FluxMuxGatewayHost.cs` → `FluxMuxSupervisor.cs` (or stays, it's tightly coupled to the gateway).
- The oscillation guard → `FluxMuxSupervisor.cs`.

**Verification:**
- Existing reload-recommend tests still pass.
- New tests: reload offer is proactive (surfaced before the prefill, not after the 897s abort).


## 7. Test Strategy

Each phase is **independently deployable** and **independently testable**.

**Unit tests (new):**
- `FluxMuxSupervisorTests.cs` — scenario computation, verdict logic, prefill prediction.
- One test per scenario combination (Cline-like × RAM-spill × YaRN × cold-start × port-rules × routing).

**Regression tests (existing):**
- All 1,740 existing tests must pass after each phase.
- The prefill, reload-recommend, hang-watchdog, and RAM-watchdog tests are the critical regression gates.

**Manual verification:**
- Phase 1: `Supervisor_Health.json` appears in `c:\AI_Workbench\Logs\` and matches the existing state files.
- Phase 2: prefill estimate uses the correct speed model (cold for RAM-spill, warm for cache-hit).
- Phase 3: reload offer is proactive (surfaced before the prefill).
- Phase 4: RAM-thrash warning at 70% (not just kill at 85%).

## 8. Constraints & Invariants

- **NEVER kill `FluxMux.Avalonia.exe`** — it serves this chat.
- **NEVER run `build-fluxmux.bat`** — it kills the app.
- **Test in a separate output dir** (`artifacts\testrun\`).
- **Do NOT use the `AvoidLockedAppHost` escape hatch** for new changes.
- **No hardcoded pixel dimensions** for layout.
- **Do not rename** C# namespaces, JSON keys in `fluxmux_config.json`, protocol string `"FluxMux"`, or `EndpointAdapters` identifiers.
- **The supervisor is in-process** (a timer/loop inside `FluxMux.Avalonia`), NOT a separate OS daemon.
- **Phase 1 is read-only** — it can't touch the running app or the build.

## 9. Expected Benefits

| Benefit | Type | Anchored to | Expected impact |
|---|---|---|---|
| Prefill time prediction | Performance | `prefill_perf` bimodal speed (125K warm / 1.8K cold) | Accurate ETA up front; no frozen bar |
| Proactive reload-to-bigger-context | Performance + Reliability | `context_fill_history` 83% fill → HTTP 400 | 897s abort *prevented*, not recovered |
| Launch-time prediction | Performance | `launch_monitor` 28/192 probe failures (~15%) | "loading, ~15s" instead of frozen "warming" |
| RAM-thrash prediction | Reliability | `StartRamWatchdog` 85% kill threshold | Pre-thrash warning at 70%, not post-thrash kill |
| Circuit-breaker awareness | Reliability | `Proxy_Cloud_Breaker` open/closed state | Cloud-consent suppressed when breaker open |
| Unified post-mortem | Reliability | 8 separate log/state files | Diagnosis: 1 file instead of 8 |
| Breaks ad-hoc-fix treadmill | Structural | 111 fix entries, 5 recurring themes | Scenarios explicit, interactions visible, fixes testable |

## 10. Backup & Fallback

**Backup created:** `c:\AI_Workbench\Workspace\backups\FluxMux.Avalonia_0.3.4_2026-11-08_0058_source`
(2,400 files, 4.3 GB — full source tree, excludes `bin\`, `obj\`, `artifacts\`, `.git\`)

**Fallback procedure:**
1. Close the running app (user action).
2. Delete the current source tree: `Remove-Item -Recurse -Force c:\AI_Workbench\Workspace\FluxMux.Avalonia\*`
3. Copy the backup back: `Copy-Item -Recurse -Force c:\AI_Workbench\Workspace\backups\FluxMux.Avalonia_0.3.4_2026-11-08_0058_source\* c:\AI_Workbench\Workspace\FluxMux.Avalonia\`
4. Rebuild: `build-fluxmux.bat clean`
5. Restart the app.

## 11. Open Questions

1. **Should the supervisor write `Supervisor_Health.json` on every tick (5s) or only when the verdict changes?**
   - Recommendation: only when the verdict changes (avoids log spam). The `updatedUtc` field tracks the last change.

2. **Should the supervisor expose observable properties for `MainViewModel.cs` to bind to, or should `MainViewModel.cs` read `Supervisor_Health.json` directly?**
   - Recommendation: observable properties (cleaner, no file I/O in the UI thread). The supervisor writes the JSON file for external tools (diagnosis, this chat) AND exposes observable properties for the UI.

3. **Should the prefill prediction be a pure function (`PredictPrefillSeconds(ModelScenario, int promptTokens)`) or should it read the `prefill_perf_*.json` file directly?**
   - Recommendation: pure function that takes the speed model as a parameter. The supervisor reads the JSON file and passes the speed model to the function. This makes the function testable.

4. **Should the supervisor replace the existing `Proxy_Local_Reload_Recommend.json` and `Proxy_Cloud_Recommend.json` files, or supplement them?**
   - Recommendation: supplement. The existing files are written by the gateway (reactive). The supervisor's `Supervisor_Health.json` is the *proactive* correlate. Both coexist; the supervisor reads the existing files and writes its own.

## 12. Next Steps

1. ✅ Plan documented (this file).
2. ✅ Backup created (see §10).
3. ⬜ Implement Phase 1 (read-only correlator).
4. ⬜ Test Phase 1 (separate output dir, full suite).
5. ⬜ Deploy Phase 1 (user action).
6. ⬜ Verify Phase 1 (manual: `Supervisor_Health.json` appears).
7. ⬜ Implement Phase 2 (prefill prediction).
8. ⬜ ... (continue through Phase 5).

### Phase 4: RAM Watchdog + Launch Monitor

**Goal:** Move the RAM watchdog and launch monitor to the supervisor.

**What moves:**
- `StartRamWatchdog` / `StopRamWatchdog` / `OnRamWatchdogTick` from `FluxMuxRuntimeService.cs` → `FluxMuxSupervisor.cs`.
- `LogLaunchMonitor` / `IsModelActuallyReadyAsync` from `FluxMuxRuntimeService.cs` → `FluxMuxSupervisor.cs` (or stays).

**Verification:**
- Existing RAM watchdog tests still pass.
- New tests: pre-thrash warning at 70% RAM (not just post-thrash kill at 85%).

### Phase 5: Delete Scattered `if`s

**Goal:** Once all interventions are in the supervisor, delete the scattered `if`s from the god objects.

**What happens:**
- `MainViewModel.cs` drops from ~21,862 to ~20,000 lines (pure UI binding + user commands).
- `FluxMuxRuntimeService.cs` drops from ~9,724 to ~8,500 lines (pure launch + process management).
- `FluxMuxGatewayHost.cs` stays ~4,243 lines (gateway + routing + compact).
- `FluxMuxSupervisor.cs` grows to ~1,800 lines (monitoring + prediction + recommendation + watchdog).

**Verification:**
- Full test suite passes.
- Manual: all existing scenarios (Cline-like, RAM-spill, YaRN cold start, port-rules-off) still work correctly.

  "breakerState": {},
  "lastServed": "Local/Qwen3.8-27B",
  "updatedUtc": "2026-11-08T00:00:00Z"
}
```

**Verdict values:** `healthy` | `watch` | `reload-recommended` | `compact-recommended` |
`cloud-consent-recommended` | `ram-thrash-risk` | `launch-stalled` | `context-overflow`

## 5. Client-Aware + Routing-Aware Routing Table

| Client type | Local can't take turn | Routing on + cloud ready | Supervisor's proactive move |
|---|---|---|---|
| **Other** (non-reinflating) | forward-compact (existing) | offer CloudConsent (existing) | pre-surface compact *or* cloud choice |
| **Cline-like** (reinflates) | compact **disabled** | offer CloudConsent (existing) | pre-surface **reload-to-bigger-context**, then cloud if reload pool exhausted |

When `routing_enabled` is **off** (current state): no cloud offers, only local reload/compact.
When `routing_enabled` is **on**: full cloud-consent + breaker + capacity protocol inherited.

