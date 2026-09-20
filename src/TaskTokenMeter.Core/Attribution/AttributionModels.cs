using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;
using TaskTokenMeter.Core.Usage;

namespace TaskTokenMeter.Core.Attribution;

public enum RootUsageScope
{
    MainOnly,
    ChildInclusive,
    Unknown
}

public enum AttributionStatus
{
    Attributed,
    AttributedAlreadyInRoot,
    Provisional,
    Unattributed,
    Invalid
}

public enum ExecutionState
{
    Unknown,
    Running,
    Completed,
    Interrupted,
    Failed
}

public enum SourceAvailability
{
    Available,
    Pending,
    Missing,
    Truncated,
    Unreadable,
    Unsupported
}

public sealed record SourceCompleteness
{
    public SourceCompleteness(
        string sourceId,
        SourceAvailability availability,
        bool isComplete,
        IReadOnlyList<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        SourceId = sourceId;
        Availability = availability;
        IsComplete = isComplete;
        Diagnostics = diagnostics ?? [];
    }

    public string SourceId { get; }

    public SourceAvailability Availability { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public static SourceCompleteness Complete(string sourceId) =>
        new(sourceId, SourceAvailability.Available, true);
}

public sealed record ExecutionObservation
{
    public ExecutionObservation(
        ExecutionIdentity identity,
        string observedSessionId,
        string observedExecutionId,
        RootTurnKey? rootTurn,
        bool isRootExecution,
        RootUsageScope rootScope,
        UsageNormalizationResult usage,
        ExecutionState executionState,
        SourceCompleteness sourceCompleteness,
        string evidence,
        long revision = 0,
        DateTimeOffset? observedAt = null,
        bool isReplay = false,
        bool attributionIsAmbiguous = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedExecutionId);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(sourceCompleteness);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        if (rootTurn is not null && rootTurn.Provider != identity.Provider)
        {
            throw new ArgumentException("Root Turn and execution providers must match.", nameof(rootTurn));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        Identity = identity;
        ObservedSessionId = observedSessionId;
        ObservedExecutionId = observedExecutionId;
        RootTurn = rootTurn;
        IsRootExecution = isRootExecution;
        RootScope = rootScope;
        Usage = usage;
        ExecutionState = executionState;
        SourceCompleteness = sourceCompleteness;
        Evidence = evidence;
        Revision = revision;
        ObservedAt = observedAt;
        IsReplay = isReplay;
        AttributionIsAmbiguous = attributionIsAmbiguous;
    }

    public ExecutionIdentity Identity { get; }

    public string ObservedSessionId { get; }

    public string ObservedExecutionId { get; }

    public RootTurnKey? RootTurn { get; }

    public bool IsRootExecution { get; }

    public RootUsageScope RootScope { get; }

    public UsageNormalizationResult Usage { get; }

    public ExecutionState ExecutionState { get; }

    public SourceCompleteness SourceCompleteness { get; }

    public string Evidence { get; }

    public long Revision { get; }

    public DateTimeOffset? ObservedAt { get; }

    public bool IsReplay { get; }

    public bool AttributionIsAmbiguous { get; }
}

public sealed record Membership(
    RootTurnKey RootTurn,
    ExecutionIdentity Execution,
    AttributionStatus Status,
    string Evidence,
    bool IsIncludedInAggregate);

public sealed record AttributedExecution(
    ExecutionObservation Observation,
    Membership Membership);

public sealed record UnattributedObservation(
    ExecutionObservation Observation,
    AttributionStatus Status,
    string Reason,
    MeasurementQuality Quality);

public sealed record ExcludedExecution(
    ExecutionIdentity Identity,
    string ObservedSessionId,
    string ObservedExecutionId,
    string Reason);

public sealed record AttributedRootGroup(
    RootTurnKey RootTurn,
    RootUsageScope RootScope,
    IReadOnlyList<AttributedExecution> Executions,
    IReadOnlyList<string> Diagnostics);

public sealed record AttributionResult(
    IReadOnlyList<AttributedRootGroup> Roots,
    IReadOnlyList<UnattributedObservation> Unattributed,
    IReadOnlyList<ExcludedExecution> Excluded,
    IReadOnlyList<string> Diagnostics);
