# AI-FluxMux — Dependencies & Environment Setup

Everything needed to build and run AI-FluxMux from source, with download links.
Verified working environment: **Windows 11 Pro x64**, .NET SDK **10.0.401**, .NET runtime **10.0.12**, Git **2.55**.

> Target frameworks: the app + updater + tests target **.NET 10** (`net10.0`; the standalone updater targets `net10.0-windows`). You need the **.NET 10 SDK** to build, and the **.NET 10 runtime** to run.

---

## 1. Required tools (install these)

### .NET 10 SDK (build) — REQUIRED
Installs the compiler, MSBuild, and the .NET 10 runtime in one package.
- **Download (Windows x64 installer):** https://dotnet.microsoft.com/download/dotnet/10.0
- **Direct SDK installer (x64):** https://dotnet.microsoft.com/download/dotnet/10.0 (select "SDK" → "Windows x64")
- **Verify:** `dotnet --list-sdks` → should show `10.0.4xx`
- **Verify runtime:** `dotnet --list-runtimes` → should show `Microsoft.NETCore.App 10.0.xx`

> If you only want to *run* a prebuilt app (not build), the **.NET 10 Desktop Runtime** is enough:
> https://dotnet.microsoft.com/download/dotnet/10.0 (select ".NET Desktop Runtime" → "Windows x64")

### Git (source control) — REQUIRED for cloning/building from a repo
- **Download (Windows x64):** https://git-scm.com/download/win
- **Verify:** `git --version`

### Visual Studio Code (optional editor)
- **Download:** https://code.visualstudio.com/
- Recommended extensions: C# Dev Kit, Avalonia (community).

### Visual Studio 2022 / 2022+ (optional, full IDE)
If you prefer a full IDE with debugger:
- **Download:** https://visualstudio.microsoft.com/downloads/
- Workload: **".NET desktop development"**.

---

## 2. NuGet packages (auto-restored — no manual download needed)

These are pulled automatically by `dotnet build` / `dotnet restore` from [nuget.org](https://www.nuget.org/). Listed here for reference / offline planning.

### Main app — `FluxMux.Avalonia.csproj`
| Package | Version | Purpose | NuGet page |
|---------|---------|---------|-----------|
| Avalonia | 12.1.1 | Cross-platform UI framework | https://www.nuget.org/packages/Avalonia |
| Avalonia.Desktop | 12.1.1 | Desktop platform (Win32/X11) | https://www.nuget.org/packages/Avalonia.Desktop |
| Avalonia.Themes.Fluent | 12.1.1 | Fluent theme | https://www.nuget.org/packages/Avalonia.Themes.Fluent |
| Avalonia.Fonts.Inter | 12.1.1 | Inter font | https://www.nuget.org/packages/Avalonia.Fonts.Inter |
| AvaloniaUI.DiagnosticsSupport | 2.2.3 | Dev diagnostics (Debug only) | https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM helpers | https://www.nuget.org/packages/CommunityToolkit.Mvvm |
| HtmlAgilityPack | 1.12.4 | HTML parsing (Help viewer) | https://www.nuget.org/packages/HtmlAgilityPack |
| SkiaSharp | 3.119.4 | 2D graphics | https://www.nuget.org/packages/SkiaSharp |
| System.Diagnostics.PerformanceCounter | 10.0.0 | Perf counters | https://www.nuget.org/packages/System.Diagnostics.PerformanceCounter |

### Tests — `FluxMux.Avalonia.Tests.csproj`
| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.NET.Test.Sdk | 17.14.1 | Test host |
| xunit | 2.9.3 | Test framework |
| xunit.runner.visualstudio | 3.1.4 | VS test runner |

> `FluxMux.Updates.Core` and `FluxMux.Updates` have **no external NuGet dependencies** (the updater uses the built-in Windows Desktop / WinForms stack from the .NET SDK).

---

## 3. Runtime / external components the app manages at runtime

These are **not** build dependencies — the app detects/downloads/manages them itself (see the in-app "Servers" / update tooling). Listed so you know what the app expects on a working machine:

| Component | Role | Where the app gets it |
|-----------|------|----------------------|
| **llama-server** (llama.cpp) | Local LLM inference server | In-app updater / `FluxMux.Updates.exe` (downloads matching release from https://github.com/ggml-org/llama.cpp/releases) |
| **Model files (`.gguf`)** | LLM weights | User-supplied (e.g. Hugging Face: https://huggingface.co/models) |
| **DeepSeek Harness** | Optional cloud/local harness | In-app updater |

> No manual install of llama.cpp is required for *building* the app. It's only needed at *runtime* to serve local models.

---

## 4. Build from source

```powershell
# 1. Get the source
git clone https://github.com/ArchPrime/AI-FluxMux.git
cd AI-FluxMux

# 2. (Optional) build the standalone updater first so the app can copy it in
dotnet build FluxMux.Updates\FluxMux.Updates.csproj -c Debug

# 3. Build the main app
dotnet build FluxMux.Avalonia.csproj -c Debug

# 4. Run
.\bin\Debug\net10.0\FluxMux.Avalonia.exe
```

Or use the provided script: `build-fluxmux.bat` (in the repo root).

### Run the tests
```powershell
dotnet test FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug
```

> **Note (running app locks output):** if AI-FluxMux is currently running, it locks `bin\Debug\net10.0\*.dll`. Build to a side output to avoid the lock:
> ```powershell
> dotnet build FluxMux.Avalonia.csproj -c Debug -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\
> ```

---

## 5. Quick environment checklist

- [ ] Windows 10/11 x64 (or build on Linux/macOS for the Avalonia UI; the standalone updater is Windows-only)
- [ ] .NET 10 SDK installed → `dotnet --list-sdks` shows `10.0.4xx`
- [ ] .NET 10 runtime installed → `dotnet --list-runtimes` shows `Microsoft.NETCore.App 10.0.xx`
- [ ] Git installed → `git --version`
- [ ] Internet access for NuGet restore (or a pre-populated NuGet cache / offline feed)
- [ ] (Runtime only) llama-server + a `.gguf` model, if you want local inference

---

## 6. Where this file lives

- **Project copy:** `FluxMux.Avalonia/DEPENDENCIES.md` (this file, tracked in git)
- **Backup copy:** `backups/FluxMux.Avalonia_0.3.0_20261009_source/DEPENDENCIES.md`

Keep them in sync when dependencies change.
