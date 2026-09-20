namespace TaskTokenMeter.Core.Contracts;

public sealed record StorageRoute(
    Guid WorkspaceId,
    StorageMode Mode,
    string CanonicalDataRoot,
    long RouteGeneration,
    string ActiveStoreId);
