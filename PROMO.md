# AI-FluxMux

**An endpoint-agnostic, OpenAI-compatible router for local and cloud models.**

AI-FluxMux is a Windows desktop app that sits between your AI client (Cline, DeepSeek Harness, or any OpenAI-compatible app) and your models — local GGUF via llama-server, cloud via API. One endpoint: `http://127.0.0.1:5001`. No proprietary protocol. No vendor lock-in.

---

## Key features

### 1. Visibility

Keep tabs on what's happening. Prefill time, VRAM pressure, context fill, model speed, why a turn routed where it did. Without AI-FluxMux, it's a black box — you send a prompt and wait, and when something goes wrong you often have to start again.

- **Prefill progress bar** — accurate, because it learned your hardware's actual prefill speed (cold, warm, and RAM-spill). No more staring at a blank chat for 90 seconds wondering if it's stuck.
- **VRAM, RAM, CPU, and context gauges** — live telemetry. See that your model is at 92% VRAM and the next turn will spill to RAM (slower). See the context filling toward the limit.
- **Throughput/latency graph** — every turn plotted as it happens. Token speed, turn timing, prefill vs. generation phases.
- **Routing explanations** — every routing decision is logged: "model not ready," "context filling," "vision needed," "VRAM pressure." No black-box routing.
- **Model statistics** — measured VRAM footprint, prefill speed, generation speed. The numbers match what you're about to run, not what the model's README claims.

### 2. Intervention

Port rules pause *before* the token burn. Model switch offer *before* the context loss. Without AI-FluxMux, you find out about problems after they've already happened.

- **Overflow warning** — before the turn fails: "This turn will exceed context. Options: compact, switch model, or send anyway." Not a 400 error. A choice.
- **Hang watchdog** — if the model goes quiet mid-generation: "Model appears stuck. Options: wait, stop, or switch to cloud." Not an infinite wait. A choice.
- **Port rules** — if your client app is tool-milling (running the same command 5 times): "This looks like a loop. Send anyway, end turn, or steer?" Not a 10-minute token burn. A choice.
- **Model switch mid-chat** — "Your local model's context is too small for this chat. Switch to [bigger model]? The chat continues." Not a new chat and lost context. A choice.
- **Operator override** — force any turn to local or cloud from the UI, regardless of what the router would choose.

### 3. Continuity

The chat keeps working. Without AI-FluxMux, every problem can become a hard stop.

- **Context overflowed?** → compact & resurrect. Hail Mary compaction shrinks just enough to fit. Essence-preserving compaction re-asserts the mission (goal evolution, decisions, dead ends) after the transcript is gone.
- **Local model can't take the turn?** → cloud or a different local picks up, don't stall. Dynamic routing re-evaluates on every turn. Local model loading? Turn goes to cloud. VRAM full? Turn goes to cloud. Ready again? Back to local. You don't switch anything.
- **Model switches mid-chat?** → conversation continues, don't restart. Maintain a pool of local model profiles. When the loaded model can't cover a turn (context too small, no vision, no reasoning), AI-FluxMux offers to switch. You approve, it reloads, the chat continues.
- **Compaction happens?** → essence survives, don't lose the mission. A durable log captures the *meaning* of your session and re-asserts it after compaction. Cline's own auto-compact still works as the first line of defence; AI-FluxMux adds the layer that survives *hard* compaction.
- **Context too big for VRAM?** → YaRN extends it into system RAM (up to 1M tokens). The model keeps working; it just gets slower.

### 4. Customisation

Extensive local model tuning and saved hot-swappable profiles. The same model can have multiple variants that you switch into the chat to address multiple scenarios.

Each profile carries **30+ settings** in the UI, all persisted, all overridable:

- **Context** — context window size, YaRN extension into RAM (up to 1M tokens), allow or prevent VRAM→RAM spill, fit-to-VRAM auto-sizing
- **Vision** — on/off, max image size, vision projector (mmproj) file
- **KV cache** — quantisation type for K and V separately (f16, q8_0, q4_0), cache strategy (Auto/Full/PrefixCheckpoints/Minimal), cache RAM for prefix reuse
- **GPU** — offload mode (auto, all, none), GPU layers, flash attention, multi-GPU (auto/manual split, tensor split, main GPU)
- **Reasoning** — Off / Low / Medium / XHigh
- **Generation** — temperature, max tokens, speculative decoding (MTP draft)
- **Performance** — thread count, batch threads, batch size, ubatch size
- **Compaction** — auto-compact on/off
- **Chat format** — chat template, chat parser, unban tokens
- **Concurrency** — multi-user mode (allow parallel requests)

**Hot-swappable variants** — the same GGUF model can have multiple profiles with different settings. A "fast" variant (small context, no YaRN, f16 cache, reasoning off) for quick turns. A "deep" variant (YaRN extended, q8_0 cache, reasoning XHigh) for long sessions. A "vision" variant (images on, larger context, projector loaded) for multimodal work. Switch between them mid-chat without restarting the model.

The **model tuning wizard** recommends starting values based on your hardware (VRAM, RAM, GPU count), but every setting is yours to override.

---

## Under the hood

- **OpenAI-compatible** — `/v1/chat/completions` (streaming and non-streaming), `/v1/models`, `/v1/health`. Any OpenAI-compatible client app works.
- **Cloud providers** — Gemini, Anthropic, OpenAI, GitHub Copilot, Groq, DeepSeek, OpenRouter, Together, or any custom OpenAI-compatible endpoint.
- **Hardware learning** — prefill performance model (cold, warm, RAM-spill), VRAM footprint estimation, cache strategy advisor.
- **Cline integration** — context window sync, compaction file writing (native rich format), tool-call healing, Cline-specific essence re-assertion.
- **DeepSeek Harness integration** — launches and stops the Harness web UI, writes Harness settings (base URL, context window, max tokens, images, reasoning) from the loaded local profile, keeps Harness in sync when you switch profiles.
- **~82,000 lines** of C# / Avalonia, 140+ service files, **1,075 unit tests** across 100+ test files.
- **Self-contained** — bundles the .NET 10 runtime. No .NET install needed. Windows 10/11 x64.
- **PolyForm Noncommercial License 1.0.0** — free for personal and non-commercial use. Commercial licensing available from Architecture Prime Ltd.

---

## Why not just use X?

**Why not point Cline directly at llama-server?**
You can. Cline has its own auto-compact, and it works fine for straightforward sessions. But when the context overflows hard, the model stalls, or VRAM runs out, Cline's compaction is all-or-nothing — it shortens the transcript but doesn't preserve the *meaning* of your session. It also doesn't route to a cloud fallback mid-chat, doesn't monitor VRAM pressure, and doesn't offer port rules to intervene before a turn goes off the rails. AI-FluxMux adds that layer — while Cline's own compaction still works as the first line of defence.

**Why not use Ollama?**
Ollama is a model manager — it downloads and serves GGUF models. It doesn't do context compaction, doesn't route between local and cloud mid-chat, doesn't monitor VRAM pressure, doesn't offer port rules, and doesn't preserve session meaning across compaction. AI-FluxMux adds the intelligence layer on top.

**Why not use a cloud API directly?**
Cloud APIs are convenient but: every token costs money, your prompts leave your machine, and you're at the mercy of rate limits and outages. AI-FluxMux lets you run local models for free and private, and only fall back to cloud when you need to. You control the cost. You control the privacy.

**Why not build this yourself?**
You could. It's ~82,000 lines of C# with 1,075 tests. The hard parts — VRAM pressure detection, context compaction that preserves meaning, YaRN RAM-spill management, per-turn routing decisions, Cline's compaction file format — are all solved here. If you want to tinker, the source is on GitHub. If you just want it to work, download the zip.

**Why not use a different local AI router?**
Most local AI tools are either model managers (Ollama, LM Studio) or chat UIs (Open WebUI, LibreChat). AI-FluxMux is neither. It's an **infrastructure layer** that sits between your existing client app and your models. It doesn't replace your workflow — it makes your existing workflow more resilient.

---

## What it's not

- It's not a chat app. It's the **infrastructure layer** between your client app and your models.
- It doesn't replace llama-server. It **manages** it.
- It doesn't replace Cline's compaction. It **survives** what Cline's compaction can't.
- It doesn't pick models for you. It gives you the **telemetry and controls** to make informed decisions.
- It's not a cloud service. Everything runs on your machine. Your prompts, your models, your data.

---

## Get started

1. Download the latest release zip from [GitHub Releases](https://github.com/ArchPrime/AI-FluxMux/releases).
2. Unzip to a folder you own (not Program Files).
3. Double-click `FluxMux.Avalonia.exe`.
4. Open the **Help** tab: install llama-server, nominate your GGUF folder, point your client app at `http://127.0.0.1:5001`.

That's it. Your local AI, with the safety net.
