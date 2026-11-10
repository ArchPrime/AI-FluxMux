# YaRN Launch Hang — Diagnosis Findings (2026-11-07)

## TL;DR
The 109s/169s "cold start" was NOT a model-load limitation. It was **two launches competing
for the same GPU**. When a new launch supersedes an in-flight launch, the old llama-server
process was NOT killed — it kept running, kept loading the model, and kept holding GPU memory.
The new launch's llama-server then fought the old one for VRAM, and both loaded slowly.

## Smoking gun (llama_launch_telemetry.log, 10/15 13:21)
```
13:21:30 launch:start variant=max VRAM only context 2
13:21:34 launch:start variant=YaRN test with vision   ← 4s later, OVERLAPPING
13:21:35 process:start attempt=1   (VRAM-only)
13:21:39 process:start attempt=1   (YaRN)
...
13:24:25 ready total=169372 ms     (YaRN took 169s — fighting VRAM-only for GPU)
13:24:33 launch:start variant=max VRAM only context 2
13:24:50 ready total=13103 ms      (VRAM-only took 13s — GPU finally free)
```

Two launches started 4s apart. The YaRN launch took 169s because it was fighting the
VRAM-only launch for GPU memory. When the VRAM-only launch finally won, it loaded in 13s.

## Root cause
`SupersedeInProgressLocalLaunch` cancelled the previous launch's CTS (stopping its polling
loop) but did NOT kill the previous llama-server process. The old process kept running,
kept loading the model, and kept holding GPU memory. The new launch's llama-server then
competed with the old one for VRAM.

## Fix (APPLIED)
In `SupersedeInProgressLocalLaunch`, when superseding a launch, **kill the previous
llama-server process** so the new launch has the GPU to itself.

File: `FluxMuxRuntimeService.cs` line 3505-3523
```csharp
// CRITICAL: cancelling the CTS only stops the previous launch's polling loop.
// The llama-server process it started is STILL RUNNING, still loading the model,
// and still holding GPU memory. If we don't kill it here, the new launch's
// llama-server will compete with the old one for GPU VRAM, and BOTH will load
// slowly. Kill the old process so the new launch has the GPU to itself.
try
{
    Process? oldProcess = _localServerProcess;
    if (oldProcess != null && !oldProcess.HasExited)
    {
        LogLaunchMonitor($"supersede: killing previous llama-server (PID {oldProcess.Id})");
        oldProcess.Kill(entireProcessTree: true);
        _localServerProcess = null;
    }
}
catch { }
```

## Other improvements (APPLIED)
1. **Probe timeout 30s→60s** (line 244): prevents the probe from timing out on cold starts.
2. **Launch budget headroom 120s→40s** (line 7816): makes failures surface faster.
3. **Prewarm buffer 8MB→64MB** (line 10249): speeds up the prewarm.
4. **Prewarm progress indicator** (MainViewModel.cs line 9189): shows the user what's happening.

## Files
- `FluxMuxRuntimeService.cs` line 3505: supersede-kill (the real fix)
- `FluxMuxRuntimeService.cs` line 244: `PublicEndpointReadyProbeTimeoutMs = 60000`
- `FluxMuxRuntimeService.cs` line 7816: launch budget headroom (40s for YaRN)
- `FluxMuxRuntimeService.cs` line 10249: prewarm buffer (64MB)
- `MainViewModel.cs` line 9189: prewarm progress indicator
