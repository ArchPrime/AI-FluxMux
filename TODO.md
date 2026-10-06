# AI-FluxMux — TODO

Working backlog for AI-FluxMux. Keep items short and actionable; move finished work into the Done section with the date it landed.

## New items

- **YaRN/Rope controls missing from profiles — restored (2026-10-06).** The YaRN feature (Extend context into RAM, Max context, Rope scaling, Rope scale) had ViewModel properties but no UI controls in the profile editor. Added an "Extend context (YaRN)" section to the profile "Detailed settings" panel (between "Fit and memory" and "Graphics card") with four controls + user-facing tooltips. **Deploy:** close app → `build-fluxmux.bat clean` → restart, then tick "Extend into RAM" and run AutoTune to confirm.
- **Tooltips audit for auto-compact + other controls (2026-10-06).** The auto-compact settings (Auto-compact, Trigger, Keep turns, Tool results, Pin user) already had user-facing tooltips — verified, no change needed. The new YaRN controls above also ship with tooltips. **Completed:** full sweep of the profile "Detailed settings" panel and the Port-rules panel. Added tooltips to 23 Port-rules NumericUpDown controls (Compact: Watermark %, Keep turns, Tool keep turns, Headroom, Preserved user chars; Omit: Keep recent results, Pin latest shell, Min chars to omit; Mill at omitted, Closed-loop lookback, Closed-loop mill ceiling, Rapid-churn Seconds/Consecutive, Observe-only, Max pictures, Hang: First-byte/Think/Max-think/Stall/Wait-longer/Decision, 503: Retries/Delay) and 2 Port-rules ComboBoxes (Client-app max tokens, Stop strings). All 114 interactive controls in MainWindow.axaml now have tooltips (the 5 remaining "missing" are sub-elements like ComboBox.ItemsPanel/ItemTemplate/CheckBox.Content, not user-facing controls).
- `Start here.txt` / README install docs pointed users at an exe that was not where they looked. **Docs fixed (2026-10-04):** README "Install (Release zip)" now gives the direct download link (skips the GitHub releases page, whose Assets panel can fail to load with "error while loading"), warns against the "Source code (zip)" download (which is what a user mistakenly grabs and finds no exe in), and says the exe is inside the unzipped `AI-FluxMux-0.2.2-beta` folder. `Start here.txt` now says it lives inside that unzipped folder and adds a note for anyone who opened a source-code download (`AI-FluxMux-main`) instead. **Still open:** (a) the GitHub *release body* for v0.2.2-beta still says "Double-click FluxMux.Avalonia.exe" with no folder note and no direct link — update it to match the README; (b) decide the zip layout — keep the top-level `AI-FluxMux-0.2.2-beta` folder (now documented) or zip the *contents* so the exe lands directly in the chosen folder; whichever is chosen, re-zip and re-upload the release asset so the published zip matches the docs.
- Harden AI-FluxMux against Windows kernel failures while writing. A kernel failure during a write just wiped all FluxMux settings including llama-server config and model profiles. This must cover writes to Cline and DeepSeek Harness configurations and any other config files. Likely approach: atomic write (write to temp, fsync, rename over target), keep a last-known-good backup before each write, and detect/repair corruption on startup.
  **Status (confirmed 2026-10-06): the main settings file is already protected.** `FluxMuxConfigService.Save` does atomic write (temp → `File.Move` overwrite) with lock-retry, and keeps a rolling `.prev` backup of the last good config; `Load` falls back to `.prev` when the live file is missing/corrupt. The main config (llama-server config + model profiles + all settings) is written only through `_configService.Save`, so the incident file is covered. `JsonFileQuarantine` renames corrupt files aside on read. Tests now cover: atomic write, lock-retry, no-stray-temp, `.prev` rolling backup, `.prev` restore on corrupt live config, and corrupt-file quarantine. **DeepSeek Harness config now hardened the same way (this session):** `DeepSeekHarnessSetup.MergeIntoSettingsFile` and `EnsureFluxMuxApiKeyEnvFile` now write atomically (new `WriteAllTextAtomic` helper: temp → `File.Move` overwrite with lock-retry, never throws) and keep a rolling `.prev` last-known-good backup (plus the existing `.bak` for the user-facing message) via a best-effort `TryCopyTextFile`. `MergeIntoSettingsFile` gained an optional `settingsPath` overload so tests target a temp folder instead of the live `~/.dsh/settings.yaml` (which the running harness holds open). 2 new tests: atomic write + `.prev` backup on update, and no-throw when the settings file is locked. Full suite 1564/1564. **Remaining (lower priority, not user settings):** a handful of telemetry/diagnostic files (`FluxMuxGatewayHost` recommend/reload/last-served/last-route paths, `FluxMuxRuntimeService` proxy-cloud-recommend, `CloudCircuitBreaker`, `DeepSeekHarnessWebHost` pid) still use raw `File.WriteAllText`; harden only if desired.
- Servers tab: when no local models folder has been selected, creating a local model fails silently. Prompt the user to go back and nominate the source folder if it is missing, or if the folder is set but no suitable model files (correct file extension) are found, confirm that with the user instead of failing silently.
- After a Cline update, the Cline context usage display no longer reflects the current model context value. Investigate and fix so the displayed context matches what the loaded model actually has.
- Review all operator-facing copy that FluxMux injects into client/endpoint dialogs (turn-failure messages, routing/switch dialogs, Health/Diagnostics sentences, `/v1/models` capability fields, and any other generated text). Several are inaccurate for the current endpoint or context: (a) turn-failure messages declare a situation "cannot be salvaged" when a model change + retry or a Cline compact-context action sometimes succeeds; (b) copy makes declarations about port-rules settings when port rules are disabled; (c) the port-rules help link is placed between the tickbox and the settings. Audit each sentence, verify it is true for the active endpoint/context, and fix the wording and layout.
- Make Cline propose context-aware operational interventions at appropriate times. The model is currently blind to its own context budget: Cline computes the used/total bar for the UI but never tells the model the number, so the model cannot propose compact / stop / re-read at the right moment. Approach: (1) FluxMux gateway already forwards every chat completion and sees `usage.prompt_tokens`; before forwarding the next request, inject a short system line `[Context budget: ~N/M used (P%). K tokens remain.]` so the model sees a real, live number (not an estimate). (2) Pair with the AGENTS.md "Local Model Context Awareness" block (already added to `C:\AI_Workbench\Workspace\AGENTS.md`) which gives the model the intervention thresholds: <60% work normally, 60-75% heads-up, 75-85% propose compact, >85% stop-and-summarise, after-compact re-read surgically, on-400 don't-retry-same-request. (3) Fix `maxInputTokens` in `ClineContextSync.WriteModelEntry` (line 346) to `contextWindow - maxTokens` so Cline's context bar is accurate and compacts earlier. Files: `FluxMuxGatewayHost.cs` (request-forwarding path, add usage tracking + injection), `ClineContextSync.cs` (maxInputTokens fix), `AGENTS.md` (already done). No Cline fork needed — logic lives in FluxMux which we control, so it survives Cline updates.
- Prevent Cline from repeatedly failing on invalid-JSON tool calls (typically reported as ~6 failed tool-call attempts). **Already exists and is wired in:** `Services/LocalToolCallHealing.cs` (non-stream repair, called from `FluxMuxGatewayHost.cs` line 1289) and `Services/LocalToolCallHealSession.cs` (stream repair, via `OpenAiStreamTelemetryProxy.cs` line 178). `CoerceArgumentsJson` already fixes: unquoted keys, trailing commas, single-quotes, ```-fenced JSON, XML `<parameter>` forms, missing outer `{}`, empty args, hallucinated tool names (dropped — only declared names promoted), and duplicate calls. It also converts text markup (`<tool_call>`, `<|tool_call`, `<function=`, `[TOOL_CALL`) into native `tool_calls`.
  **Why it still fails (hypotheses, unconfirmed — need one concrete failing payload to confirm):**
  1. Model emits tool calls in a *shape the regexes don't recognize* (e.g. `{"tool_calls":[...]}` or `{"name":...,"arguments":...}` as plain text/markup) → neither detected as text call nor native call → flows through as content with a broken JSON blob.
  2. Argument JSON is so malformed that every `CoerceArgumentsJson` heuristic fails → falls through to `WrapBareValue` → still not valid JSON for the parameter.
  3. `declaredTools` is empty for that request (heal layer silently skipped when `declaredTools.Count == 0`, see `LocalEndpointResponseSanitizer.cs` line 41 `shouldHeal = declaredTools is { Count: > 0 }`) → whole heal layer bypassed. Could happen if an overlay/compaction path strips the `tools` array before `CollectDeclaredToolNames` (line 1012) runs.
  **Still to find out (before making changes):**
  - Capture one real failing tool-call payload from the logs or Cline history to see which hypothesis is true.
  - Confirm whether the failures are on the stream path or non-stream path.
  - Check whether `declaredTools` is actually populated for the model/profile in use (is the `tools` array surviving to line 1012?).
  **Candidate fixes (pick after diagnosis):**
  - Add a last-resort valid-JSON fallback in `CoerceArgumentsJson`: if all heuristics fail, extract the largest JSON-looking substring or return `{}` with raw text stashed in a `_raw` field, so Cline always receives parseable arguments instead of a hard error.
  - Widen `MarkupNeedles` / add a "JSON-shaped text call" detector for `{"tool_calls":[...]}`-as-text and `{"name":...,"arguments":...}`-as-text cases.
  - Add diagnostic logging: log the raw broken payload whenever a non-healed local response contains tool-shaped content, and log a distinct event when `declaredTools` is empty but a local response has tool-shaped content (catches the bypass).
  **Files:** `Services/LocalToolCallHealing.cs` (CoerceArgumentsJson, MarkupNeedles), `Services/LocalToolCallHealSession.cs` (stream path), `Services/LocalEndpointResponseSanitizer.cs` (shouldHeal gate, line 41), `Services/FluxMuxGatewayHost.cs` (line 1012 CollectDeclaredToolNames, line 1289 non-stream heal).
  **Diagnostic prompt (copy-paste into a fresh session when ready to proceed):**
  ```
  Diagnose the invalid-JSON tool-call failures in FluxMux (the "~6 failed tool-call attempts" pattern).
  Do NOT make code changes in this pass — diagnosis only.

  Goal: confirm which of the 3 recorded hypotheses is the real cause, using one concrete failing payload.

  Steps:
  1. Capture a real failing tool-call payload. Check the FluxMux gateway log for `local_tool_heal` / `healed malformed` / invalid-JSON events, and/or Cline's own tool-call history for the last batch of ~6 failed attempts. Paste the raw tool-call text (the model's output) here.
  2. Classify it against the 3 hypotheses in the TODO item above:
     (a) unrecognized call shape (e.g. {"tool_calls":[...]} or {"name":...,"arguments":...} as plain text),
     (b) argument JSON too malformed for CoerceArgumentsJson to repair,
     (c) declaredTools was empty so the heal layer was bypassed.
  3. Confirm stream vs non-stream path for the failure (LocalToolCallHealSession.cs vs LocalToolCallHealing.cs).
  4. Verify declaredTools is actually populated: read FluxMuxGatewayHost.cs around line 1012 (CollectDeclaredToolNames) and check whether the `tools` array survives to that point for the model/profile in use (watch for overlay/compaction paths that strip it). Read LocalEndpointResponseSanitizer.cs line 41 (shouldHeal gate) to confirm the bypass condition.

  Output: one short conclusion — which hypothesis is confirmed, the exact code path, and the single smallest fix. Do not implement it yet; just report.
  ```

## Strategy: implementing the above under local-model context limits

The four "New items" above are large. A local model with a limited context window cannot hold the whole `FluxMuxRuntimeService.cs` (~7,700 lines) plus `MainViewModel.cs` (~15,000 lines) plus `FluxMuxGatewayHost.cs` plus the config files in one session. The strategy below keeps every change small enough to fit, and keeps the model running while the code is edited.

### 1. Keep the model running while FluxMux is edited

- **Do not run the model through FluxMux during the editing session.** Launch `llama-server` directly (see §2), so a FluxMux build failure, a bad edit, or a kernel write-failure cannot kill the model or wipe its config.
- FluxMux's gateway already detects an unmanaged llama-server on the daemon port (`_hasUnmanagedLocalEndpoint` / `IsLlamaEndpointReadyAsync` in `FluxMuxRuntimeService`). If FluxMux is later started, it adopts the running server instead of launching a second one. This is the "adopt" path — no change needed.
- While editing, point Cline (or any client) straight at `http://127.0.0.1:<llama-port>/v1` (or through the DeepSeek Harness if that is the active endpoint). Do not route through the FluxMux proxy unless the proxy is needed for the test.
- If the FluxMux proxy must be running for a test, start it **after** the model is up and confirm `Proxy_Runtime_State.json` shows `local_port` matching the running server.

### 2. Standalone launch (the "escape hatch")

- Add a small "Run standalone" command to FluxMux (or a one-off script) that:
  1. Takes the current model profile (model path, `--ctx-size`, `--n-gpu-layers`, vision projector, etc.)
  2. Builds the same `llama-server` argument list that `BuildLocalServerArgs()` already produces
  3. Writes a `.bat` (or `.ps1`) to a user-chosen folder containing the full command line
  4. Optionally writes a `Proxy_Runtime_State.json` stub so a later FluxMux start adopts the server
- This gives the operator a way to run a model with **zero** FluxMux involvement. The model files themselves are never touched by FluxMux writes — only the small JSON state files in `Logs\` and the config in `%AppData%\AI-FluxMux\` (or `.vscode\` in dev).
- For huge models (e.g. 70B+ GGUF), the launch script should set `--no-mmap` or `--mmap` explicitly and `--mlock` if RAM allows, to avoid Windows paging the model file.

### 3. Context-budget discipline for the editing session

Each item below is scoped so a single Cline turn (or a small number of turns) can hold the relevant code. The rule: **one item = one focused session = only the files that item touches are in context.**

| # | Item | Files in context (approx. lines) | Why it fits |
|---|------|----------------------------------|-------------|
| 1 | Atomic write hardening | `SafeFileWriter.cs` (new, ~80 lines), `FluxMuxConfigService.cs` (~60 lines around `Save`/`Load`), `FluxMuxRuntimeService.cs` (the ~15 `File.WriteAllText` call sites, each 3–5 lines), `FluxMuxGatewayHost.cs` (4 call sites), `DeepSeekHarnessSetup.cs` (3 call sites), `PortForwardingRulesStore.cs`, `ModelProfilePack.cs`, `EndpointAdapterPack.cs`, `JsonMergeEndpointAdapter.cs` | The new `SafeFileWriter` is small; each call site is a 1–2 line swap. No need to hold the whole `FluxMuxRuntimeService` — only the specific `File.WriteAllText` lines. |
| 2 | Servers-tab silent failure | `MainViewModel.cs` `CreateNewLocalModelProfile()` (~40 lines), `ResolveLocalModelPath()` (~30 lines), `RefreshLocalProfilesFromModelDirectory()` (~50 lines) | Self-contained in `MainViewModel`; no other files needed. |
| 3 | Cline context display | `ClineContextSync.cs` (the read/refresh path, ~100 lines), `MainViewModel.cs` (the property that displays it, ~20 lines) | Small surface; the fix is in how the context value is fetched/cached. |
| 4 | Operator-facing copy audit | `FluxMuxGatewayHost.cs` (the 400/switch/keep-local dialog strings, ~200 lines), `FluxMuxRuntimeService.cs` (Health/Diagnostics sentence builders, ~150 lines), `MainWindow.axaml` (port-rules help link layout, ~30 lines) | String-only changes; no logic. Can be done in 2–3 passes: (a) turn-failure wording, (b) port-rules declarations, (c) help-link layout. |

### 4. Ordering (why this sequence)

1. **Item 1 (atomic writes) first.** It is the highest-risk item — a kernel failure mid-edit can wipe the config again. Once `SafeFileWriter` is in place, every subsequent session is safer. It also creates the `SafeFileWriter` utility that items 2–4 do not need but that protects the config while the other items are being developed.
2. **Item 2 (Servers-tab UX) second.** Small, self-contained, low risk. Good "warm-up" after the bigger item 1.
3. **Item 4 (copy audit) third.** String-only changes; no logic risk. Can be interleaved with item 3 if context allows.
4. **Item 3 (Cline context display) last.** It requires understanding the Cline update event flow, which is the most "investigative" item. By this point the config is safe (item 1) and the model is running standalone (§2), so there is no pressure.

### 5. Per-session checklist (keep context small)

For each item, at the start of the Cline session:

1. **State the item number and the files to touch** (from the table above). Do not paste the whole file — reference line ranges.
2. **Read only the relevant line ranges** (use `start_line`/`end_line`). For item 1, read each `File.WriteAllText` site individually, not the whole `FluxMuxRuntimeService.cs`.
3. **Make the edit, build, and verify** before moving to the next call site. Do not batch all 15 `File.WriteAllText` swaps into one turn — do 3–4 per turn, build between.
4. **After each turn, record what was done** in the `## Done` section of this file (or a scratch note) so the next session can pick up without re-deriving state.
5. **If the model's context is getting tight** (Cline shows >70% used), compact the context *before* the next tool call, or start a fresh session with a short state summary.

### 6. What NOT to put in the model's context

- The full `FluxMuxRuntimeService.cs` (7,700+ lines) — never.
- The full `MainViewModel.cs` (15,000+ lines) — never.
- The full `FluxMuxGatewayHost.cs` — never.
- Binary files, logs, or `.gguf` metadata.
- The `UNSLOTH_PATHWAY_SPEC.md` and `COMMISSIONIONING_MODE_SPEC.md` files (deferred; not needed for the four items above).


## Carried over from deferred specs

These are the deferred slices already written up in the project. They are not in the UI.

### From `UNSLOTH_PATHWAY_SPEC.md` — "Steal later (one slice at a time)"

- MTP draft reserve in `LocalVramFootprintEstimate` — leave extra leftover for the second KV cache when spec type is `draft-mtp` (or the GGUF looks like MTP). Advice-only for new / AutoTune rows; do not rewrite the saved 5090 profile. Measure first.
- Health / Validate: llama-server too old for MTP — one line when help lacks `--spec-type draft-mtp`. Links only; no in-app extract.
- Optional Validate: `--spec-draft-n-max` 3 or 4 vs the default 2. Watch tok/s and whether Health stays GPU-only at the saved Context. Do not copy Unsloth's GPU preset of 6.
- Tool-call heal on Port — landed as `LocalToolCallHealing` / `LocalToolCallHealSession` (verify it is fully settled before closing this item).

### From `COMMISSIONING_MODE_SPEC.md` — Quick Commissioning Mode

- Quick Commissioning Mode (deferred). Revisit only if there is a clear product need for an auto-qualification pass that new users will actually run. See the spec for the full design.

## Done

<!-- Move completed items here with the date they landed. -->

- 2026-10-04 — In-app "Update llama-server now" button. The update tool previously only *checked* for a newer llama.cpp nightly and opened download links; it never performed the update. Added `Services/LlamaServerUpdateService.cs` which does the real in-place update: guards against a running llama-server, downloads the matching Windows zip + matching CUDA DLLs zip (the "extra DLLs"), backs up the current folder, extracts, merges the CUDA runtime DLLs, and restores any DLL the new zip did not ship. Wired into the Servers tab as an "Update llama-server now" button (shown when a matching zip is found), with a confirm dialog and live progress text. `scripts/Update-LlamaServer.ps1` now ships with the app (csproj Content include). 5 new tests in `LlamaServerUpdateServiceTests.cs` cover the guards, the swap-in-place + backup, and CUDA-DLL restore. DeepSeek Harness update remains check-only by design (AI-FluxMux does not install/upgrade dsh).
- 