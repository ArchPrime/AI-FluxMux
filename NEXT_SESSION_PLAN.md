# Next session: testing and tweaking plan (written 24 Nov 2026, ~2:55 am)

Nothing below is committed yet. `git status` shows today's uncommitted work (compaction cache,
repeat/failed-call notes, re-read pointer, Compact flag split, VIBE fixes, Help/tooltip edits).
Commit it first once the operator is happy, or review it with `git diff`.

## Already verified live (through Port 5001, real model, build of 2:49 am)

| Behaviour | Result |
| --- | --- |
| No forced Compact after Launch (`compact_now` split) | OK: 0 force-compacts after relaunch; `forward_compact` stays true |
| Repeat ×2 with empty result | OK: note + first attempt only; model changed approach (`ls -la`, `find`) |
| 3 failed tool calls | OK: note sent; model switched to a recursive `*.py` search |
| Identical re-read → pointer | OK: pointer used; model still answered correctly from the earlier copy (60) |

Test scripts: scratchpad `port_tests.py` (scenarios `repeat failed dedup`) and `long_session.py`
(`python -I long_session.py <start_turns> <count>`). They send Cline-shaped OpenAI tool-call requests
to Port 5001. Avoid ≥ 3 identical repeats in a test: that opens the pause dialog and the request waits.

## DONE (24 Nov, ~10 am): Omit broke the prompt cache every turn

Measured (8 consecutive turns, 70–77 tool results, ~41K-token prompt, Context 163840):
llama-server prompt similarity **0.13**, **~39K tokens reread every turn, ~8.5 s each**.
Cause: `LocalToolResultClearing.ClearOlderResults` keeps the newest 16 results and stubs the rest,
so each new result moves the stub point one step and changes the prompt early on.
It also fires when the chat is far from full (here 25 % of Context; in the Cline task, 5 %).

Fixed: `ClearOlderResultsWhenFilling` (half-Context gate + steps of 8). Live re-run: similarity 0.95,
~2.2K tokens / ~0.8 s reread on turns between steps. Original proposal:
1. Only stub when the prompt passes ~50 % of the loaded Context (`local_context`).
2. Stub in steps, like `TryCompactPayloadStable`: round the keep-from index down to a multiple of
   a step (e.g. 8), so the stub point stays put for several turns.
3. Add a `PromptPrefixStabilityTests`-style replay test for Omit, then re-run `long_session.py 70 8`
   and expect similarity ≈ 1.0 with only the newest result read on most turns.

## DONE: Rapid-churn retired (operator: retire it if it no longer adds value)

With the cache working, a tool turn takes ~1 s, so **Rapid-churn** (4 turns in a row < 3 s once the
pack is deep, `RapidChurnSeconds` / `RapidChurnConsecutive`) paused the long-session test at 9:59.
Options: (a) only count turns that are also repeats/failures/observe-only; (b) raise the threshold;
(c) retire it now that the repeat and failed-call notes exist. Needs the operator's choice.

## Then

- **Automatic compaction not yet seen live.** In the test, Omit kept the prompt at ~41K, so the
  per-profile trigger (80 %) was never reached. After the Omit fix, run `long_session.py` with enough
  turns to pass 80 % of Context (~135K tokens: start around 150 turns, or launch a 65536 profile) and
  check: "auto-compact: … shortened older turns", cut moves once per ~8 turns, cache reused between.
- **VIBE_STATE conversation stability on compacted turns**: confirm no "switched VIBE_STATE" lines
  between consecutive turns of one chat once compaction runs (fix is in; not yet seen live).
- **Real Cline task** with client style **Other**: watch the log for the notes, pointer lines and
  compaction, and llama-server similarity (healthy ≈ 1.0).
- **Client style box**: decide whether to replace it with a Cline-only "who shortens" option
  (discussed; operator has not decided).
- **Failed-call note**: check it fires in a real Cline run (format taken from Cline 4.1.27
  `extension.js`: tool result = JSON of `[{query,result,error,success}]`).
- Known, unrelated: two tests fail only when built with `--artifacts-path` (they look for source
  files above the build folder): `Mill_and_dump_tips_do_not_say_HTTP_400`,
  `The_snippet_tint_is_translucent_in_both_themes`.
