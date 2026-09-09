using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Holds Windows system sleep off while at least one chat completion is in flight,
/// including long agent turns. Display sleep is not forced on. Lid-close can still sleep.
/// </summary>
internal static class SystemSleepGuard
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Wake = new(false);
    private static int _holders;
    private static Thread? _thread;
    private static volatile bool _stop;

    public static void Acquire()
    {
        lock (Gate)
        {
            _holders++;
            EnsureThread();
            Wake.Set();
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_holders > 0)
            {
                _holders--;
            }

            Wake.Set();
        }
    }

    private static void EnsureThread()
    {
        if (_thread is { IsAlive: true })
        {
            return;
        }

        _stop = false;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AI-FluxMux-sleep-guard"
        };
        _thread.Start();
    }

    private static void Loop()
    {
        var holding = false;
        try
        {
            while (!_stop)
            {
                bool want;
                lock (Gate)
                {
                    want = _holders > 0;
                }

                if (want)
                {
                    SetThreadExecutionState(EsContinuous | EsSystemRequired);
                    holding = true;
                }
                else if (holding)
                {
                    SetThreadExecutionState(EsContinuous);
                    holding = false;
                }

                Wake.Reset();
                Wake.Wait(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            SetThreadExecutionState(EsContinuous);
        }
    }
}
