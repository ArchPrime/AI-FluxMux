using System;
using System.Reflection;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Tests for the unified local-launch failure classification that drives the
/// recovery dialog (heading, message, and which buttons are shown).
/// </summary>
public sealed class LocalLaunchFailureClassificationTests
{
    private static readonly MethodInfo Classify =
        typeof(MainViewModel).GetMethod(
            "ClassifyLocalLaunchFailure",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(Classify));

    private static readonly Type KindType =
        typeof(MainViewModel).GetNestedType(
            "LocalLaunchFailureKind",
            BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LocalLaunchFailureKind not found");

    private static object Kind(string name) => Enum.Parse(KindType, name);

    private static object ClassifyResult(string status, string details)
    {
        var result = new RuntimeActionResult
        {
            IsSuccess = false,
            Status = status,
            Details = details,
        };
        return Classify.Invoke(null, new object[] { result });
    }

    [Fact]
    public void OomDetails_ClassifyAsOom()
    {
        Assert.Equal(Kind("Oom"), ClassifyResult("Local launch failed", "out of memory"));
        Assert.Equal(Kind("Oom"), ClassifyResult("Local launch failed", "cuda_error_out_of_memory"));
        Assert.Equal(Kind("Oom"), ClassifyResult("Local launch failed", "failed to allocate"));
        Assert.Equal(Kind("Oom"), ClassifyResult("Local launch failed", "ggml_gallocr"));
    }

    [Fact]
    public void PortInUseStatus_ClassifyAsPortInUse()
    {
        Assert.Equal(Kind("PortInUse"), ClassifyResult("Local port already in use", "A compatible listener is already responding on daemon port 8081."));
        Assert.Equal(Kind("PortInUse"), ClassifyResult("Local switch blocked", "Previous llama-server did not release daemon port 8081."));
    }

    [Fact]
    public void DaemonPortAlreadyInDetails_ClassifyAsPortInUse()
    {
        Assert.Equal(Kind("PortInUse"), ClassifyResult("Local launch failed", "daemon port 8081 is already in use by another process."));
    }

    [Fact]
    public void CrashOrTimeout_ClassifyAsCrashedOrTimeout()
    {
        Assert.Equal(Kind("CrashedOrTimeout"), ClassifyResult("Local launch failed", "llama-server exited during startup."));
        Assert.Equal(Kind("CrashedOrTimeout"), ClassifyResult("Local launch timeout", "llama-server did not finish launching within its previous manual launch timeframe."));
        Assert.Equal(Kind("CrashedOrTimeout"), ClassifyResult("Local launch still in progress", "llama-server is still running but is not ready yet."));
        Assert.Equal(Kind("CrashedOrTimeout"), ClassifyResult("Local launch failed", "llama-server executable not found at resolved path."));
    }

    [Fact]
    public void OomTakesPrecedenceOverPortInUse()
    {
        // If the details mention both OOM and a port, OOM wins (it's the more actionable cause).
        Assert.Equal(Kind("Oom"), ClassifyResult("Local port already in use", "out of memory on daemon port 8081."));
    }
}