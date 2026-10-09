# AI-FluxMux 0.3.0 beta

Desktop router for local and cloud models. Designed for any OpenAI-compatible Client app (Cline, DeepSeek Harness, and similar). Extensive control, turn by turn, or per stored model profile, or via port rules, over model behavior. Model tuning wizard, model switch testing, YaRN context extension and more.

**Requirements:** Windows 10/11 (x64). No .NET install needed — the app is self-contained.

> **Optional prerequisites** (local models, Ollama, DeepSeek harness, cloud API keys, build-from-source) with download links: see [**prerequisites.md**](prerequisites.md).

## Features

- **One Port for local and cloud** — Cline, DeepSeek Harness, or any OpenAI-compatible Client app calls `http://127.0.0.1:5001`. AI-FluxMux talks to llama-server on a private daemon port.
- **Local GGUF launch** — starts and stops `llama-server`, loads a saved model profile (Context, Images, reasoning, GPU/CPU), and parks the model when idle.
- **Port rules (optional)** — watch a local Client-app turn for a hang, tool mill, repeated command, diagnostic dump, filling Context, or llama-server still loading. Pause in time and offer **Send this turn anyway**, **End this turn**, **Let me steer**, or switch to a ready cloud. Turn the whole set off to forward without intervention. Cloud models do not use Port rules.
- **Compact and omit** — shorten older turns and stub older tool bodies on the pack forwarded to llama-server. The Client app still has the full chat.
- **Route requests** — when the loaded local cannot take a turn, or a ready cloud would spare RAM, AI-FluxMux asks before switching. Overlay temperature / max tokens can apply without a reload.
- **In-app Help** — install, llama-server, Cline, Harness, and Port rules. Setting Help links keep a stable topic id so a later Help text update does not break the jump.

## Install (Release zip)

1. Download the app zip. Use this direct link (it skips the GitHub releases page, whose asset list can fail to load):
   [AI-FluxMux-0.3.0-beta.zip](https://github.com/ArchPrime/AI-FluxMux/releases/download/v0.3.0-beta/AI-FluxMux-0.3.0-beta.zip)
   It is about 76 MB. Do **not** use the "Source code (zip)" download on the repo's Code menu — that is the source to build from, not the app, and it has no ready-to-run exe.
2. Unzip it. You get a folder named `AI-FluxMux-0.3.0-beta`. Move or open that folder somewhere you own (for example `C:\AI-FluxMux`). Do not use Program Files — Help updates write next to the program, and Windows will refuse that there.
3. Double-click `FluxMux.Avalonia.exe` **inside that unzipped folder**. If Windows SmartScreen appears, choose **More info**, then **Run anyway**. This build is not code-signed.
4. Open the **Help** tab in AI-FluxMux and follow the instructions there. In a nutshell:
   - Install the latest `llama-server` version for your GPU (don't forget the CUDA DLLs if applicable).
   - Nominate your local model folder.
   - Point your Client app at `http://127.0.0.1:5001` (or whatever free port you prefer), model id `local`, API key `""`.
   - Do **not** point it at llama-server directly, or at a Harness web port (often 3080).

Settings are stored in `%AppData%\AI-FluxMux\`, not in the unzip folder.

llama-server, `.gguf` model files, and the Client app are separate downloads. Help names them.

Do not copy `fluxmux_config.json` or `fluxmux_secrets.json` from a development PC into this folder.

## Make a Release zip

Publishing into `artifacts\publish` is safe while the Debug window is open:

```
dotnet publish -c Release -p:PublishProfile=win-x64-selfcontained -o artifacts\publish\AI-FluxMux-0.3.0-beta
```

Zip that folder. Do not launch the daily app from `artifacts\`.

## Build from source (developers)

.NET 10. Close AI-FluxMux, then `dotnet build` / `dotnet test` — that is the Debug launcher. `start-ai-fluxmux.cmd` in this folder builds Debug and starts the exe from the project directory so a Word-saved `Help.html` here is what the Help tab loads.

While the app is already running, send compile and test output aside so the live files are not overwritten (file-lock safety only; do not launch from this folder):

```
dotnet build -p:UseAppHost=false -o artifacts\test-ref
dotnet test -p:UseAppHost=false -o artifacts\test-ref
```

How to continue development without a long briefing is in [`AGENTS.md`](AGENTS.md).

## Licence

Copyright (c) 2026 by Paul King and Architecture Prime Ltd.

AI-FluxMux is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE). Commercial use requires a separate licence from Architecture Prime Ltd. Third-party components remain under their own terms (`THIRD-PARTY-NOTICES.md`).
