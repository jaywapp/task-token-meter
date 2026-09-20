using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Storage.Routing;

namespace TaskTokenMeter.Storage.Migration;

public enum StorageMigrationPhase
{
    Planned,
    BackupCompleted,
    DestinationCommitted,
    RouteSwitched,
    Completed
}

public enum StorageMigrationFaultPoint
{
    AfterPlanJournal,
    AfterBackup,
    BeforeDestinationCommit,
    AfterDestinationCommit,
    BeforeRouteSwitch,
    AfterRouteSwitch,
    BeforeJournalComplete
}

public enum StorageMigrationOutcome
{
    Migrated,
    AlreadyActive,
    DryRun
}

public sealed record StorageStatus(
    Guid WorkspaceId,
    string CanonicalWorkspaceRoot,
    StorageRoute ActiveRoute,
    string DatabasePath,
    PurgePreview ActiveRecords,
    IReadOnlyList<StorageLineageEntry> SupersededStores);

public sealed record StorageMigrationRequest(
    string WorkspaceRoot,
    StorageMode DestinationMode,
    string? GlobalDataRoot = null,
    bool DryRun = false);

public sealed record StorageMigrationPlan(
    Guid WorkspaceId,
    StorageRoute SourceRoute,
    StorageRoute DestinationRoute,
    PurgePreview SourceRecords,
    bool DestinationExists,
    bool DestinationHasWorkspaceRecords,
    bool DestinationIsVerifiedLineage,
    GitExcludePlan GitExclude,
    string SourceManifestHash);

public sealed record StorageMigrationResult(
    StorageMigrationOutcome Outcome,
    StorageMigrationPlan Plan,
    StorageRoute ActiveRoute,
    string? MigrationId,
    StorageMigrationPhase? Phase);

public interface IStorageMigrationFaultInjector
{
    Task OnFaultPointAsync(StorageMigrationFaultPoint point, CancellationToken cancellationToken);
}

public sealed class NoStorageMigrationFaultInjector : IStorageMigrationFaultInjector
{
    public Task OnFaultPointAsync(StorageMigrationFaultPoint point, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class StorageMigrationConflictException(string message) : InvalidOperationException(message);

public sealed class StorageMigrationValidationException(string message) : IOException(message);
