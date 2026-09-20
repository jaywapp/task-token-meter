using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;

namespace TaskTokenMeter.Core.Projection;

public sealed record TurnProjection(
    RootTurnKey TurnKey,
    long Revision,
    TokenUsage Usage,
    long? KnownSubtotal,
    int UnknownObservationCount,
    int? ApiCallCount,
    long? MaxObservedInput,
    ExecutionState ExecutionState,
    MeasurementQuality MeasurementQuality,
    RootUsageScope RootScope,
    IReadOnlyList<Membership> Membership,
    IReadOnlyList<SourceCompleteness> Sources,
    IReadOnlyList<string> Diagnostics,
    DateTimeOffset? ObservedAt);

public sealed record ProjectionBatch(
    IReadOnlyList<TurnProjection> Turns,
    IReadOnlyList<UnattributedObservation> Unattributed,
    IReadOnlyList<ExcludedExecution> Excluded,
    IReadOnlyList<string> Diagnostics);
