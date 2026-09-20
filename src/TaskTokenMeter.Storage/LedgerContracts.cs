using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Projection;

namespace TaskTokenMeter.Storage;

public enum LedgerCommitStatus
{
    Committed,
    Unchanged,
    Preserved,
    RevisionConflict,
    SourceConflict,
    RouteConflict
}

public enum LedgerFaultPoint
{
    BeforeTransaction,
    AfterTransactionStarted,
    AfterRouteValidated,
    AfterSourcesWritten,
    AfterProjectionWritten,
    BeforeCommit,
    AfterCommit
}

public enum SchemaMigrationFaultPoint
{
    AfterStepApplied,
    BeforeCommit
}

public sealed record SourceSnapshot
{
    public SourceSnapshot(
        string sourceId,
        long generation,
        string? contentFingerprint,
        long? readExtent,
        SourceAvailability availability,
        bool isComplete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(readExtent ?? 0);

        if (availability == SourceAvailability.Available && string.IsNullOrWhiteSpace(contentFingerprint))
        {
            throw new ArgumentException("Available sources require a content fingerprint.", nameof(contentFingerprint));
        }

        SourceId = sourceId;
        Generation = generation;
        ContentFingerprint = contentFingerprint;
        ReadExtent = readExtent;
        Availability = availability;
        IsComplete = isComplete;
    }

    public string SourceId { get; }
    public long Generation { get; }
    public string? ContentFingerprint { get; }
    public long? ReadExtent { get; }
    public SourceAvailability Availability { get; }
    public bool IsComplete { get; }

    public bool IsAuthoritative =>
        Availability == SourceAvailability.Available && IsComplete;
}

public sealed record LedgerCommitRequest(
    StorageRoute Route,
    TurnProjection Projection,
    long ExpectedRevision,
    IReadOnlyList<SourceSnapshot> Sources)
{
    public LedgerCommitRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Route);
        ArgumentNullException.ThrowIfNull(Projection);
        ArgumentNullException.ThrowIfNull(Sources);
        ArgumentOutOfRangeException.ThrowIfNegative(ExpectedRevision);

        if (Sources.Count == 0)
        {
            throw new ArgumentException("At least one source snapshot is required.", nameof(Sources));
        }

        if (Sources.Select(static source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != Sources.Count)
        {
            throw new ArgumentException("Source IDs must be unique within a commit.", nameof(Sources));
        }

        return this;
    }
}

public sealed record LedgerCommitResult(
    LedgerCommitStatus Status,
    long CurrentRevision,
    string? DiagnosticCode = null);

public sealed record StoredTurnProjection(
    Guid WorkspaceId,
    TurnProjection Projection,
    string ProjectionHash,
    DateTimeOffset StoredAt);

public sealed record LedgerDiagnostic(
    string Code,
    string ScopeKey,
    int Count,
    DateTimeOffset OccurredAt);

public sealed record PurgePreview(
    Guid WorkspaceId,
    int TurnCount,
    int SourceCount,
    int DiagnosticCount);

public sealed record LedgerPurgeRequest(
    StorageRoute Route,
    bool IncludeDiagnostics = true);

public sealed record DiagnosticRetentionOptions
{
    public static DiagnosticRetentionOptions Default { get; } = new();

    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(14);
    public long MaximumApproximateBytes { get; init; } = 10 * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        }

        if (MaximumApproximateBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumApproximateBytes));
        }
    }
}

public sealed record SqliteLedgerOptions
{
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumRetryDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(25);
    public DiagnosticRetentionOptions DiagnosticRetention { get; init; } = DiagnosticRetentionOptions.Default;

    internal void Validate()
    {
        if (BusyTimeout < TimeSpan.Zero || MaximumRetryDuration < TimeSpan.Zero || RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SqliteLedgerOptions), "Timeouts cannot be negative.");
        }

        DiagnosticRetention.Validate();
    }
}

public interface ILedgerStore
{
    Task<LedgerCommitResult> CommitAsync(
        LedgerCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<StoredTurnProjection?> LoadAsync(
        Guid workspaceId,
        string provider,
        string rootSessionId,
        string rootTurnId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredTurnProjection>> LoadSessionAsync(
        Guid workspaceId,
        string provider,
        string rootSessionId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LedgerDiagnostic>> GetDiagnosticsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<PurgePreview> PreviewPurgeAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<PurgePreview> PurgeAsync(
        LedgerPurgeRequest request,
        CancellationToken cancellationToken = default);
}

public interface IStorageRouteAuthority
{
    Task<bool> IsActiveAsync(
        StorageRoute route,
        CancellationToken cancellationToken = default);
}

public interface IWorkspaceWriteCoordinator
{
    Task<IAsyncDisposable> AcquireWorkspaceWriteAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}

public interface ILedgerDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface ILedgerFaultInjector
{
    Task OnLedgerFaultPointAsync(LedgerFaultPoint point, CancellationToken cancellationToken);

    Task OnSchemaMigrationFaultPointAsync(
        SchemaMigrationFaultPoint point,
        int targetVersion,
        CancellationToken cancellationToken);
}

internal sealed class SystemLedgerDelay : ILedgerDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal sealed class NoLedgerFaultInjector : ILedgerFaultInjector
{
    public Task OnLedgerFaultPointAsync(LedgerFaultPoint point, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task OnSchemaMigrationFaultPointAsync(
        SchemaMigrationFaultPoint point,
        int targetVersion,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
