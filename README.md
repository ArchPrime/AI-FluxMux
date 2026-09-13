# AI-FluxMux 0.2.1 beta

Desktop router for local (llama-server + GGUF) and cloud models. An OpenAI-compatible **Client app** (Cline, Harness, and similar) connects to **Port** `http://127.0.0.1:5001`.

## Features

- **One Port for local and cloud** — Cline, DeepSeek Harness, or any OpenAI-compatible Client app calls `http://127.0.0.1:5001`. AI-FluxMux talks to llama-server on a private daemon port.
- **Local GGUF launch** — starts and stops `llama-server`, loads a saved model profile (Context, Images, reasoning, GPU/CPU), and parks the model when idle.
- **Port rules (optional)** — watch a local Client-app turn for a hang, tool mill, repeated command, diagnostic dump, filling Context, or llama-server still loading. Pause in time and offer **Send this turn anyway**, **End this turn**, **Let me steer**, or switch to a ready cloud. Turn the whole set off to forward without intervention. Cloud models do not use Port rules.
- **Compact and omit** — shorten older turns and stub older tool bodies on the pack forwarded to llama-server. The Client app still has the full chat.
- **Route requests** — when the loaded local cannot take a turn, or a ready cloud would spare RAM, AI-FluxMux asks before switching. Overlay temperature / max tokens can apply without a reload.
- **In-app Help** — install, llama-server, Cline, Harness, and Port rules. Setting Help links keep a stable topic id so a later Help text update does not break the jump.

## Install (Release zip)

1. Download `AI-FluxMux-0.2.1-beta.zip` from the [GitHub Release](https://github.com/ArchPrime/AI-FluxMux/releases).
2. Unzip to a folder you own (for example `C:\AI-FluxMux`). Do not use Program Files — Help updates write next to the program, and Windows will refuse that there.
3. Double-click `FluxMux.Avalonia.exe`. If Windows SmartScreen appears, choose **More info**, then **Run anyway**. This build is not code-signed.
4. Open the **Help** tab and follow **1 Install AI-FluxMux**, then **2 Install llama-server**, then your local model folder and Client app (Cline or DeepSeek Harness).
5. Point the Client app at Port `http://127.0.0.1:5001`, model id `local`. Do not point it at llama-server or at a Harness web port (often 3080).

Settings are stored in `%AppData%\AI-FluxMux\`, not in the unzip folder.

llama-server, `.gguf` model files, and the Client app are separate downloads. Help names them.

Do not copy `fluxmux_config.json` or `fluxmux_secrets.json` from a development PC into this folder.

## Make a Release zip

Publishing into `artifacts\publish` is safe while the Debug window is open:

```
dotnet publish -c Release -p:PublishProfile=win-x64-selfcontained -o artifacts\publish\AI-FluxMux-0.2.1-beta
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
