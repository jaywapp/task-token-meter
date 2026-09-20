using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskTokenMeter.Adapters.Claude;
using TaskTokenMeter.Adapters.Codex;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Storage;
using TaskTokenMeter.Storage.Migration;
using TaskTokenMeter.Storage.Routing;

namespace TaskTokenMeter.Cli;

public sealed class AdapterCliRuntime : ICliRuntime
{
    private readonly IReadOnlyDictionary<ProviderKind, IUsageAdapter> adapters;
    private readonly IReadOnlyDictionary<ProviderKind, IReadOnlyList<string>>? sourceRoots;
    private readonly string globalDataRoot;
    private readonly FileStorageLockManager lockManager;
    private readonly FileStorageRouteRegistry registry;
    private readonly StorageMigrationService migrationService;

    public AdapterCliRuntime(
        string? globalDataRoot = null,
        IReadOnlyDictionary<ProviderKind, IUsageAdapter>? adapters = null,
        IReadOnlyDictionary<ProviderKind, IReadOnlyList<string>>? sourceRoots = null)
    {
        this.adapters = adapters ?? new Dictionary<ProviderKind, IUsageAdapter>
        {
            [ProviderKind.Claude] = new ClaudeAdapter(),
            [ProviderKind.Codex] = new CodexUsageAdapter()
        };
        this.sourceRoots = sourceRoots;
        this.globalDataRoot = CanonicalPath(globalDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskTokenMeter"));
        var stateRoot = Path.Combine(this.globalDataRoot, "state");
        lockManager = new FileStorageLockManager(Path.Combine(stateRoot, "locks"));
        registry = new FileStorageRouteRegistry(Path.Combine(stateRoot, "storage-routes.json"), lockManager);
        migrationService = new StorageMigrationService(
            registry, lockManager, this.globalDataRoot, Path.Combine(stateRoot, "migration"));
    }

    public IReadOnlyList<SessionCandidate> Discover(string? provider, string? sessionId)
    {
        var turns = ReadAll().ToArray();
        return turns
            .Where(item => provider is null || string.Equals(ProviderName(item.TurnKey.Provider), provider, StringComparison.OrdinalIgnoreCase))
            .Select(static item => new SessionCandidate(item.TurnKey.Provider, item.TurnKey.RootSessionId, null, item.ObservedAt))
            .Where(item => sessionId is null || string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
            .Distinct()
            .Concat(provider is not null && sessionId is not null && !turns.Any(item =>
                string.Equals(ProviderName(item.TurnKey.Provider), provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.TurnKey.RootSessionId, sessionId, StringComparison.Ordinal))
                ? [new SessionCandidate(ToProvider(provider), sessionId, null, null)]
                : [])
            .GroupBy(static item => (item.Provider, item.SessionId))
            .Select(static group => group.OrderByDescending(item => item.LastObservedAt).First())
            .ToArray();
    }

    public async Task<IReadOnlyList<TurnProjection>> ReadTurnsAsync(
        SessionCandidate session,
        string workspace,
        CancellationToken cancellationToken)
    {
        var sourceTurns = ReadSession(session).ToArray();
        if (sourceTurns.Length > 0)
        {
            return sourceTurns;
        }

        var route = await EnsureRouteAsync(workspace, cancellationToken).ConfigureAwait(false);
        await using var store = SqliteLedgerStore.FromRoute(route, routeAuthority: registry);
        var stored = await store.LoadSessionAsync(
            route.WorkspaceId, ProviderName(session.Provider), session.SessionId, cancellationToken).ConfigureAwait(false);
        return stored.Select(ToStoredFallback).ToArray();
    }

    public async Task<IReadOnlyList<TurnProjection>> SyncAsync(
        SessionCandidate session,
        string workspace,
        CancellationToken cancellationToken)
    {
        var sourceTurns = ReadSession(session).ToArray();
        if (sourceTurns.Length == 0)
        {
            return await ReadTurnsAsync(session, workspace, cancellationToken).ConfigureAwait(false);
        }

        var route = await EnsureRouteAsync(workspace, cancellationToken).ConfigureAwait(false);
        await using var store = SqliteLedgerStore.FromRoute(route, routeAuthority: registry);
        await store.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var manifests = await BuildSourceSnapshotsAsync(store, route.WorkspaceId, session.Provider, sourceTurns, cancellationToken).ConfigureAwait(false);
        foreach (var turn in sourceTurns)
        {
            await CommitAsync(store, route, turn, manifests, cancellationToken).ConfigureAwait(false);
        }

        return sourceTurns;
    }

    public async Task<IReadOnlyList<TurnProjection>> RebuildAsync(
        SessionCandidate session,
        string workspace,
        CancellationToken cancellationToken)
    {
        var route = await EnsureRouteAsync(workspace, cancellationToken).ConfigureAwait(false);
        await using var store = SqliteLedgerStore.FromRoute(route, routeAuthority: registry);
        await store.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var sourceTurns = ReadSession(session).ToArray();
        if (sourceTurns.Length == 0)
        {
            return await ReadTurnsAsync(session, workspace, cancellationToken).ConfigureAwait(false);
        }

        var manifests = await BuildSourceSnapshotsAsync(store, route.WorkspaceId, session.Provider, sourceTurns, cancellationToken).ConfigureAwait(false);
        var rebuilt = new List<TurnProjection>();
        foreach (var turn in sourceTurns)
        {
            await CommitAsync(store, route, turn, manifests, cancellationToken).ConfigureAwait(false);
            rebuilt.Add(turn);
        }

        return rebuilt;
    }

    public async Task<StorageStatus> GetStorageStatusAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        await EnsureRouteAsync(workspace, cancellationToken).ConfigureAwait(false);
        return await migrationService.GetStatusAsync(CanonicalPath(workspace), cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageMigrationResult> MigrateStorageAsync(
        string workspace,
        StorageMode destination,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        await EnsureRouteAsync(workspace, cancellationToken).ConfigureAwait(false);
        return await migrationService.MigrateAsync(
            new StorageMigrationRequest(CanonicalPath(workspace), destination, destination == StorageMode.Global ? globalDataRoot : null, dryRun), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CommitAsync(
        SqliteLedgerStore store,
        StorageRoute route,
        TurnProjection turn,
        IReadOnlyList<SourceSnapshot> sources,
        CancellationToken cancellationToken)
    {
        var provider = ProviderName(turn.TurnKey.Provider);
        var stored = await store.LoadAsync(
            route.WorkspaceId, provider, turn.TurnKey.RootSessionId, turn.TurnKey.RootTurnId, cancellationToken)
            .ConfigureAwait(false);
        var candidate = turn with { Revision = stored is null ? 1 : stored.Projection.Revision };
        if (stored is not null && !SemanticEqual(stored.Projection, candidate))
        {
            candidate = candidate with { Revision = checked(stored.Projection.Revision + 1) };
        }

        var result = await store.CommitAsync(
            new LedgerCommitRequest(route, candidate, stored?.Projection.Revision ?? 0, sources),
            cancellationToken).ConfigureAwait(false);
        if (result.Status is LedgerCommitStatus.RevisionConflict or LedgerCommitStatus.SourceConflict or LedgerCommitStatus.RouteConflict)
        {
            throw new InvalidOperationException("The selected session changed while it was being synchronized.");
        }
    }

    private async Task<IReadOnlyList<SourceSnapshot>> BuildSourceSnapshotsAsync(
        SqliteLedgerStore store,
        Guid workspaceId,
        ProviderKind provider,
        IReadOnlyList<TurnProjection> turns,
        CancellationToken cancellationToken)
    {
        var previous = (await store.LoadSourcesAsync(workspaceId, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static source => source.SourceId, StringComparer.Ordinal);
        var completeness = turns.SelectMany(static turn => turn.Sources)
            .GroupBy(static source => source.SourceId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var capability = AdapterCapability(provider);
        return ExpandSourceFiles(GetSourceRoots(provider).Paths).Select(path =>
        {
            var sourceId = OpaqueSourceId(provider, path);
            var observed = completeness.TryGetValue(sourceId, out var source)
                ? source : SourceCompleteness.Complete(sourceId);
            var fingerprint = SourceFingerprint(capability, path);
            var extent = new FileInfo(path).Length;
            if (previous.TryGetValue(sourceId, out var stored) &&
                string.Equals(stored.ContentFingerprint, fingerprint, StringComparison.Ordinal) &&
                stored.ReadExtent == extent && stored.Availability == observed.Availability &&
                stored.IsComplete == observed.IsComplete)
            {
                return stored;
            }

            return new SourceSnapshot(sourceId, previous.TryGetValue(sourceId, out stored) ? checked(stored.Generation + 1) : 1,
                fingerprint, extent, observed.Availability, observed.IsComplete);
        }).ToArray();
    }

    private string AdapterCapability(ProviderKind provider) => adapters[provider] switch
    {
        ClaudeAdapter => "claude:" + ClaudeAdapter.SupportedProviderVersion,
        CodexUsageAdapter codex => "codex:" + codex.Capability.ProviderVersion,
        var adapter => provider.ToString().ToLowerInvariant() + ":" + adapter.GetType().Assembly.GetName().Version
    };

    private static string[] ExpandSourceFiles(IEnumerable<string> roots) => roots
        .Select(CanonicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
        .SelectMany(path => File.Exists(path) ? [path] : Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.jsonl", SearchOption.AllDirectories) : [])
        .Select(CanonicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string OpaqueSourceId(ProviderKind provider, string path) =>
        "source:" + ProviderName(provider) + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalPath(path))));

    private static string SourceFingerprint(string capability, string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(capability));
        hash.AppendData([0]);
        hash.AppendData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static SourceCompleteness[] SourceCompletenessFor(
        ProviderKind provider, IReadOnlyList<string> paths, IReadOnlyList<string> diagnostics)
    {
        var availability = diagnostics.Any(static diagnostic => diagnostic == "child_source_pending")
            ? SourceAvailability.Pending : SourceAvailability.Available;
        var complete = availability == SourceAvailability.Available && !diagnostics.Any(IsSourceIntegrityDiagnostic);
        return paths.Select(path => new SourceCompleteness(OpaqueSourceId(provider, path), availability, complete, diagnostics)).ToArray();
    }

    private static bool IsSourceIntegrityDiagnostic(string diagnostic) => diagnostic is
        "incomplete_tail" or "malformed_json_line" or "malformed_json_source" or "child_source_pending" or
        "session_only_usage_unsupported" or "unsupported_record_shape" or "no_token_usage_records";
    private async Task<StorageRoute> EnsureRouteAsync(string workspace, CancellationToken cancellationToken)
    {
        var canonicalWorkspace = CanonicalPath(workspace);
        var registered = await registry.GetByWorkspacePathAsync(canonicalWorkspace, cancellationToken).ConfigureAwait(false);
        if (registered is not null)
        {
            return registered.ActiveRoute;
        }

        return (await registry.InitializeAsync(
            canonicalWorkspace, StorageMode.Global, globalDataRoot, cancellationToken).ConfigureAwait(false)).ActiveRoute;
    }

    private IEnumerable<TurnProjection> ReadSession(SessionCandidate session) => ReadAll().Where(
        item => item.TurnKey.Provider == session.Provider && item.TurnKey.RootSessionId == session.SessionId);

    private IEnumerable<TurnProjection> ReadAll()
    {
        foreach (var pair in adapters)
        {
            var configured = GetSourceRoots(pair.Key);
            var paths = ExpandSourceFiles(configured.Paths);
            if (paths.Length == 0) continue;
            if (pair.Value is ClaudeAdapter claude)
            {
                var result = claude.ReadSnapshot(paths);
                if (result.Status == ClaudeReadStatus.Unsupported) throw new UnsupportedSourceException(pair.Key);
                foreach (var turn in result.Turns) yield return ToProjection(result, turn, SourceCompletenessFor(pair.Key, paths, result.Diagnostics));
                continue;
            }
            if (pair.Value is CodexUsageAdapter codex)
            {
                var result = codex.ReadDetailed(paths);
                if (!result.IsSupported) throw new UnsupportedSourceException(pair.Key);
                foreach (var turn in result.Turns) yield return ToProjection(result, turn, SourceCompletenessFor(pair.Key, paths, result.Diagnostics));
                continue;
            }
            var foundSupported = false;
            foreach (var path in paths)
            {
                if (!pair.Value.CanRead(path)) continue;
                foundSupported = true;
                foreach (var turn in pair.Value.Read(path)) yield return ToProjection(turn);
            }
            if (configured.IsExplicit && !foundSupported) throw new UnsupportedSourceException(pair.Key);
        }
    }

    private SourceRoots GetSourceRoots(ProviderKind provider)
    {
        if (sourceRoots is not null)
        {
            return sourceRoots.TryGetValue(provider, out var injected)
                ? new SourceRoots(injected, true)
                : new SourceRoots([], true);
        }

        var variable = "TOKEN_METER_" + provider.ToString().ToUpperInvariant() + "_SOURCES";
        var overridePaths = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(overridePaths))
        {
            return new SourceRoots(
                overridePaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), true);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultRoot = provider == ProviderKind.Claude
            ? Path.Combine(profile, ".claude", "projects")
            : Path.Combine(profile, ".codex", "sessions");
        return new SourceRoots(Directory.Exists(defaultRoot) ? [defaultRoot] : [], false);
    }
    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string ProviderName(ProviderKind provider) => provider.ToString().ToLowerInvariant();
    private static ProviderKind ToProvider(string provider) => provider.Equals("claude", StringComparison.OrdinalIgnoreCase) ? ProviderKind.Claude : ProviderKind.Codex;
    private static bool SemanticEqual(TurnProjection left, TurnProjection right) =>
        JsonSerializer.Serialize(left with { Revision = 0 }, ContractJson.Options) == JsonSerializer.Serialize(right with { Revision = 0 }, ContractJson.Options);
    private static string ProjectionHash(TurnProjection projection) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(projection, ContractJson.Options))));
    private static TurnProjection ToStoredFallback(StoredTurnProjection stored) => stored.Projection with
    {
        MeasurementQuality = stored.Projection.MeasurementQuality == MeasurementQuality.Observed ? MeasurementQuality.Provisional : stored.Projection.MeasurementQuality,
        Diagnostics = stored.Projection.Diagnostics.Append("stored_fallback").Distinct(StringComparer.Ordinal).ToArray()
    };
    private static TurnProjection ToProjection(TurnSnapshot snapshot) => new(
        new RootTurnKey(ToProvider(snapshot.Provider), snapshot.SessionId, snapshot.TurnId),
        1, snapshot.Usage, null, snapshot.Quality == MeasurementQuality.Observed ? 0 : 1, snapshot.ApiCallCount, snapshot.MaxObservedInput,
        ExecutionState.Unknown, snapshot.Quality, RootUsageScope.Unknown, [], [], [],
        DateTimeOffset.TryParse(snapshot.ObservedAt, out var observedAt) ? observedAt : null);

    private static TurnProjection ToProjection(ClaudeReadResult result, ClaudeTurnObservation turn, IReadOnlyList<SourceCompleteness> sources)
    {
        var rootTurn = new RootTurnKey(ProviderKind.Claude, turn.RootSessionId, turn.RootTurnId);
        return new TurnProjection(
            rootTurn, 1, turn.Usage, turn.KnownSubtotal, turn.UnknownObservationCount, turn.ApiCallCount, turn.MaxObservedInput,
            ToExecutionState(turn.ExecutionState), turn.Quality, RootUsageScope.MainOnly,
            turn.Membership.Select(membership => new Membership(rootTurn,
                new ExecutionIdentity(ProviderKind.Claude, membership.OriginSessionId, membership.ExecutionId),
                ToAttributionStatus(membership.AttributionStatus), membership.Evidence,
                membership.AttributionStatus == ClaudeAttributionStatus.Attributed)).ToArray(),
            sources,
            turn.Diagnostics.Concat(result.Diagnostics).Distinct(StringComparer.Ordinal).ToArray(),
            DateTimeOffset.TryParse(turn.ObservedAt, out var observedAt) ? observedAt : null);
    }

    private static TurnProjection ToProjection(CodexReadResult result, CodexTurnResult turn, IReadOnlyList<SourceCompleteness> sources)
    {
        var rootTurn = new RootTurnKey(ProviderKind.Codex, turn.RootSessionId, turn.RootTurnId);
        return new TurnProjection(
            rootTurn, 1, turn.Usage, turn.KnownSubtotal, turn.UnknownObservationCount, turn.ApiCallCount, turn.MaxObservedInput,
            ExecutionState.Unknown, turn.Quality, ToRootUsageScope(result.Capability.RootScope),
            turn.Membership.Select(membership => new Membership(rootTurn,
                new ExecutionIdentity(ProviderKind.Codex, membership.OriginSessionId, membership.ExecutionId),
                ToAttributionStatus(membership.AttributionStatus), membership.Evidence,
                membership.AttributionStatus == CodexAttributionStatus.Attributed)).ToArray(),
            sources,
            turn.Diagnostics.Concat(result.Diagnostics).Distinct(StringComparer.Ordinal).ToArray(),
            DateTimeOffset.TryParse(turn.ObservedAt, out var observedAt) ? observedAt : null);
    }

    private static ExecutionState ToExecutionState(ClaudeExecutionState state) => state switch
    {
        ClaudeExecutionState.Completed => ExecutionState.Completed,
        ClaudeExecutionState.Failed => ExecutionState.Failed,
        _ => ExecutionState.Unknown
    };

    private static AttributionStatus ToAttributionStatus(ClaudeAttributionStatus status) => status switch
    {
        ClaudeAttributionStatus.Attributed => AttributionStatus.Attributed,
        ClaudeAttributionStatus.Unattributed => AttributionStatus.Unattributed,
        ClaudeAttributionStatus.Invalid => AttributionStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static AttributionStatus ToAttributionStatus(CodexAttributionStatus status) => status switch
    {
        CodexAttributionStatus.Attributed => AttributionStatus.Attributed,
        CodexAttributionStatus.AttributedAlreadyInRoot => AttributionStatus.AttributedAlreadyInRoot,
        CodexAttributionStatus.Provisional => AttributionStatus.Provisional,
        CodexAttributionStatus.Unattributed => AttributionStatus.Unattributed,
        CodexAttributionStatus.Invalid => AttributionStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static RootUsageScope ToRootUsageScope(CodexRootScope scope) => scope switch
    {
        CodexRootScope.MainOnly => RootUsageScope.MainOnly,
        CodexRootScope.ChildInclusive => RootUsageScope.ChildInclusive,
        CodexRootScope.Unknown => RootUsageScope.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    private static bool IsCodexSourceIntegrityDiagnostic(string diagnostic) => diagnostic is
        "malformed_json_line" or "session_only_usage_unsupported" or "unsupported_record_shape" or "no_token_usage_records";
    private sealed record SourceRoots(IReadOnlyList<string> Paths, bool IsExplicit);
}

public sealed class UnsupportedSourceException(ProviderKind provider) : InvalidOperationException
{
    public ProviderKind Provider { get; } = provider;
}
