using System.Diagnostics;
using TaskTokenMeter.Cli.Selection;
using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Cli.Hooks;

public interface IHookAggregationRuntime
{
    Task AggregateAsync(HookInvocationContext context, CancellationToken cancellationToken);
}

public sealed class AdapterHookAggregationRuntime(string? globalDataRoot = null) : IHookAggregationRuntime
{
    public async Task AggregateAsync(HookInvocationContext context, CancellationToken cancellationToken)
    {
        var provider = context.Provider.ToProviderKind();
        var sources = new Dictionary<ProviderKind, IReadOnlyList<string>>
        {
            [provider] = context.TranscriptPaths
        };
        var runtime = new AdapterCliRuntime(globalDataRoot, sourceRoots: sources);
        var candidate = new SessionCandidate(provider, context.SessionId, context.WorkspacePath, null);
        await runtime.SyncAsync(candidate, context.WorkspacePath, cancellationToken).ConfigureAwait(false);
    }
}

public static class HookWorker
{
    public static async Task<int> RunAsync(
        HookWorkerRequest request,
        Func<IHookAggregationRuntime> runtimeFactory,
        IHookDiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await runtimeFactory().AggregateAsync(request.Context, timeout.Token).ConfigureAwait(false);
                    return 0;
                }
                catch (InvalidOperationException) when (attempt == 0)
                {
                    // Recreate the runtime so the registry route and generation are read again.
                }
            }

            diagnostics.Record("hook_route_conflict", request.Context.Provider, request.Context.EventName, started.Elapsed);
        }
        catch (OperationCanceledException)
        {
            diagnostics.Record("hook_worker_timeout", request.Context.Provider, request.Context.EventName, started.Elapsed);
        }
        catch (Exception)
        {
            diagnostics.Record("hook_worker_failed", request.Context.Provider, request.Context.EventName, started.Elapsed);
        }

        return 0;
    }
}
