using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.HailMary;

namespace FluxMux.Avalonia.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Client-app actions a rescue adapter names but the generic engine cannot do: stopping and
    /// reopening the Harness web UI ("harness-web"). Stop also waits until Harness has released
    /// the chat's write lock, so the chat file is never written while a Harness process owns it.
    /// </summary>
    private sealed class HailMaryRescueHost : IRescueHost
    {
        private readonly MainViewModel _vm;

        public HailMaryRescueHost(MainViewModel vm) => _vm = vm;

        public async Task<bool> RunNamedActionAsync(string kind, bool reopen, RescueSession session, CancellationToken cancellationToken)
        {
            if (!kind.Equals(RescueAdapterCatalog.HarnessWebAction, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return reopen
                ? await _vm.ReopenHarnessOnChatAsync(session.SessionId, cancellationToken).ConfigureAwait(false)
                : await _vm.StopHarnessForRescueAsync(session.SessionDir, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> StopHarnessForRescueAsync(string sessionDir, CancellationToken cancellationToken)
    {
        var port = (int)HarnessWebPort;
        _harnessStartCts?.Cancel();
        _harnessWebStopIntentional = true;
        try
        {
            _harnessWebHost.Stop(port);
            if (DeepSeekHarnessWebHost.IsPortListening(port))
            {
                // A Harness web the user started outside AI-FluxMux: stop whatever owns the port.
                DeepSeekHarnessWebHost.TryStopPortListener(port, out _);
            }

            await DeepSeekHarnessWebHost.WaitForPortClosedAsync(port, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtimeService.Supervisor?.LogLaunchMonitor("[HailMary] stopping Harness web failed: " + ex.Message);
        }

        _harnessLaunchedByThisFluxMuxSession = false;
        _harnessStartedUtc = null;
        Dispatcher.UIThread.Post(RefreshHarnessProcessState);

        // The process holding the chat can outlive the web port briefly (or be a Harness started
        // in a terminal); wait for its lock to go before touching the file.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DeepSeekHarnessSetup.IsHarnessSessionLocked(sessionDir) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        var released = !DeepSeekHarnessSetup.IsHarnessSessionLocked(sessionDir);
        _runtimeService.Supervisor?.LogLaunchMonitor("[HailMary] Harness chat lock " + (released ? "released" : "still held — another Harness has this chat open"));
        return released;
    }

    private async Task<bool> ReopenHarnessOnChatAsync(string sessionId, CancellationToken cancellationToken)
    {
        var port = (int)HarnessWebPort;
        var start = _harnessWebHost.TryStart(port);
        if (!start.Success)
        {
            _runtimeService.Supervisor?.LogLaunchMonitor("[HailMary] Harness restart failed: " + start.Message);
            return false;
        }

        _harnessLaunchedByThisFluxMuxSession = true;
        _harnessStartedUtc = DateTimeOffset.UtcNow;
        _harnessWebStopIntentional = false;
        var ready = await DeepSeekHarnessWebHost.WaitForWebUiAsync(port, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        // Harness's web page does not take a chat from its address (a "?session=" link opens a new
        // blank chat). It reopens the chat that was open last, which it keeps in the browser's own
        // storage for this address and port — the stuck chat — so open the plain page.
        _ = sessionId;
        var url = DeepSeekHarnessSetup.ResolveOpenChatUrl(port);
        Dispatcher.UIThread.Post(() =>
        {
            RefreshHarnessProcessState();
            if (ready)
            {
                OpenExternalLink(url);
            }
        });
        return ready;
    }
}
