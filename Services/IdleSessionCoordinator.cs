using System;
using System.Threading;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Tracks Client-app chat completions so idle unload cannot race a long reply.
/// </summary>
public sealed class IdleSessionCoordinator
{
    private readonly object _gate = new();
    private int _inFlight;
    private bool _unloadBusy;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public int InFlightChatCount
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    public DateTime LastActivityUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastActivityUtc;
            }
        }
    }

    public void NoteActivity()
    {
        lock (_gate)
        {
            _lastActivityUtc = DateTime.UtcNow;
        }
    }

    public IDisposable BeginChat()
    {
        lock (_gate)
        {
            while (_unloadBusy)
            {
                Monitor.Wait(_gate, TimeSpan.FromSeconds(60));
            }

            _inFlight++;
            _lastActivityUtc = DateTime.UtcNow;
            SystemSleepGuard.Acquire();
        }

        return new ChatSession(this);
    }

    public bool TryStartUnload(TimeSpan idleFor)
    {
        lock (_gate)
        {
            if (_inFlight > 0 || _unloadBusy)
            {
                return false;
            }

            if (DateTime.UtcNow - _lastActivityUtc < idleFor)
            {
                return false;
            }

            _unloadBusy = true;
            return true;
        }
    }

    public void EndUnload()
    {
        lock (_gate)
        {
            _unloadBusy = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void EndChat()
    {
        lock (_gate)
        {
            if (_inFlight > 0)
            {
                _inFlight--;
            }

            _lastActivityUtc = DateTime.UtcNow;
            SystemSleepGuard.Release();
        }
    }

    private sealed class ChatSession : IDisposable
    {
        private IdleSessionCoordinator? _owner;

        public ChatSession(IdleSessionCoordinator owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndChat();
        }
    }
}
