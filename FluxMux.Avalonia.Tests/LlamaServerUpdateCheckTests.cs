using System;
using System.IO;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LlamaServerUpdateCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fluxmux-llama-fam-" + Guid.NewGuid().ToString("n"));

    public LlamaServerUpdateCheckTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Build_number_reads_tag_and_version_text()
    {
        Assert.Equal(7001, LlamaCppBuildNumber.TryParse("b7001"));
        Assert.Equal(5800, LlamaCppBuildNumber.TryParse("version: 1234 (build 5800)"));
        Assert.Null(LlamaCppBuildNumber.TryParse("no build here"));
    }

    [Fact]
    public void Fingerprint_uses_cuda_dlls_when_folder_name_is_generic()
    {
        var exe = MakeInstall("llama-server", "ggml-cuda.dll", "cublas64_12.dll");
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(exe);
        Assert.True(family.CanMatch);
        Assert.Equal("cuda", family.Backend);
        Assert.Equal("12", family.CudaMajor);
        Assert.Null(family.CudaExact);
    }

    [Fact]
    public void Fingerprint_reads_exact_cuda_from_github_folder_name()
    {
        var exe = MakeInstall("llama-b1234-bin-win-cuda-12.4-x64");
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(exe);
        Assert.True(family.CanMatch);
        Assert.Equal("cuda", family.Backend);
        Assert.Equal("12.4", family.CudaExact);
        Assert.Equal("12", family.CudaMajor);
    }

    [Fact]
    public void Fingerprint_reads_vulkan_from_folder_and_files()
    {
        var exe = MakeInstall("llama-b1234-bin-win-vulkan-x64", "ggml-vulkan.dll");
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(exe);
        Assert.True(family.CanMatch);
        Assert.Equal("vulkan", family.Backend);
    }

    [Fact]
    public void Fingerprint_does_not_guess_cuda_major_from_ggml_cuda_alone()
    {
        var exe = MakeInstall("llama-server", "ggml-cuda.dll");
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(exe);
        Assert.False(family.CanMatch);
        Assert.Equal("cuda", family.Backend);
        Assert.Contains("CUDA 12 vs 13", family.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_skips_when_folder_name_and_dlls_disagree()
    {
        var exe = MakeInstall("llama-b1234-bin-win-vulkan-x64", "cublas64_12.dll");
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(exe);
        Assert.True(family.Ambiguous);
        Assert.False(family.CanMatch);
    }

    [Fact]
    public void Fingerprint_skips_when_folder_cuda_major_disagrees_with_dlls()
    {
        var exe = MakeInstall("llama-b1234-bin-win-cuda-12.4-x64", "ggml-cuda.dll", "cublas64_13.dll");
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(exe);
        Assert.True(family.Ambiguous);
        Assert.False(family.CanMatch);
        Assert.Contains("CUDA 12", family.Summary, StringComparison.Ordinal);
        Assert.Contains("CUDA 13", family.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Matcher_picks_same_cuda_minor_zip_and_skips_vulkan()
    {
        var family = new LlamaServerFamily
        {
            Backend = "cuda",
            CudaExact = "12.4",
            CudaMajor = "12",
            Summary = "test"
        };
        var releases = LlamaCppReleaseMatcher.ParseReleases(
            """
            [
              {
                "tag_name": "b8000",
                "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b8000",
                "assets": [
                  {
                    "name": "llama-b8000-bin-win-vulkan-x64.zip",
                    "browser_download_url": "https://example.com/vulkan.zip"
                  },
                  {
                    "name": "llama-b8000-bin-win-cuda-12.4-x64.zip",
                    "browser_download_url": "https://example.com/cuda124.zip"
                  },
                  {
                    "name": "cudart-llama-bin-win-cuda-12.4-x64.zip",
                    "browser_download_url": "https://example.com/cudart124.zip"
                  },
                  {
                    "name": "llama-b8000-bin-win-cuda-13.0-x64.zip",
                    "browser_download_url": "https://example.com/cuda130.zip"
                  }
                ]
              }
            ]
            """);

        var match = LlamaCppReleaseMatcher.FindNewerMatching(releases, family, installedBuild: 7000);
        Assert.NotNull(match);
        Assert.Equal(8000, match!.RemoteBuild);
        Assert.False(match.MultipleCudaMinors);
        Assert.Equal("llama-b8000-bin-win-cuda-12.4-x64.zip", match.ServerZipName);
        Assert.Equal("https://example.com/cuda124.zip", match.ServerZipUrl);
        Assert.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", match.CudartZipName);
        Assert.DoesNotContain("vulkan", match.ServerZipName, StringComparison.Ordinal);
    }

    [Fact]
    public void Matcher_does_not_pick_a_cuda_minor_when_several_match_major_only()
    {
        var family = new LlamaServerFamily
        {
            Backend = "cuda",
            CudaMajor = "12",
            Summary = "test"
        };
        var releases = LlamaCppReleaseMatcher.ParseReleases(
            """
            [
              {
                "tag_name": "b8001",
                "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b8001",
                "assets": [
                  {
                    "name": "llama-b8001-bin-win-cuda-12.4-x64.zip",
                    "browser_download_url": "https://example.com/cuda124.zip"
                  },
                  {
                    "name": "llama-b8001-bin-win-cuda-12.8-x64.zip",
                    "browser_download_url": "https://example.com/cuda128.zip"
                  }
                ]
              }
            ]
            """);

        var match = LlamaCppReleaseMatcher.FindNewerMatching(releases, family, installedBuild: 7000);
        Assert.NotNull(match);
        Assert.True(match!.MultipleCudaMinors);
        Assert.Null(match.ServerZipUrl);
        Assert.Equal("https://github.com/ggml-org/llama.cpp/releases/tag/b8001", match.ReleaseUrl);
    }

    [Fact]
    public void Matcher_reports_current_when_installed_build_is_newest()
    {
        var family = new LlamaServerFamily
        {
            Backend = "vulkan",
            Summary = "test"
        };
        var releases = LlamaCppReleaseMatcher.ParseReleases(
            """
            [
              {
                "tag_name": "b7100",
                "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b7100",
                "assets": [
                  {
                    "name": "llama-b7100-bin-win-vulkan-x64.zip",
                    "browser_download_url": "https://example.com/vulkan.zip"
                  }
                ]
              }
            ]
            """);

        var match = LlamaCppReleaseMatcher.FindNewerMatching(releases, family, installedBuild: 7100);
        Assert.NotNull(match);
        Assert.Equal(7100, match!.RemoteBuild);
        Assert.Null(match.ServerZipUrl);
        Assert.False(match.MultipleCudaMinors);
    }

    private string MakeInstall(string folderName, params string[] files)
    {
        var dir = Path.Combine(_root, folderName);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "llama-server.exe");
        File.WriteAllBytes(exe, [0]);
        foreach (var file in files)
        {
            File.WriteAllBytes(Path.Combine(dir, file), [0]);
        }

        return exe;
    }
}
