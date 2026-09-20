using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Adapters.Codex;

public enum CodexUsageKind
{
    CallDelta,
    TurnSnapshot,
    SessionSnapshot
}

public enum CodexRootScope
{
    MainOnly,
    ChildInclusive,
    Unknown
}

public enum CodexAttributionStatus
{
    Attributed,
    AttributedAlreadyInRoot,
    Provisional,
    Unattributed,
    Invalid
}

public sealed record CodexAdapterCapability(
    string ProviderVersion,
    string RecordShapeVersion,
    CodexRootScope RootScope,
    bool IsVerified)
{
    public static CodexAdapterCapability V01534 { get; } = new(
        "0.153.4",
        "token_usage_record-v0.153.4",
        CodexRootScope.MainOnly,
        true);

    public IReadOnlyList<CodexUsageKind> UsageKinds { get; } =
        [CodexUsageKind.CallDelta, CodexUsageKind.TurnSnapshot, CodexUsageKind.SessionSnapshot];

    public string TurnIdentity => IsVerified ? "rollout-turn-id" : "unsupported";
    public string CallIdentity => IsVerified ? "response-id" : "unsupported";
    public bool? TurnSnapshotAuthority => IsVerified ? true : null;
    public string RootSessionSource => IsVerified ? "record-session-id" : "unknown";
    public string CacheWriteSemantics => IsVerified ? "included-unknown" : "unknown";
    public string InterruptEvidence => IsVerified ? "hook" : "none";
    public string ForkReplayIdentity => IsVerified ? "origin-id" : "unknown";
    public string HookNeutralResponseVersion => $"codex-hooks-v{ProviderVersion}";

    public static CodexAdapterCapability Verified(
        string providerVersion,
        string recordShapeVersion,
        CodexRootScope rootScope) =>
        new(providerVersion, recordShapeVersion, rootScope, true);

    public static CodexAdapterCapability Unsupported(
        string providerVersion,
        string recordShapeVersion) =>
        new(providerVersion, recordShapeVersion, CodexRootScope.Unknown, false);
}

public sealed record CodexNativeUsage(
    long? InputTokens = null,
    long? CachedInputTokens = null,
    long? OutputTokens = null,
    long? ReasoningOutputTokens = null,
    long? TotalTokens = null,
    long? CacheWriteInputTokens = null)
{
    public bool IsTotalOnly =>
        TotalTokens is not null &&
        InputTokens is null &&
        CachedInputTokens is null &&
        OutputTokens is null &&
        ReasoningOutputTokens is null &&
        CacheWriteInputTokens is null;

    public TokenUsage ToTokenUsage()
    {
        var uncachedInput = InputTokens is not null && CachedInputTokens is not null
            ? (long?)checked(InputTokens.Value - CachedInputTokens.Value)
            : null;
        var processed = InputTokens is not null && OutputTokens is not null
            ? (long?)checked(InputTokens.Value + OutputTokens.Value)
            : null;

        return new TokenUsage(
            InputTotal: InputTokens,
            UncachedInput: uncachedInput,
            CacheRead: CachedInputTokens,
            CacheWrite: CacheWriteInputTokens,
            Output: OutputTokens,
            Reasoning: ReasoningOutputTokens,
            ProcessedTokens: processed,
            NativeTotal: TotalTokens);
    }
}

public sealed record CodexLineage(
    string? RootSessionId,
    string? RootTurnId,
    string? ThreadId,
    string? TurnId,
    string? ParentThreadId,
    string? ForkedFromThreadId,
    string? ResponseId,
    string? OriginExecutionId);

public sealed record CodexUsageObservation(
    CodexUsageKind Kind,
    CodexLineage Lineage,
    CodexNativeUsage Usage,
    long Sequence);

public sealed record CodexMembership(
    string ExecutionId,
    string OriginSessionId,
    CodexAttributionStatus AttributionStatus,
    string Evidence);

public sealed record CodexTurnResult(
    string RootSessionId,
    string RootTurnId,
    CodexNativeUsage? NativeUsage,
    CodexNativeUsage? RootObservedUsage,
    IReadOnlyList<CodexNativeUsage> ChildObservedUsage,
    TokenUsage Usage,
    MeasurementQuality Quality,
    int? ApiCallCount,
    long? MaxObservedInput,
    long? KnownSubtotal,
    int UnknownObservationCount,
    IReadOnlyList<CodexMembership> Membership,
    IReadOnlyList<string> Diagnostics,
    string? ObservedAt)
{
    public TurnSnapshot ToTurnSnapshot() => new(
        "codex",
        RootSessionId,
        RootTurnId,
        Usage,
        Quality,
        ApiCallCount,
        MaxObservedInput,
        ObservedAt);
}

public sealed record CodexUnattributedObservation(
    CodexLineage Lineage,
    CodexNativeUsage Usage,
    MeasurementQuality Quality,
    IReadOnlyList<string> Diagnostics);

public sealed record CodexExcludedExecution(
    string ExecutionId,
    string SessionId,
    string ThreadId,
    string TurnId,
    string Reason);

public sealed record CodexReadResult(
    CodexAdapterCapability Capability,
    IReadOnlyList<CodexTurnResult> Turns,
    IReadOnlyList<CodexUsageObservation> Observations,
    IReadOnlyList<CodexUnattributedObservation> UnattributedObservations,
    IReadOnlyList<CodexExcludedExecution> ExcludedExecutions,
    IReadOnlyList<string> Diagnostics,
    bool IsSupported)
{
    public IReadOnlyList<TurnSnapshot> ToTurnSnapshots() =>
        Turns.Select(static turn => turn.ToTurnSnapshot()).ToArray();
}

public sealed class CodexUnsupportedFormatException : IOException
{
    public CodexUnsupportedFormatException(string message)
        : base(message)
    {
    }
}
