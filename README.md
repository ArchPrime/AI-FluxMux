# AI-FluxMux 0.2 beta

Desktop router for local (llama-server + GGUF) and cloud models. An OpenAI-compatible **Client app** (Cline, Harness, and similar) connects to `http://127.0.0.1:5001`.

Operator Help is inside the app. How to continue development without a long briefing is in [`AGENTS.md`](AGENTS.md).

## Run the zip (usual)

Unzip the Release folder to a place you own (not Program Files). Double-click `FluxMux.Avalonia.exe`. If Windows SmartScreen appears, choose **More info**, then **Run anyway** — this build is not code-signed.

Open the **Help** tab and start with **Install AI-FluxMux**, then **llama-server**. Settings are stored in `%AppData%\AI-FluxMux\`.

Do not ship or unzip `fluxmux_config.json` or `fluxmux_secrets.json` from a development PC.

## Make a Release zip

Close the Debug AI-FluxMux window first if you would overwrite `bin\Debug`. Publishing into `artifacts\publish` is safe while that window is open:

```
dotnet publish -c Release -p:PublishProfile=win-x64-selfcontained -o artifacts\publish\AI-FluxMux-0.2-beta
```

Zip that folder. Do not launch the daily app from `artifacts\`.

## Build from source (developers)

.NET 10. Close AI-FluxMux, then `dotnet build` / `dotnet test` — that is the Debug launcher.

While the app is already running, send compile and test output aside so the live files are not overwritten (file-lock safety only; do not launch from this folder):

```
dotnet build -p:UseAppHost=false -o artifacts\test-ref
dotnet test -p:UseAppHost=false -o artifacts\test-ref
```

Point AI-FluxMux at your `llama-server.exe` and model folder on Servers. Launch a **model profile**, then point the Client app at Port 5001.

## Licence

Copyright (c) 2026 by Paul King and Architecture Prime Ltd.

AI-FluxMux is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE). Commercial use requires a separate licence from Architecture Prime Ltd. Third-party components remain under their own terms (`THIRD-PARTY-NOTICES.md`).
