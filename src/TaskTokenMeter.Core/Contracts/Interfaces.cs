namespace TaskTokenMeter.Core.Contracts;

public interface ISessionDiscovery
{
    /// <summary>
    /// Lists session candidates, optionally filtered by provider and/or a resolved session ID.
    /// </summary>
    /// <param name="workspace">
    /// A canonical workspace root (see <c>TaskTokenMeter.Core.Identity.WorkspaceRoot</c>). When set, a
    /// candidate whose provider log records a *different, known* workspace is excluded. A candidate
    /// whose workspace could not be determined from the log is kept rather than hidden — an unknown
    /// workspace is not evidence of a mismatch.
    /// </param>
    IReadOnlyList<SessionCandidate> Discover(string? provider, string? sessionId, string? workspace = null);
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
