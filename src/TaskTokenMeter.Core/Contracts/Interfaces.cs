namespace TaskTokenMeter.Core.Contracts;

public interface ISessionDiscovery
{
    IReadOnlyList<SessionCandidate> Discover(string? provider, string? sessionId);
}

public interface IUsageAdapter
{
    ProviderKind Provider { get; }
    bool CanRead(string sourcePath);
    IReadOnlyList<TurnSnapshot> Read(string sourcePath);
}

public interface IStorageRouter
{
    StorageRoute Resolve(Guid workspaceId, StorageMode mode);
}

public interface ILedgerRepository
{
    Task SaveAsync(MeterSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<MeterSnapshot?> LoadAsync(string provider, string sessionId, CancellationToken cancellationToken = default);
}
