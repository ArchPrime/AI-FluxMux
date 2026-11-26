using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// The "can't continue" rescue for any Client app described by a <see cref="RescueAdapterDefinition"/>
/// (Harness, or one a user added in configuration). Prepare finds the stuck chat and takes the summary
/// and keep count from <see cref="CompactionContext.Plan"/> (the chat as the Client app last sent it,
/// sized by <see cref="RescuePlanner"/>), or without one builds the summary from the session note
/// (VIBE_STATE + essence) and keeps the adapter's fixed number. No model call either way, which is
/// why it works when the chat is too large for the model. Commit runs the adapter's steps. The
/// built-in Cline rescue keeps <see cref="ExternalRescueStrategy"/>, whose writer must match Cline's hash.
/// </summary>
public sealed class ConfiguredRescueStrategy : ICompactionStrategy
{
    private readonly RescueAdapterDefinition _adapter;
    private readonly IFidelitySource _fidelity;
    private readonly IRescueHost? _host;
    private readonly string? _scriptsFolder;
    private RescueSession? _session;
    private int? _keepRecent;

    public ConfiguredRescueStrategy(
        RescueAdapterDefinition adapter,
        IRescueHost? host,
        IFidelitySource? fidelity = null,
        string? scriptsFolder = null)
    {
        _adapter = adapter;
        _host = host;
        _fidelity = fidelity ?? new VibeStatePlusMessagesFidelitySource();
        _scriptsFolder = scriptsFolder;
    }

    public string Name => "rescue:" + _adapter.Id;

    public string DisplayName => _adapter.Label;

    /// <summary>The chat the last prepare found.</summary>
    public RescueSession? Session => _session;

    public async Task<CompactionResult> ExecuteAsync(CompactionContext ctx)
    {
        ctx.LogLine("[HailMary] strategy=" + Name + ": preparing (root=" + _adapter.Find.Root + ")");
        _session = RescueEngine.FindSession(_adapter.Find);
        if (_session is null)
        {
            return CompactionResult.Fail("Could not find a chat to rebuild for " + string.Join(", ", _adapter.ClientApps)
                + " (looked in " + System.Environment.ExpandEnvironmentVariables(_adapter.Find.Root) + ").");
        }

        if (ctx.Plan is { } plan)
        {
            _keepRecent = plan.KeepRecent;
            ctx.LogLine("[HailMary] strategy=" + Name + ": chat " + _session.SessionId + ", summary from the chat as the Client app last sent it (" + plan.Describe() + ")");
            return new CompactionResult(true, "Ready to rebuild chat " + _session.SessionId + ": " + plan.Describe() + ".", SummaryPreview: plan.Summary);
        }

        _keepRecent = null;
        var snapshot = await _fidelity.BuildAsync(new CompactionContext
        {
            VibeStatePath = ctx.VibeStatePath,
            EssencePath = ctx.EssencePath,
            Log = ctx.Log,
            TargetFreeFraction = ctx.TargetFreeFraction,
            PromptTokens = ctx.PromptTokens,
            ContextLimit = ctx.ContextLimit,
        }).ConfigureAwait(false);
        if (snapshot.IsEmpty)
        {
            return CompactionResult.Fail("AI-FluxMux has no session note for this chat yet (VIBE_STATE and essence are empty), so there is nothing to rebuild it from.");
        }

        var summary = snapshot.ToClineSummary();
        ctx.LogLine("[HailMary] strategy=" + Name + ": chat " + _session.SessionId + " (" + _session.SessionFile + "), summary " + summary.Length + " chars");
        return new CompactionResult(
            Success: true,
            Message: "Ready to rebuild chat " + _session.SessionId + ": the older part becomes this summary and the last "
                + _adapter.KeepRecent + " messages are kept. " + (_adapter.Stop.IsNone ? string.Empty : "The Client app is stopped first and reopened on the same chat."),
            SummaryPreview: summary);
    }

    public async Task<CompactionResult> CommitAsync(CompactionContext ctx, CompactionResult prepared)
    {
        if (!prepared.Success || string.IsNullOrWhiteSpace(prepared.SummaryPreview) || _session is null)
        {
            return CompactionResult.Fail("Nothing prepared to rebuild.");
        }

        var run = await RescueEngine.RunAsync(_adapter, _session, prepared.SummaryPreview, _host, _scriptsFolder, CancellationToken.None, _keepRecent).ConfigureAwait(false);
        foreach (var line in run.Log)
        {
            ctx.LogLine("[HailMary] " + Name + ": " + line);
        }

        return run.Success
            ? new CompactionResult(true, run.Message, _session.SessionFile, prepared.SummaryPreview)
            : CompactionResult.Fail(run.Message);
    }
}
