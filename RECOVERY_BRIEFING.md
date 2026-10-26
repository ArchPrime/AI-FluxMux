# AI-FluxMux — 17/10 Source-Reversion Recovery Briefing (paste into a new session)

## Context (read first)
A previous chat overwrote the `FluxMux.Avalonia\` source tree with a compiled OLD git version, reverting work done between 16/10 and the reversion event (17/10). The goal: get the source to compile to match the **intact pre-reversion build** (now the "master"), and separately implement the intended next updates.

**Read `c:\AI_Workbench\Workspace\STATUS.md` (top section "⚠️ ACTIVE: 17/10 source-reversion recovery") for the full, current state before doing anything.**

## Key rule (ALWAYS)
- **NEVER kill/close the running `FluxMux.Avalonia.exe`** — it serves this chat. The user closes it manually.
- Build to a **separate output dir** to avoid the file lock:
  `dotnet build FluxMux.Avalonia.csproj -c Debug -p:BaseOutputPath=artifacts\recovery\ -p:BaseIntermediateOutputPath=artifacts\recovery\obj\`
- A "Build FAILED" that is ONLY the `FluxMux.Updates.Core.dll` copy step (locked by the app) is NOT a code error — check for `error CS####`.

## The three reference points
| Path | What | Use |
|------|------|-----|
| `FluxMux.Avalonia\master\` | Intact pre-reversion **binary** (DLL hash `6BE46482...`). Self-contained (exe + Avalonia DLLs + Help.html). | What the user runs (master mode). Read-only. |
| `FluxMux.Avalonia\artifacts\decompile_prereversion\` | **Decompiled source** of the master (per-type folders). | Authoritative reference for what code/XAML *should* be. Read-only. NOT directly compilable (ILSpy artifacts: `<>z__*` types, `#define DEBUG`, inlined source-gen). Use to GUIDE edits into the real source. |
| `FluxMux.Avalonia\` (source tree) | Editable source (partially recovered). | Where development happens. |

## Damage assessment (verified)
- **ViewModel (`MainViewModel.cs`): INTACT.** All commands (`[RelayCommand]`), properties, launch-countdown monitor, stress-test logic present. (An earlier "lost 88KB" claim was WRONG — that was decompiler overhead, not lost logic.)
- **XAML (`MainWindow.axaml`): lost exactly two blocks, both in the graph/telemetry region:**
  1. ✅ **Stress-test block** — RECOVERED & VERIFIED this session (ComboBox + "Stress test" + "Cancel stress test" buttons, re-inserted between `GpuVramButton` and `GpuVramPopup` ~L3230). 0-error build.
  2. ❌ **`GraphTerminalGrid` terminal split** — STILL MISSING from source. This is the remaining gap.

## The one remaining task: recover `GraphTerminalGrid`
- **What it is:** a 3-column `Grid x:Name="GraphTerminalGrid"` (col 0 = graph, col 1 = sash `GridSplitter` theme `DiagnosticsSashTheme`, col 2 = terminal `TextBox x:Name="DiagnosticTerminal"` class `diagnosticTerminal`). ~100 XAML lines.
- **Current source state:** graph-only layout — two stacked graphs (`TelemetryGraph` + `TelemetryGraphThroughput`), no terminal, no sash, no `GraphTerminalGrid` wrapper.
- **Reference (decompiled master):** `artifacts\decompile_prereversion\FluxMux.Avalonia.Views\MainWindow.cs` lines **~21048-21520** (maps to original XAML lines 3000-3099). The `GraphTerminalGrid` named Grid is at decompiled L21073; the sash/terminal follow.
- **Current source insertion point:** `Views\MainWindow.axaml` **L2931** (the `<Border Grid.Row="1">` that starts the graph-only region). The graph content (L2931 onward) needs to be wrapped in col 0 of `GraphTerminalGrid`, with the sash added in col 1 and the terminal panel in col 2.
- **Code-behind already expects it (null-safe, no crash):** `Views\MainWindow.axaml.cs:249` `FindControl<Grid>("GraphTerminalGrid")` and `ApplyGraphTerminalDefaultSplit()` (sets 1/3 graph / 2/3 terminal). Also `DiagnosticTerminal` TextBox + `DiagnosticTerminalText` property-change handling (~L9668).
- **Approach:** Reconstruct the XAML by translating the decompiled C# block-by-block (graph wrapper → graph panel → sash → terminal panel). Build-verify after. This is a large, deeply-nested edit — do it in a fresh session with full context.
- **Verify:** rebuild to `artifacts\recovery\`, confirm 0 errors AND `GraphTerminalGrid` + `DiagnosticTerminal` present in the rebuilt DLL.

## After the source is fully recovered + intended updates implemented
- Set `USE_MASTER=0` in `FluxMux.Avalonia\start-ai-fluxmux.cmd` to resume normal source-build mode.
- Deploy (USER action only): close app → `build-fluxmux.bat clean` → restart.

## What the user will provide (separate work)
The specific functional/operational changes that were intended when the reversion happened (user is gathering these from other chat histories). Implement each into the source tree, using `decompile_prereversion\` as reference where a change touches lost code.

## Safety artifacts
- `ViewModels\MainViewModel.cs.reverted-backup` — pre-recovery source (reference only).
- `artifacts\recovery\Debug\net10.0\` — 0-error build with the stress-test XAML fix.
- `master\` — the intact master build (do not modify).
