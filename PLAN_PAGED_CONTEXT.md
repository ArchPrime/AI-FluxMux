# PLAN — Paged Context + VIBE_STATE.md (UNIFIED context strategy, HANDOFF, resumable)

> Recorded 11/9. **This file is the source of truth for resuming.** If a chat's context
> overflows mid-implementation, a new chat reads this file + `BRIEFING.md` + `STATUS.md` +
> `PLAN_CONTEXT_FIXES.md` and continues from the first unchecked step. Each step has a
> "DONE?" marker — flip it to `[x]` and note the build/test state when the step is verified.
>
> **Do NOT deploy** (close app → `build-fluxmux.bat clean` → restart) — that is the user's
> action. Record deploy state in `STATUS.md` at the end.

## What this is (and what it is NOT)

This is a **unified** context strategy: it ASSIMILATES the existing context-fixes pipeline
(forward-prune, conventional compaction, Hail Mary, measured tokens, prefix-cache guard,
client-style gating) and adds the two genuinely-new capabilities on top. It does NOT add a
parallel pipeline.

The two new capabilities:
1. **`VIBE_STATE.md`** — a persistent, capped, model-writable "ground-truth" state file
   (Current Goal / Working-Do-Not-Touch / Active Bugs / Dead Ends) re-injected every turn as
   a prefix-safe, byte-stable system suffix. Survives context clears AND app restarts.
2. **Paged context** — when `ForwardPrune` truncates an oversized tool body, SPILL the full
   body to `.fluxmux/swap/` first and leave a `swap_id` pointer, so the model can page back in
   via a `read_swap_log` tool (exposed on a new `/mcp` route). Turns the existing LOSSY
   truncation into RETRIEVABLE paging.

### Explicitly NOT doing (per the 11/9 discussion)
- No fixed 8k/24k/64k presets. Budgets are a **fraction of the profile's real
  `local_context`**, using the **measured** token count from `LocalPromptTokenCounter`
  (never the `len/3.5` estimate — we already have the real count).
- No `get_repo_map` tool (Cline/Harness already ship file/grep/read tools).
- No new client-style detection. Reuse the existing `UserClientReinflatesContext` /
  `CompactionControlsEnabled` / `GetForwardPruneEnabled` machinery.
- No heavy dependencies (no vector DB, no embedding model, no background summarizer LLM,
  no compiled native binaries). Std-lib + existing `HttpListener` only.

## Why this is worth doing (the value, verified)

The gateway is **stateless** — it holds no conversation history. The "context" between turns
is (a) the client's re-sent message array and (b) llama-server's KV/prefix cache.
`STATUS.md` (11/4) documents the core pain: any prompt modification → `cache_prompt=false`
→ **full KV recompute (4–6 min instead of 5–10 s)**.

- **YaRN** buys raw window (393K) but the moment you fill it, compaction fires and you pay
  the recompute. Long vibe sessions hit this repeatedly.
- **Paged context** keeps the **active prefix small and stable** → fewer compactions → fewer
  `cache_prompt=false` → the 5-second prefill stays 5 seconds. It DELAYS/REDUCES the YaRN
  compaction events that cause the slow-prefill pain. The two are **complementary**, and
  paged context is the better of the two for long vibe sessions because it attacks the actual
  cost (compact → cache-invalidation → recompute) rather than just enlarging the window.

## VERIFIED endpoint-gating table (the rule that ties it together)

Verified in live code 11/9. For a **Cline-like** client (`UserClientReinflatesContext == true`),
`MainViewModel.ApplyUserClientStyle()` (line ~1173) **forces `LocalForwardPruneEnabled = false`**
(line ~1195) along with auto-compact, variant auto-compress, and port-rules compact. The
gateway gate at `FluxMuxGatewayHost.cs` line ~896 only runs `ForwardPrune.PruneTurn` when
`GetForwardPruneEnabled == true`, so **forward-prune (and everything assimilated into it) is a
NO-OP for Cline.** This is by design: Cline reinflates the full history each turn and owns its
own context; any FluxMux prompt modification would break the prefix cache.

| Capability | Cline-like (reinflates) | FluxMux-managed |
|---|---|---|
| Forward-prune (existing) | ❌ off | ✅ on |
| **Paged context** (in forward-prune) | ❌ off **automatically** (inherits the gate) | ✅ on (opt-in per profile) |
| **`VIBE_STATE.md`** (prefix-safe system suffix) | ✅ on (byte-stable; one recompute only when file changes) | ✅ on |
| **`/mcp` route** (separate HTTP route, not prompt mod) | ✅ available; `read_swap_log` → `{ok:false}` (no swap written) | ✅ available |
| **`git_checkpoint`** (no prompt mod) | ✅ (opt-in) | ✅ (opt-in) |

**Key consequence:** because paging is assimilated INTO `ForwardPrune`, it **inherits the
existing Cline gate for free** — no new detection, no special-casing. `VIBE_STATE.md` is the
only new capability that delivers value on the Cline path, and that is exactly where the model
has the least FluxMux-side memory.

## Existing substrate (assimilate, do not duplicate)

| Stage | File / location | Role |
|---|---|---|
| Per-turn waste prune | `Services/ForwardPrune.cs` `PruneTurn` (gateway ~line 896–902) | dedupe + collapse tool loops + **truncate oversized tool results (currently LOSSY)** |
| Conventional compaction | `Services/LocalHistoryCompaction.cs` / `TryApplyForwardCompact` (~line 904–907) | summarize older turns when window nearly full |
| Emergency salvage | `Services/HailMaryCompaction.cs` `Salvage` | one-off, when compaction bails |
| Measured tokens | `Services/LocalPromptTokenCounter.cs` (~line 678) | ground-truth count from llama-server `/input_tokens` |
| Cache guard | `Services/LocalPrefixCachePolicy.cs` `ApplyAfterCompact` | `cache_prompt=false` after any prefix change |
| Client-style gate | `MainViewModel.CompactionControlsEnabled` (line ~791) + `GetUserClientReinflatesContext` | disables all prompt-modifying stages for Cline-like |
| Per-profile toggles | `LocalForwardPruneEnabled`, auto-compact mode (`FluxMuxRuntimeService.GetForwardPruneEnabled` line ~936) | read live each turn, mid-chat, no reload |

## Unified per-turn pipeline (single, ordered)

```
[0] VIBE_STATE.md  → prefix-safe stable block appended to system msg   (NEW, always on)
[1] LocalSessionArtifactPolicy   (existing; skipped for Cline-like, gateway ~line 621)
[2] LocalToolResultClearing      (existing, ~line 664)
[3] ForwardPrune.PruneTurn       (existing, ~line 896)  ← PAGING ASSIMILATED HERE
[4] LocalHistoryCompaction       (existing, ~line 904)
[5] HailMaryCompaction           (existing, emergency only)
[6] LocalPrefixCachePolicy       (existing)
```

## Design principles (locked 11/9 — these govern every implementation decision)

Four objectives, all satisfied by the same shape:
1. **Maximum performance & resilience** — no hidden indirection; graceful degradation; the
   Cline gate is inherited, not re-implemented.
2. **Minimum brittleness under future change** — explicit, co-located integration; a single
   owner for per-profile config; nothing hidden behind a router.
3. **Modularity / multiplatform / breaking up god objects** (long-standing background
   objective) — the new capability is a **self-contained module the god objects reference,
   not code that swells them.** When `FluxMuxRuntimeService` (10.6k) or `MainViewModel`
   (24.8k) are eventually split, the context-engine module walks away cleanly.
4. **No duplication** — reuse the existing per-profile bool read and atomic-write; do NOT
   copy them. Refactor the existing one to be shared.

### The modular architecture: `Services/ContextEngine/` (a folder = an extractable unit)

```
Services/ContextEngine/
  IContextEngine.cs          // the public contract (the seam the god objects depend on)
  ContextEngine.cs           // orchestrates the three; implements IContextEngine
  VibeState.cs               // state-file logic (self-contained)
  ContextSwapStore.cs        // swap spill/read/purge (self-contained)
  McpControlPlane.cs         // /mcp tool dispatch (self-contained)
  ContextEngineConfig.cs     // reads its OWN two flags via an injected config accessor
  IContextIo.cs              // I/O seam (file/dir/atomic-write) — multiplatform + testable
```

**Multiplatform / no-brittleness rules for the module:**
1. **No scattered `File.`/`Directory.` calls.** All I/O goes through one `IContextIo` seam
   (wraps the existing atomic-write helper). A future non-Windows target or a unit test
   substitutes it. This is the single biggest multiplatform win and it is cheap.
2. **No reference to `MainViewModel`, `FluxMuxRuntimeService`, or `FluxMuxGatewayHost`
   types.** The module depends only on `System.Text.Json` + `IContextIo` + (for
   `git_checkpoint`) an `IGitRunner` seam. It can be unit-tested and moved without dragging
   the god objects.
3. **Config via injected delegates, NOT a hard call into `FluxMuxRuntimeService`.** The
   gateway already hands the host a bunch of `Func<...>` delegates (`GetForwardPruneEnabled`
   etc., `FluxMuxRuntimeService` line ~839). The module gets its two flags the same way.
   - The module does not depend on `FluxMuxRuntimeService` (breaks the coupling).
   - When `FluxMuxRuntimeService` is split, the delegates are just re-pointed. Zero module
     change.
   - This is the DRY win done the modular way: the per-profile bool read still lives in ONE
     place (`FluxMuxRuntimeService.GetProfileBool`), but the module receives it as a value,
     not a dependency.

### DRY: one shared bool getter, one shared atomic write (no copies)
- **`FluxMuxRuntimeService`:** add ONE private helper
  `GetProfileBool(string model, string variant, string key, bool fallback)` containing the
  exact body of today's `GetForwardPruneEnabled` (line ~936), parameterized on `key` and
  `fallback`. Then:
  - `GetForwardPruneEnabled` → `GetProfileBool(model, variant, "LocalForwardPruneEnabled", true)`
    (refactored onto the helper — **removes** the existing one-off, no behavior change).
  - `GetVibeStateEnabled` → `GetProfileBool(model, variant, "LocalVibeStateEnabled", true)`.
  - `GetContextPagingEnabled` → `GetProfileBool(model, variant, "LocalContextPagingEnabled", false)`.
  - Expose the two new ones as the injected delegates the module consumes.
- **Atomic write:** the module's `IContextIo` reuses the existing
  `FluxMuxConfigService.WriteAllTextResilient` (line ~98, temp + `File.Move` + retry). Make
  it `public static` (one-word change) OR extract to a tiny shared `Services/AtomicFile.cs`
  and have `FluxMuxConfigService` call that too. Either way: **one atomic-write
  implementation**, not three.
- **Config save/load for the two new keys:** two lines in the *existing* per-profile save
  method (the same place `LocalForwardPruneEnabled` is persisted), not new save methods.

### God objects become thin call sites (smaller responsibilities, not bigger)
- **`FluxMuxGatewayHost` (4651):** one line to construct/inject `IContextEngine`; one line
  `InjectVibeState` at stage 0; one line routing `POST /mcp`; one line at startup
  (`EnsureExists` + `PurgeOlderThan`). It *uses* the module; it does not *contain* it.
- **`FluxMuxRuntimeService` (10.6k):** gains the shared `GetProfileBool` (refactor) + exposes
  two delegates. That is all.
- **`MainViewModel` (24.8k) + `MainWindow.axaml` (4.3k):** two checkbox bindings that write
  the two config keys. The module never knows the UI exists.

### How this serves every objective at once
- **Performance/resilience:** identical to the minimal approach — paging still inside
  `ForwardPrune`'s gate, `VIBE_STATE` byte-stable, everything degrades gracefully.
- **Brittleness:** the module is a clean seam. Pipeline changes touch the gateway's 4
  one-line call sites; config changes touch `GetProfileBool`; UI changes touch the two
  bindings. Nothing hidden.
- **Modularity/multiplatform:** the module is extractable, I/O-seamed, dependency-light.
- **No duplication:** one `GetProfileBool`, one atomic-write, one I/O seam — all *used*,
  never re-implemented.

### The one honest tradeoff
Slightly more upfront work than the minimal approach (the `IContextEngine` contract + the
`IContextIo` seam + the injected delegates). But it is the ONLY shape that satisfies all four
objectives at once, and it makes the NEXT god-object split cheaper. Given modularity is a
long-standing objective, pay it now — it is cheap now and expensive to retrofit.

### Assimilation 1 — Paging upgrades the lossy truncate (stage 3)
`ForwardPrune` currently **discards** the elided tool body (leaves only a stub). Change: when
it truncates a body over the threshold, first **spill the full body to `.fluxmux/swap/`** and
leave the existing stub, now with a pointer:
`[Paged Out: <n> lines → swap_id="<id>". Call read_swap_log to inspect.]`
- **No new stage.** A small change inside the existing truncate path (spill before stub).
- **Same gate as forward-prune** (`LocalForwardPruneEnabled` + `!reinflates`). For Cline-like
  clients the whole stage is already off, so paging is automatically off too.
- New service `ContextSwapStore` (spill/read/purge) is *used by* `ForwardPrune`, not a
  parallel pipeline.
- `read_swap_log` is exposed via the `/mcp` route so the model can page back in.

### Assimilation 2 — `VIBE_STATE.md` as a prefix-safe system suffix (stage 0)
- New service `VibeState` (self-healing, capped at 2000 chars, atomic write).
- Injected as a **stable suffix to the system message**, appended *after* the existing system
  text so it never disturbs the client's own prefix.
- **Prefix-safe:** the block only changes when the file changes → one recompute only on an
  actual update (the same cost Cline's own compact already pays). This is what makes it safe
  for Cline-like clients, where every other prompt-modifying stage is off.
- `update_vibe_state` via `/mcp` lets the model maintain it.

### Assimilation 3 — `/mcp` control plane (new route, not a pipeline stage)
- `POST /mcp` branch in `FluxMuxGatewayHost.HandleContextAsync` (~line 454), before the 404.
- Tools: `update_vibe_state`, `read_swap_log`, `git_checkpoint` (opt-in).
- **Always available** (separate route, not prompt modification). `read_swap_log` returns
  `{ok:false}` on the Cline-like path (no swap store is written there).

## Budgeting (correction to the original brief)
No fixed presets. The paging spill threshold is a **fraction of the profile's real
`local_context`** (e.g. a tool body > 5% of the window **and** the active prefix trending
toward the compact watermark), using the **measured** count from `LocalPromptTokenCounter`.
Goal: keep the active prefix under the 85% compact watermark so `TryApplyForwardCompact` →
`cache_prompt=false` fires far less often. The threshold is a single constant, not a preset
table.

## New files (the `Services/ContextEngine/` module — one extractable unit)

All new logic lives HERE. The god objects only reference this module.

1. `Services/ContextEngine/IContextEngine.cs` — the public contract (the seam). Methods:
   `InjectVibeState(JsonObject payload, bool enabled)`, `HandleMcpAsync(HttpListenerContext)`,
   `EnsureExists()`, `PurgeOnStartup()`. This is the ONLY type the god objects depend on.
2. `Services/ContextEngine/ContextEngine.cs` — implements `IContextEngine`; orchestrates
   `VibeState` + `ContextSwapStore` + `McpControlPlane`. Thin; no business logic of its own.
3. `Services/ContextEngine/VibeState.cs` — capped, self-healing state file.
   - `VibeStatePath` resolved next to the config (`FluxMuxConfigPaths`), lives with the project.
   - `EnsureExists()` — creates a default template (Current Goal / Working — Do Not Touch /
     Active Bugs / Dead Ends) if missing.
   - `ReadCapped(int maxChars = 2000)` — returns state, hard-capped; malformed/locked → `""`
     + warning (graceful degradation, never throws).
   - `UpdateSection(string section, string content)` — rewrites only the named section,
     preserves the rest; writes via `IContextIo` (the shared atomic-write).
4. `Services/ContextEngine/ContextSwapStore.cs` — the paging store under `.fluxmux/swap/`.
   - `EnsureDir()`, `Spill(content, turnIndex) → (swapId, path)` (atomic),
     `Read(swapId, startLine, limit) → string` (paginated), `PurgeOlderThan(days = 7)`.
   - All ops never throw; a failed write → no-op so the turn passes through unmodified.
   - Writes via `IContextIo`.
5. `Services/ContextEngine/McpControlPlane.cs` — handles `POST /mcp` (minimal named-tool
   JSON, NOT full MCP protocol — fits "zero heavy dependencies").
   - `update_vibe_state(section, content)` → `VibeState.UpdateSection`
   - `read_swap_log(swap_id, start_line, limit)` → `ContextSwapStore.Read`
   - `git_checkpoint(label)` → opt-in only; safe WIP commit only inside a git repo with dirty
     changes; otherwise no-op. Runs via an `IGitRunner` seam (no hard `Process` dep).
   - Any error → structured `{ok:false, error}` (never a 500 that breaks the chat).
6. `Services/ContextEngine/ContextEngineConfig.cs` — reads the module's two flags
   (`LocalVibeStateEnabled`, `LocalContextPagingEnabled`) via **injected delegates**
   (`Func<string,string,bool?>`), NOT a hard call into `FluxMuxRuntimeService`.
7. `Services/ContextEngine/IContextIo.cs` — the I/O seam (file read/write/atomic-move/dir
   create). Default impl wraps the existing `FluxMuxConfigService.WriteAllTextResilient`.
   Substitutable for tests and future non-Windows targets.

## Changed files (thin call sites — smaller responsibilities, not bigger)

1. `Services/ForwardPrune.cs` — **the core assimilation**: spill-before-stub in the truncate
   path. When paging is enabled and a body is truncated, call `ContextSwapStore.Spill` first
   and append the `swap_id` pointer to the stub. ~3–4 lines; no new methods.
2. `Services/FluxMuxGatewayHost.cs` — ~6 lines total:
   - one line to construct/inject `IContextEngine` (pass the two config delegates);
   - one line `InjectVibeState(payload, enabled)` at stage 0;
   - one line routing `POST /mcp` → `IContextEngine.HandleMcpAsync` (before the 404, ~line 454);
   - one line at startup: `EnsureExists()` + `PurgeOnStartup()`;
   - one diagnostic log line `[context-engine] active | vibe_state=on | swap_dir=.fluxmux/swap`.
3. `Services/FluxMuxRuntimeService.cs` — the DRY refactor + two delegates:
   - add ONE private `GetProfileBool(model, variant, key, fallback)` (body of today's
     `GetForwardPruneEnabled`, line ~936, parameterized);
   - refactor `GetForwardPruneEnabled` onto it (no behavior change);
   - add `GetVibeStateEnabled` / `GetContextPagingEnabled` as one-liners over `GetProfileBool`;
   - expose both as the injected delegates the module consumes (alongside the existing
     `host.GetForwardPruneEnabled = ...` at line ~839).
   - two lines in the existing per-profile save method to persist the two new keys.
4. `Views/MainWindow.axaml` + `ViewModels/MainViewModel.cs` — two checkboxes in the Quick
   Select "Context management" section (next to the existing Forward-Prune checkbox at
   `MainWindow.axaml` line ~408): "Vibe state file" and "Paged context (swap)". Follow the
   existing `LocalForwardPruneEnabled` binding + save/load pattern (MainViewModel lines
   ~7842, ~20977, ~21580, ~23000, ~23317). **Grey out "Paged context" with
   `CompactionControlsEnabled`** (it is off for Cline-like). `VIBE_STATE` checkbox stays
   enabled for all client styles. The module never knows the UI exists.


## IMPLEMENTATION STEPS (do in this order; mark [x] when verified)

### Step 0 — DRY refactor (prerequisite, no behavior change) `[x]`
- `FluxMuxRuntimeService`: add ONE private `GetProfileBool(model, variant, key, fallback)`
  (body of today's `GetForwardPruneEnabled`, line ~936, parameterized on `key`/`fallback`).
- Refactor `GetForwardPruneEnabled` → `GetProfileBool(model, variant, "LocalForwardPruneEnabled", true)`.
- Make `FluxMuxConfigService.WriteAllTextResilient` (line ~98) `public static` (or extract to
  `Services/AtomicFile.cs` and point `FluxMuxConfigService` at it).
- Verify: build clean; full test suite unchanged (this is a pure refactor — no test may change
  behavior).
- **DONE 11/9:** extracted `Services/AtomicFile.cs` (public static `WriteAllTextResilient`);
  `FluxMuxConfigService.WriteAllTextResilient` now a thin wrapper delegating to it. Added
  `GetProfileBool(model, variant, key, fallback)` to `FluxMuxRuntimeService` and refactored
  `GetForwardPruneEnabled` onto it (behavior identical — `fallback` replaces the hardcoded
  `true`). Build: 0 errors. Full suite: **1763/1763 passed**.

### Step 1 — `ContextEngine` module skeleton + `VibeState` (NEW) `[x]`
- Create `Services/ContextEngine/` with `IContextEngine.cs`, `IContextIo.cs` (default impl
  wraps the shared atomic-write), `ContextEngine.cs` (thin), and `VibeState.cs` (capped,
  self-healing, never throws, writes via `IContextIo`).
- Verify: build clean; `VibeStateTests` (Step 5).
- **DONE 11/9:** 5 files created — `IContextEngine.cs` (+ `ContextEngineOptions` injected-delegate
  config), `IContextIo.cs` (+ `FileSystemContextIo` default wrapping `AtomicFile`),
  `ContextEngine.cs` (concrete impl; `PageContext` delegates to `ForwardPrune.PruneTurn`),
  `VibeState.cs` (record + JSON), `VibeStateMarkdown.cs` (render/parse/reinjection; made `public`
  to match the codebase's testable-helper convention). 20 unit tests in
  `FluxMux.Avalonia.Tests/ContextEngineTests.cs` (in-memory `FakeContextIo`). Build 0 errors;
  full suite **1783/1783 passed**.

### Step 2 — `VIBE_STATE` injection + config key + UI `[x]`
- `FluxMuxRuntimeService`: `GetVibeStateEnabled` = one-liner over `GetProfileBool`
  (`"LocalVibeStateEnabled"`, fallback true); exposed as an injected delegate alongside
  `host.GetForwardPruneEnabled` (line ~839); `_vibeStatePath` (next to config) added; two lines in
  the existing per-profile save method.
- `ContextEngineConfig`: read `LocalVibeStateEnabled` via the injected delegate.
- `FluxMuxGatewayHost`: construct/inject `IContextEngine`; `InjectVibeState(payload, model,
  variant)` at stage 0 (prefix-safe system suffix; missing/locked → skip silently). Byte-stable
  across unchanged turns.
- `MainWindow.axaml` + `MainViewModel`: "Keep session vibe (VIBE_STATE.md)" checkbox (gated by
  `CompactionControlsEnabled`); `LocalVibeStateEnabled` property + 4 config save/load sites.
- Verify: build clean; gateway test that the block is byte-stable when the file is unchanged.
- **DONE 11/9:** all wiring in place. `GetVibeStateEnabled` one-liner + `_vibeStatePath` added to
  the runtime service; gateway host now carries `GetVibeStateEnabled`, `ContextEngine`, and
  `VibeStatePath` and calls `InjectVibeState` at stage 0 (before forward-prune, only for non-probe
  hot-context turns; appends to the existing system message or inserts one at the front if none).
  `LocalVibeStateEnabled` property + 4 config save/load sites added to `MainViewModel`;
  "Keep session vibe (VIBE_STATE.md)" checkbox added to `MainWindow.axaml`. Build 0 errors; full
  suite **1783/1783 passed**. Not deployed (user's action).

### Step 3 — `ContextSwapStore` + `McpControlPlane` (NEW) `[x]`
- `ContextSwapStore.cs`: atomic spill, paginated read, purge-by-age, never throws, writes via
  `IContextIo`.
- `McpControlPlane.cs`: `update_vibe_state`, `read_swap_log`, `git_checkpoint` (via
  `IGitRunner` seam); any error → `{ok:false, error}`.
- Verify: build clean; `ContextSwapStoreTests` + `McpControlPlaneTests` (Step 5).
- **DONE 11/9:** 3 files created — `ContextSwapStore.cs` (atomic spill via `IContextIo`, idempotent
  FNV-1a `swap_id`, paginated `ReadPage`, `PurgeOlderThan`, `EnsureExists`, never throws, 512KB
  per-entry cap), `IGitRunner.cs` (+ `GitRunner` default shelling out to `git add -A` / `git commit`),
  `McpControlPlane.cs` (3 tools: `update_vibe_state` merges with existing state, `read_swap_log`
  returns a page, `git_checkpoint` via `IGitRunner`; any error → `{ok:false, error}`, never throws).
  17 unit tests in `ContextEngineTests.cs` (`ContextSwapStoreTests` + `McpControlPlaneTests` using
  in-memory `FakeContextIo` + `FakeGit`). Build 0 errors; full suite **1800/1800 passed**. Not
  deployed (user's action).

### Step 4 — Paging assimilation into `ForwardPrune` + `/mcp` route + config key + UI `[x]`
- `ForwardPrune.cs`: spill-before-stub in the truncate path (gated on
  `GetContextPagingEnabled == true`; paging-off = today's lossy stub byte-for-byte).
- `FluxMuxGatewayHost`: one line routing `POST /mcp` → `IContextEngine.HandleMcpAsync`
  (before the 404, ~line 454); startup `EnsureExists()` + `PurgeOnStartup()`; diagnostic line.
- `FluxMuxRuntimeService`: `GetContextPagingEnabled` = one-liner over `GetProfileBool`
  (`"LocalContextPagingEnabled"`, fallback false); expose as injected delegate; two lines in
  the save method.
- `MainWindow.axaml` + `MainViewModel`: "Paged context (swap)" checkbox, greyed out with
  `CompactionControlsEnabled` (off for Cline-like).
- Verify: build clean; `ForwardPruneTests` +1 (pointer + body on disk; off = today's stub);
  `McpControlPlaneTests` (Step 5).
- **DONE 11/9:** `ForwardPrune.PruneTurn` + `TruncateOversizedToolResults` now accept an optional
  `ContextSwapStore?` — when non-null (paging on), the truncate stage spills each oversized body
  to the store and leaves a `swap_id` pointer (lossless); when null (paging off), the lossy
  head+tail stub is used byte-for-byte. `ContextEngine.PageContext` + `IContextEngine.PageContext`
  updated to pass the swap store. Gateway host now carries `SwapStore` + `McpControlPlane` fields
  and routes `POST /mcp` → `HandleMcpRequestAsync` (before the 404). Runtime service constructs
  the swap store (`context_swap.json` next to config) + MCP control plane and wires them into the
  gateway; `GetContextPagingEnabled` one-liner added; `LocalContextPagingEnabled` property + 4
  config save/load sites in `MainViewModel`; "Page context (lossless spill)" checkbox in
  `MainWindow.axaml`. Build 0 errors; full suite **1800/1800 passed**. Not deployed (user's action).

### Step 5 — Tests `[x]`
- `FluxMux.Avalonia.Tests/VibeStateTests.cs` (new): create-if-missing, capped read,
  section-update preserves others, malformed/locked → empty + no throw. Use a fake `IContextIo`.
- `FluxMux.Avalonia.Tests/ContextSwapStoreTests.cs` (new): stable id, paginated read,
  purge-by-age, write-failure → no-op. Use a fake `IContextIo`.
- `FluxMux.Avalonia.Tests/ForwardPruneTests.cs` +2: `TruncateOversizedToolResults_with_swap_store_spills_body_and_leaves_pointer`
  verifies the swap_id pointer + body on disk; `TruncateOversizedToolResults_without_swap_store_uses_lossy_stub`
  verifies the lossy stub byte-for-byte.
- `FluxMux.Avalonia.Tests/McpControlPlaneTests.cs` (new): each tool happy path + error shape;
  `read_swap_log` no-ops on Cline-like; `git_checkpoint` no-ops outside a repo.
- Gateway: `VIBE_STATE` block byte-stable across unchanged turns (prefix-cache safe);
  toggle-off = today's behavior.
- Run targeted filters first, then the full suite (no `--filter`).
- **DONE 11/9:** `VibeStateTests` (Step 1, 20 tests), `ContextSwapStoreTests` + `McpControlPlaneTests`
  (Step 3, 17 tests), `ForwardPruneTests` +2 (Step 5). Full suite **1802/1802 passed**. Not deployed (user's action).

### Step 6 — STATUS.md `[ ]`
- Record: what landed (Steps 0–5), test count, "compiled/tested in artifacts\testrun\, NOT
  deployed," and the deploy handoff (close app → `build-fluxmux.bat clean` → restart).
- Note the two new per-profile keys (`LocalVibeStateEnabled` default on,
  `LocalContextPagingEnabled` default off) + Quick Select controls.
- Note the verified endpoint-gating table (paging off for Cline-like by construction).
- Note the new `Services/ContextEngine/` module (extractable unit) + the `GetProfileBool`
  DRY refactor.


## Rollout
- **Phase 1:** `VIBE_STATE.md` (default on) — lowest risk, highest value, endpoint-agnostic.
- **Phase 2:** Paging + `/mcp` (default off, opt-in per profile).
- **Phase 3:** `git_checkpoint` (opt-in).

## Build / test protocol (from AGENTS.md — do NOT deviate)
```powershell
cd c:\AI_Workbench\Workspace\FluxMux.Avalonia
dotnet build FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --nologo -v q `
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\
dotnet test FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --no-build --nologo `
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\ `
  --filter "FullyQualifiedName~<TestClassName>"
```
- A "Build FAILED" that is ONLY the `FluxMux.Updates.Core.dll` copy step (locked by the
  running app) is NOT a code error — check for `error CS####`.
- Do NOT use the `AvoidLockedAppHost` escape hatch for new changes (stale prebuilt DLL).
- Do NOT kill `FluxMux.Avalonia.exe` or run `build-fluxmux.bat`.

## What NOT to do
- Do NOT add a parallel context pipeline — assimilate into the existing stages.
- Do NOT use fixed 8k/24k/64k presets or the `len/3.5` estimate (use the measured count + a
  fraction of `local_context`).
- Do NOT add `get_repo_map` (clients already ship file/grep tools).
- Do NOT add new client-style detection — reuse `UserClientReinflatesContext` /
  `CompactionControlsEnabled` / `GetForwardPruneEnabled`.
- Do NOT let paging run for Cline-like clients (it inherits the forward-prune gate; verify
  this holds).
- Do NOT let any new path break `/v1/chat/completions` — every path degrades gracefully.
- Do NOT deploy; hand the user the close/build/restart steps.
- Do NOT rename C# namespaces, JSON keys in `fluxmux_config.json` (add NEW keys, don't rename
  existing ones), the protocol string "FluxMux", or `EndpointAdapters` identifiers.
- **Do NOT bolt new logic into the god objects.** All new behavior lives in
  `Services/ContextEngine/`; the god objects get thin call sites only.
- **Do NOT duplicate the per-profile bool read or the atomic write.** Use the shared
  `GetProfileBool` and the shared atomic-write helper; never copy them.
- **Do NOT make the module depend on `MainViewModel` / `FluxMuxRuntimeService` /
  `FluxMuxGatewayHost` types.** Config via injected delegates; I/O via `IContextIo`; git via
  `IGitRunner`. The module must be extractable and unit-testable in isolation.
- **Do NOT scatter `File.`/`Directory.` calls** in the module — route all I/O through
  `IContextIo` (multiplatform + testable).

## Open decisions (locked 11/9)
1. **Paging default:** OFF (locked — rollout behind the toggle).
2. **Swap location:** `.fluxmux/swap/` in the workspace (locked — project-local).
3. **`/mcp` transport:** minimal named-tool JSON endpoint (locked — fits "zero heavy deps").
4. **Architecture:** self-contained `Services/ContextEngine/` module behind `IContextEngine`,
   I/O-seamed (`IContextIo`), config via injected delegates (locked — serves modularity /
   multiplatform / god-object breakup + no-duplication + min brittleness).
5. **DRY:** one shared `GetProfileBool` (refactor `GetForwardPruneEnabled` onto it) + one
   shared atomic-write (locked — no copies of existing helpers).

## Progress log (append as you go)
- 11/9: plan recorded. No code changed yet. Verified endpoint-gating table in live code
  (forward-prune is a no-op for Cline-like; paging inherits that gate by construction).
- 11/9: locked the modular architecture (`Services/ContextEngine/` + `IContextEngine` +
  `IContextIo` + injected-delegate config) and the DRY refactor (`GetProfileBool` + shared
  atomic-write) after the user prioritized max performance/resilience, min brittleness,
  modularity/multiplatform (god-object breakup), and no code duplication.
- 11/9: **Step 0 complete.** `Services/AtomicFile.cs` created (shared atomic-write);
  `FluxMuxConfigService` delegates to it; `FluxMuxRuntimeService.GetProfileBool` added and
  `GetForwardPruneEnabled` refactored onto it. Build 0 errors; full suite 1763/1763 passed.
  **DEPLOYED 11/9** (user: close app → `build-fluxmux.bat clean` → restart).
- 11/9: **Step 1 complete.** `Services/ContextEngine/` module skeleton created:
  `IContextEngine.cs` (+ `ContextEngineOptions`), `IContextIo.cs` (+ `FileSystemContextIo`),
  `ContextEngine.cs`, `VibeState.cs`, `VibeStateMarkdown.cs`. 20 unit tests in
  `ContextEngineTests.cs` (in-memory `FakeContextIo`). Build 0 errors; full suite 1783/1783 passed.
  Not deployed (user's action).
- 11/9: **Step 2 complete.** VIBE_STATE wired into the live app: `GetVibeStateEnabled` one-liner +
  `_vibeStatePath` in the runtime service; gateway host carries `GetVibeStateEnabled`/`ContextEngine`/
  `VibeStatePath` and calls `InjectVibeState` at stage 0 (before forward-prune, non-probe hot-context
  turns only; appends to the system message or inserts one at the front); `LocalVibeStateEnabled`
  property + 4 config save/load sites in `MainViewModel`; "Keep session vibe (VIBE_STATE.md)" checkbox
  in `MainWindow.axaml`. Build 0 errors; full suite 1783/1783 passed. Not deployed (user's action).
- 11/9: **Step 3 complete.** `ContextSwapStore` + `McpControlPlane` created: `ContextSwapStore.cs`
  (atomic spill, idempotent FNV-1a swap_id, paginated ReadPage, PurgeOlderThan, EnsureExists, never
  throws, 512KB cap), `IGitRunner.cs` (+ `GitRunner` default), `McpControlPlane.cs` (3 tools:
  update_vibe_state, read_swap_log, git_checkpoint; any error → {ok:false, error}). 17 unit tests
  (ContextSwapStoreTests + McpControlPlaneTests). Build 0 errors; full suite 1800/1800 passed. Not
  deployed (user's action).
- 11/9: **Step 4 complete.** Paging assimilated into `ForwardPrune` + `/mcp` route + config key + UI:
  `ForwardPrune.PruneTurn`/`TruncateOversizedToolResults` accept optional `ContextSwapStore?` (spill
  when non-null, lossy stub when null); `ContextEngine.PageContext` + `IContextEngine.PageContext`
  updated; gateway routes `POST /mcp` → `HandleMcpRequestAsync` (before 404); runtime service
  constructs swap store + MCP control plane and wires them into the gateway; `GetContextPagingEnabled`
  one-liner + `LocalContextPagingEnabled` property + 4 config save/load sites + "Page context
  (lossless spill)" checkbox. Build 0 errors; full suite 1800/1800 passed. Not deployed (user's action).
- 11/9: **Step 5 complete.** `ForwardPruneTests` +2: `TruncateOversizedToolResults_with_swap_store_spills_body_and_leaves_pointer`
  (verifies swap_id pointer + body on disk via `ReadPage`) and `TruncateOversizedToolResults_without_swap_store_uses_lossy_stub`
  (verifies the lossy head+tail stub byte-for-byte). Full suite **1802/1802 passed**. Not deployed (user's action).





