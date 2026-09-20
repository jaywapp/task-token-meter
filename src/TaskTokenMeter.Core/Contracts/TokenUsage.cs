namespace TaskTokenMeter.Core.Contracts;

public sealed record TokenUsage(
    long? InputTotal = null,
    long? UncachedInput = null,
    long? CacheRead = null,
    long? CacheWrite = null,
    long? CacheWrite5m = null,
    long? CacheWrite1h = null,
    long? Output = null,
    long? Reasoning = null,
    long? ProcessedTokens = null,
    long? NativeTotal = null);
