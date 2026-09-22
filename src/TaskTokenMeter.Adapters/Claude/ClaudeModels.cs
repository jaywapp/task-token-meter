using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Adapters.Claude;

public enum ClaudeReadStatus
{
    Supported,
    Partial,
    Invalid,
    Unsupported
}

public enum ClaudeAttributionStatus
{
    Attributed,
    Unattributed,
    Invalid
}

public enum ClaudeExecutionState
{
    Unknown,
    Completed,
    Failed
}

public sealed record ClaudeAdapterCapabilities(
    string ProviderVersion,
    string RecordShapeVersion,
    string TurnIdentity,
    string CallIdentity,
    string RootSessionSource,
    string RootScope,
    string CacheWriteSemantics,
    string InterruptEvidence,
    string ForkReplayIdentity,
    string HookNeutralResponseVersion);

public sealed record ClaudeNativeUsage(
    long? InputTokens,
    long? CacheCreationInputTokens,
    long? CacheReadInputTokens,
    long? OutputTokens,
    long? CacheWrite5m,
    long? CacheWrite1h,
    long? ThinkingTokens);

public sealed record ClaudeMembership(
    string ExecutionId,
    string OriginSessionId,
    ClaudeAttributionStatus AttributionStatus,
    string Evidence,
    string? ParentSessionId = null,
    string? AgentId = null);

public sealed record ClaudeTurnObservation(
    string RootSessionId,
    string RootTurnId,
    ClaudeNativeUsage? NativeUsage,
    TokenUsage Usage,
    MeasurementQuality Quality,
    int? ApiCallCount,
    long? MaxObservedInput,
    long? KnownSubtotal,
    int UnknownObservationCount,
    IReadOnlyList<ClaudeMembership> Membership,
    ClaudeExecutionState ExecutionState,
    IReadOnlyList<string> Diagnostics,
    string? ObservedAt = null,
    string? WorkspaceRoot = null);

public sealed record ClaudeUnattributedObservation(
    ClaudeNativeUsage NativeUsage,
    TokenUsage Usage,
    ClaudeMembership Membership,
    MeasurementQuality Quality,
    IReadOnlyList<string> Diagnostics);

public sealed record ClaudeReadResult(
    ClaudeReadStatus Status,
    ClaudeAdapterCapabilities Capabilities,
    IReadOnlyList<ClaudeTurnObservation> Turns,
    IReadOnlyList<ClaudeUnattributedObservation> Unattributed,
    IReadOnlyList<string> Diagnostics);

public sealed class ClaudeSourceException(string message) : IOException(message);

