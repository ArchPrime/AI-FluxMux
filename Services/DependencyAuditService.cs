using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Models;

namespace FluxMux.Avalonia.Services;

public sealed class DependencyAuditService
{
    // llama.cpp uses release tags like b7000, not dotted 0.0.0. Model Profiles can emit
    // -fa, --jinja, --spec-type, --swa-full, and --reasoning* against this floor.
    private const int LlamaMinimumBuild = 7000;
    private const string LlamaMinimumLabel = "b7000";

    private readonly string _configPath;

    public DependencyAuditService(string configPath)
    {
        _configPath = configPath;
    }

    public async Task<DependencyAuditReport> BuildReportAsync(
        string activeProvider,
        string modelDirectory,
        int orchestratorPort,
        string llamaServerPath = "",
        CancellationToken cancellationToken = default)
    {
        _ = activeProvider;
        var scriptRoot = Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;
        var workspaceRoot = Directory.GetParent(scriptRoot)?.FullName ?? scriptRoot;
        var workbenchRoot = Directory.GetParent(workspaceRoot)?.FullName ?? workspaceRoot;
        var secretsPath = Path.Combine(scriptRoot, "fluxmux_secrets.json");

        var defaultLlamaDirectory = Path.Combine(workbenchRoot, "llama-server");
        var defaultServerPath = Path.Combine(defaultLlamaDirectory, "llama-server.exe");
        var serverPath = ResolveLlamaServerPath(llamaServerPath, defaultServerPath, scriptRoot);

        var logDirectory = Path.Combine(workbenchRoot, "Logs");
        var stateFolder = Path.Combine(defaultLlamaDirectory, "kv_cache_slots");
        var scratchpadPath = Path.Combine(workspaceRoot, ".vscode", "workspace_scratchpad.md");

        var llamaTask = GetLlamaVersionAsync(serverPath, cancellationToken);
        var hardwareTask = BuildHardwareSummaryAsync(cancellationToken);

        await Task.WhenAll(llamaTask, hardwareTask);

        var llamaRaw = llamaTask.Result;

        var coreDependencies = new List<DependencyMatrixRow>
        {
            BuildLlamaServerRow(llamaRaw, serverPath)
        };

        var providerRequirements = new List<ProviderRequirementRow>
        {
            new()
            {
                Provider = "Gemini",
                Sdk = "HTTPS",
                RequiredFiles = $"{_configPath}; {secretsPath}",
                RequiredFields = "Config: ActiveCloud, ActiveCloudModel; Secrets: GeminiApiKey",
                Guidance = "Needed when running the Gemini cloud route."
            },
            new()
            {
                Provider = "Anthropic",
                Sdk = "HTTPS",
                RequiredFiles = $"{_configPath}; {secretsPath}",
                RequiredFields = "Config: ActiveCloud, ActiveCloudModel; Secrets: AnthropicApiKey",
                Guidance = "Needed when running the Anthropic cloud route."
            },
            new()
            {
                Provider = "OpenAI",
                Sdk = "HTTPS",
                RequiredFiles = $"{_configPath}; {secretsPath}",
                RequiredFields = "Config: ActiveCloud, ActiveCloudModel, OpenAiEndpoint; Secrets: OpenAiApiKey",
                Guidance = "Needed when running the OpenAI cloud route."
            },
            new()
            {
                Provider = "Custom OpenAI-Compatible",
                Sdk = "HTTPS",
                RequiredFiles = $"{_configPath}; {secretsPath}",
                RequiredFields = "Config: CustomCloudProviders[name].Endpoint (legacy CustomCompatEndpoint still read); Secrets: CustomCompatApiKeys[name] (legacy CustomCompatApiKey for the default custom slot)",
                Guidance = "Needed when running a custom OpenAI-compatible cloud route. You can register several named custom providers at once."
            },
            new()
            {
                Provider = "Copilot GitHub",
                Sdk = "HTTPS",
                RequiredFiles = $"{_configPath}; {secretsPath}",
                RequiredFields = "Config: ActiveCloud, ActiveCloudModel, GitHubCopilotEndpoint; Secrets: GitHubCopilotApiKey",
                Guidance = "Needed when running the GitHub Copilot cloud route."
            },
            new()
            {
                Provider = "Local model route",
                Sdk = "llama-server",
                RequiredFiles = $"{_configPath}; {serverPath}",
                RequiredFields = "Config: ActiveLocalModel, ModelDirectory, OrchestratorPort, LocalVisionEnabled, LocalVisionProjectorPath",
                Guidance = "Needed when launching a local model."
            }
        };

        var coreInstallGuide = new List<InstallGuideRow>
        {
            new()
            {
                Dependency = "llama-server",
                InstallOrUpdate = $"Download a llama.cpp Windows release at {LlamaMinimumLabel} or newer and set Servers → llama-server.exe to that binary.",
                Source = "https://github.com/ggml-org/llama.cpp/releases",
                Guidance = "Used for local model launch, including Flash Attention, Jinja templates, speculative decode, SWA, and Thinking flags on Model Profiles."
            },
            new()
            {
                Dependency = "NVIDIA driver",
                InstallOrUpdate = "Install the current NVIDIA driver for this GPU so CUDA llama-server builds and VRAM readout work.",
                Source = "https://www.nvidia.com/Download/index.aspx",
                Guidance = "Used for GPU telemetry on this tab and for CUDA llama-server builds."
            }
        };

        var hardRequiredPaths = new List<PathStatusRow>
        {
            BuildPathStatusRow("Script root", "Folder", scriptRoot, true),
            BuildPathStatusRow("Orchestrator config", "File", _configPath, false),
            BuildPathStatusRow("Orchestrator secrets", "File", secretsPath, false),
            BuildPathStatusRow("Local model directory", "Folder", modelDirectory, true),
            BuildPathStatusRow("llama-server executable", "File", serverPath, false)
        };

        var runtimePaths = new List<PathStatusRow>
        {
            BuildRuntimePathStatusRow("Log directory", "Folder", logDirectory),
            BuildRuntimePathStatusRow("KV cache state folder", "Folder", stateFolder),
            BuildRuntimePathStatusRow("Workspace scratchpad", "File", scratchpadPath),
            BuildRuntimePathStatusRow("Proxy runtime state", "File", Path.Combine(logDirectory, "Proxy_Runtime_State.json")),
            BuildRuntimePathStatusRow("Proxy handshake log", "File", Path.Combine(logDirectory, "Proxy_Cloud_Handshake.log")),
            BuildRuntimePathStatusRow("Combined runtime log", "File", Path.Combine(logDirectory, "Orchestrator_Runtime_Combined.log"))
        };

        var hardwareSummary = hardwareTask.Result;
        var localCapabilities = BuildLocalModelCapabilities(modelDirectory);

        return new DependencyAuditReport
        {
            HardwareSummary = hardwareSummary,
            CoreDependencies = coreDependencies,
            ProviderAddons = [],
            CoreInstallGuide = coreInstallGuide,
            SdkInstallGuide = [],
            ProviderRequirements = providerRequirements,
            HardRequiredPaths = hardRequiredPaths,
            RuntimePaths = runtimePaths,
            LocalModelCapabilities = localCapabilities
        };
    }

    private static string ResolveLlamaServerPath(string configuredPath, string defaultServerPath, string scriptRoot)
    {
        var candidates = new List<string>
        {
            configuredPath,
            defaultServerPath,
            Path.Combine(scriptRoot, "llama-server.exe")
        };

        try
        {
            var whereOutput = RunCommandSync("where", "llama-server.exe", 1000);
            foreach (var line in whereOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(line.Trim());
            }
        }
        catch
        {
            // Ignore PATH lookup failures and keep candidate probing deterministic.
        }

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return defaultServerPath;
    }

    private static DependencyMatrixRow BuildLlamaServerRow(string installed, string serverPath)
    {
        const string guidance =
            "Used to launch local models. Model Profiles Flash Attention, Jinja templates, speculative decode, SWA, and Thinking flags need this llama.cpp floor.";

        var filePresent = !string.IsNullOrWhiteSpace(serverPath) && File.Exists(serverPath);
        var status = "OK";

        if (!filePresent || installed.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
        {
            status = "Missing";
        }
        else
        {
            var build = LlamaCppBuildNumber.TryParse(installed);
            if (build is null)
            {
                status = "Present (build unknown)";
            }
            else if (build.Value < LlamaMinimumBuild)
            {
                status = $"Older than {LlamaMinimumLabel}";
            }
        }

        return new DependencyMatrixRow
        {
            Dependency = "llama-server",
            Minimum = LlamaMinimumLabel,
            Installed = string.IsNullOrWhiteSpace(installed) ? "Unknown" : installed,
            Status = status,
            Guidance = guidance
        };
    }

    private static PathStatusRow BuildPathStatusRow(string component, string kind, string path, bool directory)
    {
        var exists = directory ? Directory.Exists(path) : File.Exists(path);
        return new PathStatusRow
        {
            Component = component,
            Kind = kind,
            ResolvedPath = path,
            Status = exists ? "Present" : "Missing",
            Guidance = directory
                ? "Required directory used by AI-FluxMux runtime features."
                : "Required file used by AI-FluxMux runtime features."
        };
    }

    private static PathStatusRow BuildRuntimePathStatusRow(string component, string kind, string path)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        return new PathStatusRow
        {
            Component = component,
            Kind = kind,
            ResolvedPath = path,
            Status = exists ? "Present" : "Not yet generated",
            Guidance = "Runtime-generated asset; appears after related workflow runs."
        };
    }

    private static List<LocalModelCapabilityRow> BuildLocalModelCapabilities(string modelDirectory)
    {
        var rows = new List<LocalModelCapabilityRow>();
        if (!Directory.Exists(modelDirectory))
        {
            rows.Add(new LocalModelCapabilityRow
            {
                Model = "(none found)",
                Format = "-",
                VisionSupport = "Unknown",
                Notes = "Model directory does not exist.",
                Guidance = "Local model scan cannot run until a valid model directory is configured."
            });
            return rows;
        }

        List<string> files;
        List<string> mmprojFiles;
        try
        {
            files = Directory.EnumerateFiles(modelDirectory, "*.gguf", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();

            mmprojFiles = Directory.EnumerateFiles(modelDirectory, "*mmproj*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try
                    {
                        return Path.GetFullPath(path);
                    }
                    catch
                    {
                        return string.Empty;
                    }
                })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
        }
        catch (Exception ex)
        {
            rows.Add(new LocalModelCapabilityRow
            {
                Model = "(scan unavailable)",
                Format = "local model",
                VisionSupport = "Unknown",
                Notes = "Could not scan the configured model directory: " + ex.Message,
                Guidance = "Check that the model directory exists and AI-FluxMux has permission to read its subfolders."
            });
            return rows;
        }

        if (files.Count == 0)
        {
            rows.Add(new LocalModelCapabilityRow
            {
                Model = "(none found)",
                Format = "local model",
                VisionSupport = "Unknown",
                Notes = "No local models found in the configured model directory or its subfolders.",
                Guidance = "Choose the folder containing your local models on the Servers tab, then refresh this scan."
            });
            return rows;
        }

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var lower = fileName.ToLowerInvariant();

            var likelyVision = lower.Contains("vision")
                               || lower.Contains("llava")
                               || lower.Contains("vl")
                               || lower.Contains("image")
                               || lower.Contains("multimodal")
                               || lower.Contains("omni");

            var mmprojExists = mmprojFiles.Any(projector =>
                FluxMuxRuntimeService.VisionProjectorMatchesModel(file, projector));

            var support = likelyVision || mmprojExists ? "Possible vision support" : "Likely text-only";
            var notes = likelyVision
                ? "Filename indicates a vision variant. Enable local vision input and select a compatible projector before launch."
                : "No explicit vision marker in filename.";

            if (mmprojExists)
            {
                notes += " Detected matching mmproj sidecar.";
            }

            rows.Add(new LocalModelCapabilityRow
            {
                Model = fileName,
                Format = "local model",
                VisionSupport = support,
                Notes = notes,
                Guidance = "Filename and projector-sidecar estimate only; launch validation is required to confirm vision support."
            });
        }

        return rows;
    }

    private async Task<string> GetLlamaVersionAsync(string serverPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(serverPath))
        {
            return "Not Found";
        }

        var probes = new[] { "--version", "-v", string.Empty };
        foreach (var probe in probes)
        {
            var output = await RunCommandAsync(serverPath, probe, cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
            {
                var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    return string.Join(" | ", lines.Take(2));
                }
            }
        }

        return "Unknown";
    }

    private static async Task<string> BuildHardwareSummaryAsync(CancellationToken cancellationToken)
    {
        var output = await RunCommandAsync(
            "nvidia-smi",
            "--query-gpu=name,memory.total,memory.used,driver_version --format=csv,noheader,nounits",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(output) || output.Contains("not recognized", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU telemetry unavailable (nvidia-smi not found).";
        }

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var gpuCount = 0;
        var totalMb = 0.0;
        var usedMb = 0.0;
        var primaryName = "Unknown";
        var driver = "Unknown";

        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            gpuCount++;
            if (gpuCount == 1)
            {
                primaryName = parts[0];
                driver = parts[3];
            }

            if (double.TryParse(parts[1], out var total))
            {
                totalMb += total;
            }

            if (double.TryParse(parts[2], out var used))
            {
                usedMb += used;
            }
        }

        if (gpuCount == 0)
        {
            return "GPU telemetry unavailable (no NVIDIA adapters reported).";
        }

        var totalGb = Math.Round(totalMb / 1024.0, 1);
        var usedGb = Math.Round(usedMb / 1024.0, 1);
        var freeGb = Math.Round(Math.Max(0.0, totalGb - usedGb), 1);

        var driverAdvice = "Driver recommendation: verify latest game-ready/studio driver in NVIDIA App for your GPU.";
        if (int.TryParse(driver.Split('.').FirstOrDefault(), out var major) && major < 560)
        {
            driverAdvice = "Driver recommendation: installed driver appears older; check NVIDIA App for newer performance/stability updates.";
        }

        return $"GPU: {(gpuCount == 1 ? primaryName : $"{gpuCount}x NVIDIA adapters ({primaryName} primary)")} | VRAM free {freeGb} GB / {totalGb} GB | Driver {driver} | {RuntimeInformation.OSDescription} | {driverAdvice}";
    }

    private static string RunCommandSync(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        return (output + Environment.NewLine + error).Trim();
    }

    private static async Task<string> RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            var completed = await Task.WhenAny(waitTask, timeoutTask);
            if (completed != waitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            var combined = (output + Environment.NewLine + error).Trim();

            return string.IsNullOrWhiteSpace(combined) ? "Unknown" : combined;
        }
        catch (Exception ex)
        {
            return $"Not Found ({ex.GetType().Name})";
        }
    }
}
