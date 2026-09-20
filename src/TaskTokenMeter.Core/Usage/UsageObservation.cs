using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Core.Usage;

public enum UsageObservationKind
{
    Unknown,
    ApiCall,
    Snapshot
}

public sealed record NativeTokenUsage(
    long? InputTotal = null,
    long? CacheRead = null,
    long? CacheWrite = null,
    long? CacheWrite5m = null,
    long? CacheWrite1h = null,
    long? Output = null,
    long? Reasoning = null,
    long? NativeTotal = null);

public sealed record UsageObservation(
    ProviderKind Provider,
    NativeTokenUsage NativeUsage,
    string? StableCallId = null,
    UsageObservationKind Kind = UsageObservationKind.Unknown);
