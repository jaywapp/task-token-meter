using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Core.Identity;

public sealed record RootTurnKey
{
    public RootTurnKey(ProviderKind provider, string rootSessionId, string rootTurnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootTurnId);

        Provider = provider;
        RootSessionId = rootSessionId;
        RootTurnId = rootTurnId;
    }

    public ProviderKind Provider { get; }

    public string RootSessionId { get; }

    public string RootTurnId { get; }
}

public sealed record ExecutionIdentity
{
    public ExecutionIdentity(ProviderKind provider, string originSessionId, string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        Provider = provider;
        OriginSessionId = originSessionId;
        ExecutionId = executionId;
    }

    public ProviderKind Provider { get; }

    public string OriginSessionId { get; }

    public string ExecutionId { get; }
}
