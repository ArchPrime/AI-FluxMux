# AI-FluxMux — Regression Audit (10/25)

> Generated after the user reported "many things regressed" following the rebuild.
> This is a consolidated list of every feature/fix added in the last ~6 hours (10/24 22:45 → 10/25 04:20),
> so we can triage what's broken without re-specifying each bug.

## Source integrity check (DONE)

- Current source files are all **newer and larger** than the 10/24 22:45 backup → NOT overwritten with old content.
- `ContextFillStressProbe.cs` is byte-identical to the backup (0 diff) → confirms no wholesale replacement.
- The large diff counts in `MainViewModel.cs` (32k lines), `MainWindow.axaml` (4.7k), `FluxMuxGatewayHost.cs` (5.8k) are the **new features below**, not corruption.
- My only edit: added `<vm:StringToVisibilityConverter x:Key="StringToVisibility" />` to `MainWindow.axaml` line 26 (fixes the launch crash).

## Features/fixes added in the last 6 hours (triage list)

### 10/25 (today)
| # | Feature | Files | Status |
|---|---------|-------|--------|
| 1 | **StringToVisibility resource fix** (launch crash) | `MainWindow.axaml` | ✅ Fixed, deployed |
| 2 | **GPU process list: PerformanceCounter warm-up** | `FluxMuxRuntimeService.cs` | ⚠️ May not populate (pre-existing issue noted) |
| 3 | **Hail Mary: pulse animation + removed "queued" text + GPU scan cooldown** | `MainWindow.axaml`, `MainWindow.axaml.cs`, `MainViewModel.cs` | ⚠️ Triage |
| 4 | **Hail Mary middle-cut: token-driven (retroactive compaction)** | `HailMaryCompaction.cs`, `FluxMuxGatewayHost.cs` | ⚠️ Triage |
| 5 | **Rejoin-aware proactive compact** | `FluxMuxGatewayHost.cs` | ⚠️ Triage |
| 6 | **Hail Mary button: no-visual-feedback fix + outcome text** | `MainWindow.axaml`, `MainViewModel.cs` | ⚠️ Triage |
| 7 | **Hail Mary: praying-hands glyph (Wingdings 0x5B)** | `MainWindow.axaml` | ⚠️ Triage |

### 10/24 (evening)
| # | Feature | Files | Status |
|---|---------|-------|--------|
| 8 | **TTFT graph headroom + bar value clearing (StringToVisibilityConverter)** | `TelemetryGraphViewModel.cs`, `MainWindow.axaml`, `MainWindow.axaml.cs`, `StringToVisibilityConverter.cs` | ⚠️ Triage |
| 9 | **Hail Mary queued feedback + GPU & VRAM Settings tab + reduced tick marks** | `MainWindow.axaml`, `MainViewModel.cs`, `MainWindow.axaml.cs` | ⚠️ Triage |
| 10 | **VRAM spill dialog wording fix** | `LocalReloadRouting.cs` | ⚠️ Triage |
| 11 | **2 pre-existing test failures fixed (wording)** | `DisplayAdapterLabelingTests.cs`, `LocalRoutingDecisionTests.cs` | ✅ Tests |
| 12 | **Removed log scale from telemetry graphs + Y-axis/bar alignment** | `TelemetryGraphViewModel.cs`, `MainViewModel.cs`, `MainWindow.axaml`, `MainWindow.axaml.cs`, `TelemetryGraphViewModelTests.cs` | ⚠️ Triage |
| 13 | **DeepSeek Harness: semver-aware AppVersionComparer + broken alpha install fix** | `FluxMux.Updates.Core/AppVersionComparer.cs`, `FluxMuxRuntimeService.cs` | ⚠️ Triage |
| 14 | **Launch timing: median-based warmup budget + countdown estimate** | `FluxMuxRuntimeService.cs` | ⚠️ Triage |

### 10/23
| # | Feature | Files | Status |
|---|---------|-------|--------|
| 15 | **Stress test: zero-level guard + auto-load verification** | `MainViewModel.cs` | ⚠️ Triage |
| 16 | **Stress test graph fixes** | `TelemetryGraphViewModel.cs`, `MainWindow.axaml` | ⚠️ Triage |

## How to use this

Tell me which # (or a few) are visibly broken, and I'll trace each to the code and fix it.
No need to re-specify the symptom in detail — just point at the row(s).
