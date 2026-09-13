# Unsloth Desktop — development pathway (deferred)

> Deferred notes only. Nothing here is in the UI. Do not start this while NVFP4 launch, Validate, profile, or a `FluxMuxRuntimeService` split is in flight. Revisit when there is energy for one measured slice.

Unsloth Desktop (Aug 2026 beta) is a chat + train + serve app on `llama-server`, default `http://localhost:8888`. It is not a mux. Keep FluxMux **Port** at `5001` (daemon port Port+1). Changing Port to 8888 does not import Unsloth behaviour.

## Not a competitor for this product

Unsloth does not hold a Client-app turn, offer Switch to cloud, Compact only forwarded history, idle park/wake, or write Cline 4.x / Harness `settings.yaml`. Official `unsloth start` is Claude Code / Codex / Hermes / OpenClaw / OpenCode / Pi. Harness can point yaml at `:8888` (needs `sk-unsloth-…`); that is not FluxMux’s Harness block. Do not point Cline or Harness at Unsloth instead of Port.

## Daily profile stays the source of truth

On this RTX 5090: `Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf`, spec type **draft-mtp**, max tokens **16384**. Images on: `mmproj-Qwen3.8-27B-NVFP4-BF16.gguf`, Context **163840**. Text-only: Images off, Context **213056**. FluxMux already emits `--spec-draft-n-max` default **2**. AutoTune must not lower those hand-tuned values.

Qwen 3.8 is hybrid (DeltaNet + attention). FluxMux bills KV at `hybridKvScale = 0.38` and q8_0. Unsloth’s “spill to RAM above ~45k” banner treats this GGUF like dense attention (and often f16 + a large MTP draft). Do not copy 45k into AutoTune. Trust FluxMux Health (**GPU only** vs **GPU + CPU**) on a long Client-app turn.

Their hub `unsloth/Qwen3.8-27B-NVFP4` (safetensors) is a different file. Studio is not designed for native NVFP4 inference on Windows yet.

## Steal later (one slice at a time)

1. **MTP draft reserve in `LocalVramFootprintEstimate`** — same shape as Images-on. When spec type is `draft-mtp` (or the GGUF looks like MTP), leave extra leftover for the second KV cache. Advice-only for *new* / AutoTune rows. Do not rewrite the saved 5090 profile. Measure first: Unsloth auto Context vs **163840** / **213056** with FluxMux parked. Their own MTP fit still has a hole when `--spec-type` is in extra args (FluxMux’s launch path).

2. **Health / Validate: llama-server too old for MTP** — one line when help lacks `--spec-type draft-mtp`. Links only; no in-app extract.

3. **Optional Validate: `--spec-draft-n-max` 3 or 4** vs the default 2. Watch tok/s and whether Health stays GPU-only at the saved Context. Do not copy Unsloth’s GPU preset of 6.

4. **Tool-call heal on Port** — landed as `LocalToolCallHealing` / `LocalToolCallHealSession`. Promotes text-form calls to structured `tool_calls` for names the Client app declared, coerces invalid argument JSON, dedupes, and holds XML across SSE deltas. Diagnostics logs `local_tool_heal`. Do not add Studio sandbox tools or a nudge retry that issues extra generation.

Compact policy steal (not Unsloth Auto Compact): when a tool ledger exists, do not paste truncated dropped file bodies into the forward summary. The Client app still has the full chat.

## Do not steal

- Port 8888, Cloudflare, Studio chat, training, diffusion, server-side web search / sandbox
- Unsloth Auto Compact (different job from FluxMux Compact)
- Idle auto-unload (worse than park/wake)
- A Qwen sampling table that overwrites hand-tuned temperature / max tokens / template
- Tensor parallel on this single 5090 (`LocalSplitMode` already exists; `--split-mode tensor` wants two or more GPUs and Unsloth then disables MTP)
- ngram-mod CPU chain, `--model-draft` sidecar (MTP is inside this GGUF)
- Their 0.85 VRAM constant or “smart auto context” as a replacement for measured leftover → 213056
- In-app llama.cpp overwrite
