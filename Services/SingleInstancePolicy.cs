using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public enum SingleInstanceDecision
{
    TakeOwnership,
    ReplaceExisting
}

public readonly record struct SingleInstancePeer(int Pid, bool HasMainWindow, TimeSpan Age);

public static class SingleInstancePolicy
{
    public static SingleInstanceDecision Decide(bool createdNew, IReadOnlyList<SingleInstancePeer> peers)
    {
        if (createdNew)
        {
            return SingleInstanceDecision.TakeOwnership;
        }

        return SingleInstanceDecision.ReplaceExisting;
    }
}
