# AI-FluxMux — Prerequisites

AI-FluxMux is a **self-contained** Windows app: it bundles the .NET runtime, so you do **not** need to install .NET to run it. The only hard requirement is Windows 10/11 (x64).

Everything else is **optional** and depends on which features you want to use. The app runs out of the box in **cloud-only** mode (route requests to cloud providers) with no extra software.

## Required

| Item | Minimum | Notes |
|---|---|---|
| Windows | 10 (x64) or 11 | The published build targets `win-x64`. |
| Disk space | ~150 MB | For the app folder. |

That's all you need to launch the app and use cloud providers.

## Optional — Local model inference (llama.cpp)

To run **local** LLMs on your own machine (the app's core "local-first" feature), you need:

| Item | What it is | Where to get it |
|---|---|---|
| **llama-server** (llama.cpp) | The local inference server the app launches and points at. | [https://github.com/ggml-org/llama.cpp/releases](https://github.com/ggml-org/llama.cpp/releases) — download a Windows build. The app can also update it in place via `FluxMux.Updates.exe llama --server-dir <folder>`. |
| **A model file** (`.gguf`) | The weights llama-server loads. | [https://huggingface.co/models](https://huggingface.co/models) — filter by `gguf`. |
| **NVIDIA GPU + driver** *(recommended)* | For fast local inference and VRAM telemetry. | Driver: [https://www.nvidia.com/en-us/drivers/](https://www.nvidia.com/en-us/drivers/). The app reads GPU memory via `nvidia-smi` (bundled with the NVIDIA driver). |
| **CPU-only** *(alternative)* | Runs without a GPU, slower. | No extra install — llama.cpp has a CPU build. |

> The app's **Dependency Audit** (in-app) checks for `llama-server`, model files, and `nvidia-smi` and tells you exactly what's missing.

## Optional — Ollama

If you prefer to use [Ollama](https://ollama.com) as your local backend instead of llama-server:

| Item | Where |
|---|---|
| Ollama | [https://ollama.com/download](https://ollama.com/download) |

## Optional — DeepSeek Harness (dsh)

For the DeepSeek harness integration:

| Item | Where |
|---|---|
| `dsh` | Installed via npm; the app's updater manages it (`FluxMux.Updates.exe harness`). |

## Optional — Cloud provider API keys

To route to cloud providers (OpenAI, Anthropic, DeepSeek, etc.) you need the relevant **API key**. You enter these in the app's settings; no system-level install is required.

## Build prerequisites (for developers only)

If you want to **build from source** (not needed to use the published app):

| Item | Where |
|---|---|
| .NET 10 SDK | [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Git | [https://git-scm.com/downloads](https://git-scm.com/downloads) |

```powershell
git clone https://github.com/ArchPrime/AI-FluxMux.git
cd AI-FluxMux
dotnet build -c Release
```

---

**Summary:** install the app and you're ready for cloud use. Add llama.cpp + a model (+ NVIDIA driver for GPU) to use local inference. Ollama, DeepSeek harness, and cloud API keys are all optional add-ons.
