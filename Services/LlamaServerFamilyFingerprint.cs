using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

public sealed class LlamaServerFamily
{
    public string Backend { get; init; } = "unknown";
    public string? CudaMajor { get; init; }
    public string? CudaExact { get; init; }
    public bool Ambiguous { get; init; }
    public string Summary { get; init; } = string.Empty;

    public bool CanMatch =>
        !Ambiguous
        && (Backend is "vulkan" or "hip" or "cpu"
            || (Backend == "cuda" && (CudaExact is not null || CudaMajor is not null)));
}

/// <summary>
/// Reads the llama-server folder already in use. Does not guess a CUDA family when the folder is unclear.
/// </summary>
public static class LlamaServerFamilyFingerprint
{
    private static readonly Regex FolderCudaExact = new(
        @"win-cuda-(\d+\.\d+)-x64",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FolderCudaMajor = new(
        @"win-cuda-(\d+)(?:-x64|\.\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LlamaServerFamily FromInstallDirectory(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new LlamaServerFamily
            {
                Backend = "unknown",
                Summary = "llama-server.exe is not set. Choose it on the Servers tab first."
            };
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? string.Empty;
        var names = ListFileNames(directory);
        var pathBlob = directory + " " + (Directory.GetParent(directory)?.Name ?? string.Empty);

        var folderBackend = BackendFromPath(pathBlob, out var folderCudaExact, out var folderCudaMajor);
        var fileBackend = BackendFromFiles(names, out var fileCudaMajor);
        var folderExact = folderCudaExact ?? ExactCudaFromPath(pathBlob);

        if (folderBackend is not null
            && fileBackend is not null
            && !folderBackend.Equals(fileBackend, StringComparison.Ordinal))
        {
            return new LlamaServerFamily
            {
                Backend = "unknown",
                Ambiguous = true,
                Summary = "This llama-server folder name and the files inside it do not agree on CUDA vs Vulkan vs CPU. Confirm by hand which zip this PC should use."
            };
        }

        var backend = folderBackend ?? fileBackend;
        if (backend is null)
        {
            return new LlamaServerFamily
            {
                Backend = "unknown",
                Summary = "Could not tell whether this llama-server folder is CUDA, Vulkan, HIP, or CPU. The check will not guess, so a different zip cannot overwrite it. Keep the unzipped GitHub folder name, or extract the matching CUDA DLLs zip into this folder."
            };
        }

        string? cudaExact = folderExact;
        string? cudaMajor = folderCudaMajor ?? fileCudaMajor;
        if (cudaExact is not null)
        {
            cudaMajor = cudaExact.Split('.')[0];
        }

        if (backend == "cuda"
            && fileCudaMajor is not null
            && cudaMajor is not null
            && !fileCudaMajor.Equals(cudaMajor, StringComparison.Ordinal))
        {
            return new LlamaServerFamily
            {
                Backend = "unknown",
                Ambiguous = true,
                Summary = "This folder looks like CUDA " + cudaMajor + " from its name, but the DLLs look like CUDA " + fileCudaMajor
                    + ". Confirm which zip matches this GPU before updating."
            };
        }

        if (backend == "cuda" && cudaMajor is null && cudaExact is null)
        {
            return new LlamaServerFamily
            {
                Backend = "cuda",
                Summary = "This folder looks like a CUDA llama-server, but the CUDA 12 vs 13 zip is not clear. Confirm the zip family by hand."
            };
        }

        var summary = backend switch
        {
            "cuda" when cudaExact is not null =>
                "This install looks like Windows CUDA " + cudaExact + ".",
            "cuda" =>
                "This install looks like Windows CUDA " + cudaMajor + " (minor version not in the folder name).",
            "vulkan" => "This install looks like Windows Vulkan.",
            "hip" => "This install looks like Windows HIP.",
            "cpu" => "This install looks like Windows CPU (no GPU zip).",
            _ => "This llama-server family is unclear."
        };

        return new LlamaServerFamily
        {
            Backend = backend,
            CudaMajor = cudaMajor,
            CudaExact = cudaExact,
            Summary = summary
        };
    }

    public static bool AssetMatchesFamily(string assetName, LlamaServerFamily family)
    {
        if (string.IsNullOrWhiteSpace(assetName) || !family.CanMatch)
        {
            return false;
        }

        var name = assetName.ToLowerInvariant();
        if (!name.Contains("win", StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal))
        {
            return false;
        }

        return family.Backend switch
        {
            "cuda" => MatchesCuda(name, family),
            "vulkan" => name.Contains("win-vulkan", StringComparison.Ordinal) && name.Contains("x64", StringComparison.Ordinal),
            "hip" => name.Contains("win-hip", StringComparison.Ordinal) && name.Contains("x64", StringComparison.Ordinal),
            "cpu" => name.Contains("win-cpu", StringComparison.Ordinal) && name.Contains("x64", StringComparison.Ordinal)
                     && !name.Contains("cuda", StringComparison.Ordinal)
                     && !name.Contains("vulkan", StringComparison.Ordinal),
            _ => false
        };
    }

    public static bool IsLlamaServerZip(string assetName)
    {
        var name = assetName.ToLowerInvariant();
        return name.StartsWith("llama-b", StringComparison.Ordinal)
            && name.Contains("bin-win", StringComparison.Ordinal)
            && name.EndsWith(".zip", StringComparison.Ordinal)
            && !name.Contains("cudart", StringComparison.Ordinal);
    }

    public static bool IsCudartZip(string assetName)
    {
        var name = assetName.ToLowerInvariant();
        return name.Contains("cudart", StringComparison.Ordinal)
            && name.Contains("win", StringComparison.Ordinal)
            && name.EndsWith(".zip", StringComparison.Ordinal);
    }

    private static bool MatchesCuda(string name, LlamaServerFamily family)
    {
        if (!name.Contains("cuda", StringComparison.Ordinal) || !name.Contains("x64", StringComparison.Ordinal))
        {
            return false;
        }

        if (family.CudaExact is not null)
        {
            return name.Contains("cuda-" + family.CudaExact, StringComparison.Ordinal);
        }

        if (family.CudaMajor is not null)
        {
            return Regex.IsMatch(name, @"cuda-" + Regex.Escape(family.CudaMajor) + @"(\.|-)", RegexOptions.IgnoreCase);
        }

        return false;
    }

    private static string? BackendFromPath(string pathBlob, out string? cudaExact, out string? cudaMajor)
    {
        cudaExact = ExactCudaFromPath(pathBlob);
        cudaMajor = null;
        var lower = pathBlob.ToLowerInvariant();
        if (lower.Contains("win-vulkan", StringComparison.Ordinal))
        {
            return "vulkan";
        }

        if (lower.Contains("win-hip", StringComparison.Ordinal) || lower.Contains("win-roc", StringComparison.Ordinal))
        {
            return "hip";
        }

        if (lower.Contains("win-cpu", StringComparison.Ordinal))
        {
            return "cpu";
        }

        if (cudaExact is not null)
        {
            cudaMajor = cudaExact.Split('.')[0];
            return "cuda";
        }

        var majorMatch = FolderCudaMajor.Match(pathBlob);
        if (majorMatch.Success)
        {
            cudaMajor = majorMatch.Groups[1].Value;
            return "cuda";
        }

        return null;
    }

    private static string? ExactCudaFromPath(string pathBlob)
    {
        var match = FolderCudaExact.Match(pathBlob);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? BackendFromFiles(IReadOnlyList<string> names, out string? cudaMajor)
    {
        cudaMajor = null;
        var lower = names.Select(name => name.ToLowerInvariant()).ToList();
        if (lower.Any(name => name.Contains("ggml-vulkan", StringComparison.Ordinal) || name == "vulkan-1.dll"))
        {
            return "vulkan";
        }

        if (lower.Any(name => name.Contains("ggml-hip", StringComparison.Ordinal) || name.Contains("amdhip", StringComparison.Ordinal)))
        {
            return "hip";
        }

        if (lower.Any(name => name.Contains("ggml-cuda", StringComparison.Ordinal)
            || name.StartsWith("cublas64_", StringComparison.Ordinal)
            || name.StartsWith("cudart64_", StringComparison.Ordinal)))
        {
            if (lower.Any(name => name.StartsWith("cublas64_13", StringComparison.Ordinal) || name.StartsWith("cudart64_13", StringComparison.Ordinal)))
            {
                cudaMajor = "13";
            }
            else if (lower.Any(name => name.StartsWith("cublas64_12", StringComparison.Ordinal) || name.StartsWith("cudart64_12", StringComparison.Ordinal)))
            {
                cudaMajor = "12";
            }
            else if (lower.Any(name => name.StartsWith("cudart64_11", StringComparison.Ordinal) || name.StartsWith("cublas64_11", StringComparison.Ordinal)))
            {
                cudaMajor = "11";
            }

            return "cuda";
        }

        return null;
    }

    private static List<string> ListFileNames(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
