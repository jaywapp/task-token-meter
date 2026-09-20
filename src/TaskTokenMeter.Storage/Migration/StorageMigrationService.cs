using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Storage.Routing;

namespace TaskTokenMeter.Storage.Migration;

public sealed class StorageMigrationService
{
    private readonly IStorageRouteRegistry registry;
    private readonly IStorageLockManager lockManager;
    private readonly IGitExcludeManager gitExcludeManager;
    private readonly IStorageFileSystem fileSystem;
    private readonly IStorageMigrationFaultInjector faultInjector;
    private readonly string defaultGlobalDataRoot;
    private readonly string migrationStateRoot;
    private readonly TimeProvider timeProvider;

    public StorageMigrationService(
        IStorageRouteRegistry registry,
        IStorageLockManager lockManager,
        string defaultGlobalDataRoot,
        string migrationStateRoot,
        IGitExcludeManager? gitExcludeManager = null,
        IStorageFileSystem? fileSystem = null,
        IStorageMigrationFaultInjector? faultInjector = null,
        TimeProvider? timeProvider = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultGlobalDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationStateRoot);
        this.fileSystem = fileSystem ?? new PhysicalStorageFileSystem();
        this.gitExcludeManager = gitExcludeManager ?? new GitExcludeManager(this.fileSystem);
        this.faultInjector = faultInjector ?? new NoStorageMigrationFaultInjector();
        this.defaultGlobalDataRoot = CanonicalPath(defaultGlobalDataRoot);
        this.migrationStateRoot = CanonicalPath(migrationStateRoot);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StorageStatus> GetStatusAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var registered = await GetRequiredRegistrationAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        var databasePath = DatabasePath(registered.ActiveRoute);
        var snapshot = await ReadSnapshotIfPresentAsync(
            databasePath, registered.ActiveRoute.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return new StorageStatus(
            registered.ActiveRoute.WorkspaceId,
            registered.CanonicalWorkspaceRoot,
            registered.ActiveRoute,
            databasePath,
            snapshot?.Preview ?? EmptyPreview(registered.ActiveRoute.WorkspaceId),
            registered.SupersededStores);
    }

    public async Task<StorageMigrationPlan> PlanAsync(
        StorageMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registered = await GetRequiredRegistrationAsync(request.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        return await BuildPlanAsync(request, registered, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageMigrationResult> MigrateAsync(
        StorageMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var initial = await GetRequiredRegistrationAsync(request.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        await using var workspaceLock = await lockManager.AcquireWorkspaceAsync(
            initial.ActiveRoute.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var registered = await GetRequiredRegistrationAsync(request.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        var destinationRoot = ResolveDestinationRoot(request, registered.CanonicalWorkspaceRoot);
        if (registered.ActiveRoute.Mode == request.DestinationMode &&
            FileStorageRouteRegistry.PathsEqual(registered.ActiveRoute.CanonicalDataRoot, destinationRoot))
        {
            await CompleteJournalAfterObservedSwitchAsync(registered.ActiveRoute, cancellationToken).ConfigureAwait(false);
            var activePlan = await BuildPlanAsync(request, registered, cancellationToken).ConfigureAwait(false);
            return new StorageMigrationResult(
                StorageMigrationOutcome.AlreadyActive,
                activePlan,
                registered.ActiveRoute,
                null,
                StorageMigrationPhase.Completed);
        }

        var plan = await BuildPlanAsync(request, registered, cancellationToken).ConfigureAwait(false);
        if (request.DryRun)
        {
            return new StorageMigrationResult(
                StorageMigrationOutcome.DryRun,
                plan,
                plan.SourceRoute,
                null,
                null);
        }

        if (request.DestinationMode == StorageMode.Workspace)
        {
            await gitExcludeManager.EnsureExcludedAsync(registered.CanonicalWorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);
        }

        ProbeDestinationWritable(plan.DestinationRoute.CanonicalDataRoot);
        var existingJournal = ReadJournal(plan.WorkspaceId);
        var journal = existingJournal is null || existingJournal.Phase == StorageMigrationPhase.Completed
            ? MigrationJournal.Create(plan, timeProvider.GetUtcNow())
            : existingJournal;
        ValidateJournal(journal, plan);
        WriteJournal(journal);
        await faultInjector.OnFaultPointAsync(StorageMigrationFaultPoint.AfterPlanJournal, cancellationToken)
            .ConfigureAwait(false);

        var sourceDatabasePath = DatabasePath(plan.SourceRoute);
        if (!fileSystem.FileExists(sourceDatabasePath))
        {
            throw new StorageMigrationValidationException("The active source ledger does not exist.");
        }

        fileSystem.CreateDirectory(BackupRoot);
        var backupPath = Path.Combine(BackupRoot, $"{journal.MigrationId}.db");
        await BackupDatabaseAsync(sourceDatabasePath, backupPath, cancellationToken).ConfigureAwait(false);
        var sourceSnapshot = await ReadSnapshotAsync(backupPath, plan.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (sourceSnapshot.Route is null ||
            !RouteMatchesStore(sourceSnapshot.Route, plan.SourceRoute))
        {
            throw new StorageRouteConflictException(plan.WorkspaceId);
        }

        ValidateSnapshot(sourceSnapshot);
        journal.SourceManifestHash = sourceSnapshot.ManifestHash;
        journal.BackupPath = backupPath;
        journal.Phase = StorageMigrationPhase.BackupCompleted;
        WriteJournal(journal);
        await faultInjector.OnFaultPointAsync(StorageMigrationFaultPoint.AfterBackup, cancellationToken)
            .ConfigureAwait(false);

        var currentRegistration = await registry.GetAsync(plan.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (currentRegistration is null || !RoutesEqual(currentRegistration.ActiveRoute, plan.SourceRoute))
        {
            throw new StorageRouteConflictException(plan.WorkspaceId);
        }

        var destinationPath = DatabasePath(plan.DestinationRoute);
        var existingDestination = await ReadSnapshotIfPresentAsync(
            destinationPath, plan.WorkspaceId, cancellationToken).ConfigureAwait(false);
        EnsureDestinationCanBeReplaced(existingDestination, sourceSnapshot, currentRegistration, plan, journal);
        await faultInjector.OnFaultPointAsync(
            StorageMigrationFaultPoint.BeforeDestinationCommit, cancellationToken).ConfigureAwait(false);
        await ImportSnapshotAsync(destinationPath, plan.DestinationRoute, sourceSnapshot, cancellationToken)
            .ConfigureAwait(false);
        var validatedDestination = await ReadSnapshotAsync(destinationPath, plan.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        ValidateSnapshot(validatedDestination);
        if (!string.Equals(sourceSnapshot.ManifestHash, validatedDestination.ManifestHash, StringComparison.Ordinal))
        {
            throw new StorageMigrationValidationException("The destination ledger does not match the source manifest.");
        }

        journal.DestinationManifestHash = validatedDestination.ManifestHash;
        journal.Phase = StorageMigrationPhase.DestinationCommitted;
        WriteJournal(journal);
        await faultInjector.OnFaultPointAsync(
            StorageMigrationFaultPoint.AfterDestinationCommit, cancellationToken).ConfigureAwait(false);
        await faultInjector.OnFaultPointAsync(
            StorageMigrationFaultPoint.BeforeRouteSwitch, cancellationToken).ConfigureAwait(false);

        var liveSourceSnapshot = await ReadSnapshotAsync(
            sourceDatabasePath, plan.WorkspaceId, cancellationToken).ConfigureAwait(false);
        ValidateSnapshot(liveSourceSnapshot);
        if (liveSourceSnapshot.Route is null ||
            !RouteMatchesStore(liveSourceSnapshot.Route, plan.SourceRoute))
        {
            throw new StorageRouteConflictException(plan.WorkspaceId);
        }

        if (!string.Equals(
                liveSourceSnapshot.ManifestHash,
                sourceSnapshot.ManifestHash,
                StringComparison.Ordinal))
        {
            throw new StorageMigrationConflictException(
                "The active source changed during migration; retry the migration.");
        }

        var switched = await registry.SwitchAsync(
            plan.SourceRoute,
            plan.DestinationRoute,
            journal.MigrationId,
            sourceSnapshot.ManifestHash,
            cancellationToken).ConfigureAwait(false);
        journal.Phase = StorageMigrationPhase.RouteSwitched;
        WriteJournal(journal);
        await faultInjector.OnFaultPointAsync(StorageMigrationFaultPoint.AfterRouteSwitch, cancellationToken)
            .ConfigureAwait(false);
        await faultInjector.OnFaultPointAsync(
            StorageMigrationFaultPoint.BeforeJournalComplete, cancellationToken).ConfigureAwait(false);
        journal.Phase = StorageMigrationPhase.Completed;
        journal.CompletedAt = timeProvider.GetUtcNow();
        WriteJournal(journal);
        return new StorageMigrationResult(
            StorageMigrationOutcome.Migrated,
            plan,
            switched.ActiveRoute,
            journal.MigrationId,
            journal.Phase);
    }

    private async Task<StorageMigrationPlan> BuildPlanAsync(
        StorageMigrationRequest request,
        RegisteredStorageRoute registered,
        CancellationToken cancellationToken)
    {
        var sourcePath = DatabasePath(registered.ActiveRoute);
        var source = await ReadSnapshotIfPresentAsync(
            sourcePath, registered.ActiveRoute.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? DatabaseSnapshot.Empty(registered.ActiveRoute.WorkspaceId);
        if (source.Route is not null && !RouteMatchesStore(source.Route, registered.ActiveRoute))
        {
            throw new StorageRouteConflictException(registered.ActiveRoute.WorkspaceId);
        }

        var destinationRoot = ResolveDestinationRoot(request, registered.CanonicalWorkspaceRoot);
        var destinationRoute = new StorageRoute(
            registered.ActiveRoute.WorkspaceId,
            request.DestinationMode,
            destinationRoot,
            checked(registered.ActiveRoute.RouteGeneration + 1),
            StorageStoreIdentity.FromDataRoot(destinationRoot));
        var destinationPath = DatabasePath(destinationRoute);
        var destination = await ReadSnapshotIfPresentAsync(
            destinationPath, registered.ActiveRoute.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var isLineage = registered.SupersededStores.Any(entry =>
            string.Equals(entry.StoreId, destinationRoute.ActiveStoreId, StringComparison.Ordinal) &&
            FileStorageRouteRegistry.PathsEqual(entry.CanonicalDataRoot, destinationRoot));
        var exact = destination is not null &&
            string.Equals(destination.ManifestHash, source.ManifestHash, StringComparison.Ordinal);
        var unfinishedJournal = ReadJournal(registered.ActiveRoute.WorkspaceId);
        var knownUnfinishedImport = destination is not null &&
            IsKnownUnfinishedImport(unfinishedJournal, registered.ActiveRoute, destinationRoute, destination.ManifestHash);
        if (destination?.HasWorkspaceState == true && !isLineage && !exact && !knownUnfinishedImport)
        {
            throw new StorageMigrationConflictException(
                "The destination contains independent data for this workspace.");
        }

        var exclude = request.DestinationMode == StorageMode.Workspace
            ? await gitExcludeManager.PreviewAsync(registered.CanonicalWorkspaceRoot, cancellationToken)
                .ConfigureAwait(false)
            : new GitExcludePlan(false, null, false, false);
        if (exclude.HasTrackedStorageFiles)
        {
            throw new StorageMigrationConflictException("Workspace storage files are already tracked by Git.");
        }

        return new StorageMigrationPlan(
            registered.ActiveRoute.WorkspaceId,
            registered.ActiveRoute,
            destinationRoute,
            source.Preview,
            fileSystem.FileExists(destinationPath),
            destination?.HasWorkspaceState == true,
            isLineage,
            exclude,
            source.ManifestHash);
    }

    private async Task<RegisteredStorageRoute> GetRequiredRegistrationAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return await registry.GetByWorkspacePathAsync(CanonicalPath(workspaceRoot), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new StorageMigrationConflictException("The workspace has no active storage route.");
    }

    private string ResolveDestinationRoot(StorageMigrationRequest request, string canonicalWorkspaceRoot)
    {
        if (request.DestinationMode == StorageMode.Workspace)
        {
            if (!string.IsNullOrWhiteSpace(request.GlobalDataRoot))
            {
                throw new ArgumentException("A custom data root is valid only for global storage.", nameof(request));
            }

            return CanonicalPath(Path.Combine(canonicalWorkspaceRoot, ".token-meter"));
        }

        return CanonicalPath(request.GlobalDataRoot ?? defaultGlobalDataRoot);
    }

    private void ProbeDestinationWritable(string destinationRoot)
    {
        fileSystem.CreateDirectory(destinationRoot);
        var probe = Path.Combine(destinationRoot, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            using var stream = fileSystem.OpenFile(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
            stream.Flush();
        }
        finally
        {
            if (fileSystem.FileExists(probe))
            {
                fileSystem.DeleteFile(probe);
            }
        }
    }

    private static async Task BackupDatabaseAsync(
        string sourcePath,
        string backupPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var backupBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        await using var source = new SqliteConnection(sourceBuilder.ToString());
        await using var destination = new SqliteConnection(backupBuilder.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task ImportSnapshotAsync(
        string destinationPath,
        StorageRoute destinationRoute,
        DatabaseSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using (var store = new SqliteLedgerStore(destinationPath))
        {
            await store.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            foreach (var table in new[] { "diagnostics", "turns", "sources", "workspace_routes" })
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE workspace_id = $workspace;";
                delete.Parameters.AddWithValue("$workspace", WorkspaceText(destinationRoute.WorkspaceId));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertRouteAsync(connection, transaction, destinationRoute, cancellationToken).ConfigureAwait(false);
            foreach (var source in snapshot.Sources)
            {
                await InsertSourceAsync(connection, transaction, destinationRoute.WorkspaceId, source, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var turn in snapshot.Turns)
            {
                await InsertTurnAsync(connection, transaction, destinationRoute.WorkspaceId, turn, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var diagnostic in snapshot.Diagnostics)
            {
                await InsertDiagnosticAsync(
                    connection, transaction, destinationRoute.WorkspaceId, diagnostic, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DatabaseSnapshot?> ReadSnapshotIfPresentAsync(
        string databasePath,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(databasePath))
        {
            return null;
        }

        return await ReadSnapshotAsync(databasePath, workspaceId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(
        string databasePath,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var version = await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if (version != SqliteSchemaMigrator.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Ledger schema version {version} is not supported for migration.");
        }

        var workspace = WorkspaceText(workspaceId);
        StorageRoute? route = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT mode, canonical_data_root, route_generation, active_store_id FROM workspace_routes WHERE workspace_id = $workspace;";
            command.Parameters.AddWithValue("$workspace", workspace);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                route = new StorageRoute(
                    workspaceId,
                    (StorageMode)reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3));
            }
        }

        var turns = new List<RawTurn>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT provider, root_session_id, root_turn_id, revision, projection_hash, projection_json, stored_at FROM turns WHERE workspace_id = $workspace ORDER BY provider, root_session_id, root_turn_id;";
            command.Parameters.AddWithValue("$workspace", workspace);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                turns.Add(new RawTurn(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
        }

        var sources = new List<RawSource>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_id, generation, content_fingerprint, read_extent, availability, is_complete, updated_at FROM sources WHERE workspace_id = $workspace ORDER BY source_id;";
            command.Parameters.AddWithValue("$workspace", workspace);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sources.Add(new RawSource(
                    reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt32(4),
                    reader.GetInt32(5), reader.GetString(6)));
            }
        }

        var diagnostics = new List<RawDiagnostic>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT scope_key, code, count, occurred_at, approximate_bytes FROM diagnostics WHERE workspace_id = $workspace ORDER BY occurred_at, id;";
            command.Parameters.AddWithValue("$workspace", workspace);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                diagnostics.Add(new RawDiagnostic(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetInt64(4)));
            }
        }

        return DatabaseSnapshot.Create(workspaceId, route, turns, sources, diagnostics);
    }

    private static void ValidateSnapshot(DatabaseSnapshot snapshot)
    {
        foreach (var turn in snapshot.Turns)
        {
            var bytes = Encoding.UTF8.GetBytes(turn.ProjectionJson);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(hash, turn.ProjectionHash, StringComparison.Ordinal))
            {
                throw new StorageMigrationValidationException("A stored projection hash is invalid.");
            }

            var projection = JsonSerializer.Deserialize<TurnProjection>(turn.ProjectionJson, ContractJson.Options)
                ?? throw new StorageMigrationValidationException("A stored projection is invalid.");
            if (!string.Equals(projection.TurnKey.Provider.ToString(), turn.Provider, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(projection.TurnKey.RootSessionId, turn.RootSessionId, StringComparison.Ordinal) ||
                !string.Equals(projection.TurnKey.RootTurnId, turn.RootTurnId, StringComparison.Ordinal) ||
                projection.Revision != turn.Revision)
            {
                throw new StorageMigrationValidationException("A stored projection identity does not match its key.");
            }
        }
    }

    private static void EnsureDestinationCanBeReplaced(
        DatabaseSnapshot? destination,
        DatabaseSnapshot source,
        RegisteredStorageRoute registration,
        StorageMigrationPlan plan,
        MigrationJournal journal)
    {
        if (destination is null || !destination.HasWorkspaceState)
        {
            return;
        }

        if (string.Equals(destination.ManifestHash, source.ManifestHash, StringComparison.Ordinal))
        {
            return;
        }

        var knownJournalImport = string.Equals(
                destination.ManifestHash, journal.SourceManifestHash, StringComparison.Ordinal) ||
            string.Equals(destination.ManifestHash, journal.DestinationManifestHash, StringComparison.Ordinal);
        var verifiedLineage = registration.SupersededStores.Any(item =>
            string.Equals(item.StoreId, plan.DestinationRoute.ActiveStoreId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(item.MigrationReceipt));
        if (!knownJournalImport && !verifiedLineage)
        {
            throw new StorageMigrationConflictException(
                "The destination contains independent data for this workspace.");
        }
    }

    private async Task CompleteJournalAfterObservedSwitchAsync(
        StorageRoute activeRoute,
        CancellationToken cancellationToken)
    {
        var journal = ReadJournal(activeRoute.WorkspaceId);
        if (journal is null || journal.Phase == StorageMigrationPhase.Completed ||
            !string.Equals(journal.DestinationStoreId, activeRoute.ActiveStoreId, StringComparison.Ordinal) ||
            journal.DestinationGeneration != activeRoute.RouteGeneration)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        journal.Phase = StorageMigrationPhase.Completed;
        journal.CompletedAt = timeProvider.GetUtcNow();
        WriteJournal(journal);
    }

    private MigrationJournal? ReadJournal(Guid workspaceId)
    {
        var path = JournalPath(workspaceId);
        if (!fileSystem.FileExists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<MigrationJournal>(
            fileSystem.ReadAllText(path), MigrationJson.Options)
            ?? throw new InvalidDataException("The storage migration journal is invalid.");
    }

    private void WriteJournal(MigrationJournal journal)
    {
        fileSystem.CreateDirectory(JournalRoot);
        var path = JournalPath(journal.WorkspaceId);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            fileSystem.WriteAllText(temporary, JsonSerializer.Serialize(journal, MigrationJson.Options));
            fileSystem.MoveFile(temporary, path, overwrite: true);
        }
        finally
        {
            if (fileSystem.FileExists(temporary))
            {
                fileSystem.DeleteFile(temporary);
            }
        }
    }

    private static void ValidateJournal(MigrationJournal journal, StorageMigrationPlan plan)
    {
        if (journal.WorkspaceId != plan.WorkspaceId ||
            !string.Equals(journal.SourceStoreId, plan.SourceRoute.ActiveStoreId, StringComparison.Ordinal) ||
            !string.Equals(journal.DestinationStoreId, plan.DestinationRoute.ActiveStoreId, StringComparison.Ordinal) ||
            journal.ExpectedGeneration != plan.SourceRoute.RouteGeneration ||
            journal.DestinationGeneration != plan.DestinationRoute.RouteGeneration)
        {
            throw new StorageMigrationConflictException("An unfinished migration targets a different route.");
        }
    }

    private static bool IsKnownUnfinishedImport(
        MigrationJournal? journal,
        StorageRoute sourceRoute,
        StorageRoute destinationRoute,
        string destinationManifestHash) =>
        journal is not null &&
        journal.Phase != StorageMigrationPhase.Completed &&
        journal.WorkspaceId == sourceRoute.WorkspaceId &&
        string.Equals(journal.SourceStoreId, sourceRoute.ActiveStoreId, StringComparison.Ordinal) &&
        string.Equals(journal.DestinationStoreId, destinationRoute.ActiveStoreId, StringComparison.Ordinal) &&
        journal.ExpectedGeneration == sourceRoute.RouteGeneration &&
        journal.DestinationGeneration == destinationRoute.RouteGeneration &&
        (string.Equals(destinationManifestHash, journal.SourceManifestHash, StringComparison.Ordinal) ||
         string.Equals(destinationManifestHash, journal.DestinationManifestHash, StringComparison.Ordinal));

    private static bool RoutesEqual(StorageRoute left, StorageRoute right) =>
        left.WorkspaceId == right.WorkspaceId &&
        left.Mode == right.Mode &&
        left.RouteGeneration == right.RouteGeneration &&
        string.Equals(left.ActiveStoreId, right.ActiveStoreId, StringComparison.Ordinal) &&
        FileStorageRouteRegistry.PathsEqual(left.CanonicalDataRoot, right.CanonicalDataRoot);

    private static bool RouteMatchesStore(StorageRoute stored, StorageRoute active) =>
        stored.WorkspaceId == active.WorkspaceId &&
        stored.Mode == active.Mode &&
        stored.RouteGeneration == active.RouteGeneration &&
        string.Equals(stored.ActiveStoreId, active.ActiveStoreId, StringComparison.Ordinal) &&
        FileStorageRouteRegistry.PathsEqual(stored.CanonicalDataRoot, active.CanonicalDataRoot);

    private static async Task InsertRouteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StorageRoute route,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO workspace_routes (workspace_id, mode, canonical_data_root, route_generation, active_store_id) VALUES ($workspace, $mode, $root, $generation, $store);";
        command.Parameters.AddWithValue("$workspace", WorkspaceText(route.WorkspaceId));
        command.Parameters.AddWithValue("$mode", (int)route.Mode);
        command.Parameters.AddWithValue("$root", Path.GetFullPath(route.CanonicalDataRoot));
        command.Parameters.AddWithValue("$generation", route.RouteGeneration);
        command.Parameters.AddWithValue("$store", route.ActiveStoreId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTurnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        RawTurn turn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO turns (workspace_id, provider, root_session_id, root_turn_id, revision, projection_hash, projection_json, stored_at) VALUES ($workspace, $provider, $session, $turn, $revision, $hash, $json, $stored);";
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        command.Parameters.AddWithValue("$provider", turn.Provider);
        command.Parameters.AddWithValue("$session", turn.RootSessionId);
        command.Parameters.AddWithValue("$turn", turn.RootTurnId);
        command.Parameters.AddWithValue("$revision", turn.Revision);
        command.Parameters.AddWithValue("$hash", turn.ProjectionHash);
        command.Parameters.AddWithValue("$json", turn.ProjectionJson);
        command.Parameters.AddWithValue("$stored", turn.StoredAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        RawSource source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sources (workspace_id, source_id, generation, content_fingerprint, read_extent, availability, is_complete, updated_at) VALUES ($workspace, $source, $generation, $fingerprint, $extent, $availability, $complete, $updated);";
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        command.Parameters.AddWithValue("$source", source.SourceId);
        command.Parameters.AddWithValue("$generation", source.Generation);
        command.Parameters.AddWithValue("$fingerprint", source.ContentFingerprint);
        command.Parameters.AddWithValue("$extent", (object?)source.ReadExtent ?? DBNull.Value);
        command.Parameters.AddWithValue("$availability", source.Availability);
        command.Parameters.AddWithValue("$complete", source.IsComplete);
        command.Parameters.AddWithValue("$updated", source.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertDiagnosticAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        RawDiagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO diagnostics (workspace_id, scope_key, code, count, occurred_at, approximate_bytes) VALUES ($workspace, $scope, $code, $count, $occurred, $bytes);";
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        command.Parameters.AddWithValue("$scope", diagnostic.ScopeKey);
        command.Parameters.AddWithValue("$code", diagnostic.Code);
        command.Parameters.AddWithValue("$count", diagnostic.Count);
        command.Parameters.AddWithValue("$occurred", diagnostic.OccurredAt);
        command.Parameters.AddWithValue("$bytes", diagnostic.ApproximateBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private string CanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(fileSystem.GetFullPath(path));

    private static string DatabasePath(StorageRoute route) => Path.Combine(route.CanonicalDataRoot, "ledger.db");
    private static string WorkspaceText(Guid workspaceId) => workspaceId.ToString("D");
    private static PurgePreview EmptyPreview(Guid workspaceId) => new(workspaceId, 0, 0, 0);
    private string JournalRoot => Path.Combine(migrationStateRoot, "journals");
    private string BackupRoot => Path.Combine(migrationStateRoot, "backups");
    private string JournalPath(Guid workspaceId) => Path.Combine(JournalRoot, $"{workspaceId:D}.json");

    private sealed record RawTurn(
        string Provider,
        string RootSessionId,
        string RootTurnId,
        long Revision,
        string ProjectionHash,
        string ProjectionJson,
        string StoredAt);

    private sealed record RawSource(
        string SourceId,
        long Generation,
        string ContentFingerprint,
        long? ReadExtent,
        int Availability,
        int IsComplete,
        string UpdatedAt);

    private sealed record RawDiagnostic(
        string ScopeKey,
        string Code,
        int Count,
        string OccurredAt,
        long ApproximateBytes);

    private sealed record DatabaseSnapshot(
        Guid WorkspaceId,
        StorageRoute? Route,
        IReadOnlyList<RawTurn> Turns,
        IReadOnlyList<RawSource> Sources,
        IReadOnlyList<RawDiagnostic> Diagnostics,
        string ManifestHash)
    {
        public bool HasWorkspaceState => Route is not null || Turns.Count > 0 || Sources.Count > 0 || Diagnostics.Count > 0;
        public PurgePreview Preview => new(WorkspaceId, Turns.Count, Sources.Count, Diagnostics.Count);

        public static DatabaseSnapshot Empty(Guid workspaceId) =>
            Create(workspaceId, null, [], [], []);

        public static DatabaseSnapshot Create(
            Guid workspaceId,
            StorageRoute? route,
            IReadOnlyList<RawTurn> turns,
            IReadOnlyList<RawSource> sources,
            IReadOnlyList<RawDiagnostic> diagnostics)
        {
            var manifest = new
            {
                workspaceId,
                turns = turns.Select(static turn => new
                {
                    turn.Provider,
                    turn.RootSessionId,
                    turn.RootTurnId,
                    turn.Revision,
                    turn.ProjectionHash,
                    turn.ProjectionJson
                }),
                sources = sources.Select(static source => new
                {
                    source.SourceId,
                    source.Generation,
                    source.ContentFingerprint,
                    source.ReadExtent,
                    source.Availability,
                    source.IsComplete
                }),
                diagnostics = diagnostics.Select(static diagnostic => new
                {
                    diagnostic.ScopeKey,
                    diagnostic.Code,
                    diagnostic.Count,
                    diagnostic.OccurredAt,
                    diagnostic.ApproximateBytes
                })
            };
            var json = JsonSerializer.Serialize(manifest, MigrationJson.Options);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            return new DatabaseSnapshot(workspaceId, route, turns, sources, diagnostics, hash);
        }
    }

    private sealed class MigrationJournal
    {
        public int SchemaVersion { get; set; } = 1;
        public required string MigrationId { get; set; }
        public Guid WorkspaceId { get; set; }
        public required string SourceStoreId { get; set; }
        public required string DestinationStoreId { get; set; }
        public required string SourceDatabasePath { get; set; }
        public required string DestinationDatabasePath { get; set; }
        public long ExpectedGeneration { get; set; }
        public long DestinationGeneration { get; set; }
        public StorageMigrationPhase Phase { get; set; }
        public string? BackupPath { get; set; }
        public string? SourceManifestHash { get; set; }
        public string? DestinationManifestHash { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        public static MigrationJournal Create(StorageMigrationPlan plan, DateTimeOffset startedAt) => new()
        {
            MigrationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = plan.WorkspaceId,
            SourceStoreId = plan.SourceRoute.ActiveStoreId,
            DestinationStoreId = plan.DestinationRoute.ActiveStoreId,
            SourceDatabasePath = DatabasePath(plan.SourceRoute),
            DestinationDatabasePath = DatabasePath(plan.DestinationRoute),
            ExpectedGeneration = plan.SourceRoute.RouteGeneration,
            DestinationGeneration = plan.DestinationRoute.RouteGeneration,
            Phase = StorageMigrationPhase.Planned,
            StartedAt = startedAt
        };
    }

    private static class MigrationJson
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    }
}
