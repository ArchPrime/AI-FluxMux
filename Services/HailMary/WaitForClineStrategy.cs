using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Stage 0 strategy (the existing behavior): wait for Cline to become idle, then trigger Cline's
/// own /smol command via UI Automation. This wraps the existing ClineSmolTrigger so the Hail Mary
/// flow goes through the strategy seam without changing the underlying logic.
/// </summary>
public sealed class WaitForClineStrategy : ICompactionStrategy
{
    public string Name => "wait-for-cline";
    public string DisplayName => "Wait for Cline to become ready, then send /smol";

    public async Task<CompactionResult> ExecuteAsync(CompactionContext ctx)
    {
        ctx.LogLine("[HailMary] strategy=" + Name + ": waiting for Cline idle, then sending /smol");
        // Run on a thread-pool thread: TriggerSmol contains Thread.Sleep calls.
        var result = await Task.Run(() => ClineSmolTrigger.TriggerSmol()).ConfigureAwait(false);
        ctx.LogLine("[HailMary] strategy=" + Name + " result: " + (result.Success ? "SUCCESS" : "FAILED") + " — " + result.Message);
        return result.Success ? CompactionResult.Ok(result.Message) : CompactionResult.Fail(result.Message);
    }

    // The wait-for-cline path does everything in ExecuteAsync; commit is a no-op.
    public Task<CompactionResult> CommitAsync(CompactionContext ctx, CompactionResult prepared)
    {
        return Task.FromResult(prepared);
    }
}
