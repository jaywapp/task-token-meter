using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Core.Usage;

public sealed record UsageNormalizationResult(
    IReadOnlyList<UsageObservation> NativeObservations,
    TokenUsage Usage,
    long? KnownSubtotal,
    int UnknownObservationCount,
    int? ApiCallCount,
    long? MaxObservedInput,
    MeasurementQuality Quality,
    IReadOnlyList<string> Diagnostics);
