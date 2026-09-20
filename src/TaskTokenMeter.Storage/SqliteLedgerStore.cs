using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Projection;

namespace TaskTokenMeter.Storage;

public sealed partial class SqliteLedgerStore : ILedgerStore, IAsyncDisposable
{
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly SqliteLedgerOptions options;
    private readonly ILedgerDelay delay;
    private readonly ILedgerFaultInjector faultInjector;
    private readonly IStorageRouteAuthority? routeAuthority;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private bool initialized;

    public SqliteLedgerStore(
        string databasePath,
        SqliteLedgerOptions? options = null,
        ILedgerDelay? delay = null,
        ILedgerFaultInjector? faultInjector = null,
        TimeProvider? timeProvider = null,
        IStorageRouteAuthority? routeAuthority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.options = options ?? new SqliteLedgerOptions();
        this.options.Validate();
        this.delay = delay ?? new SystemLedgerDelay();
        this.faultInjector = faultInjector ?? new NoLedgerFaultInjector();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.routeAuthority = routeAuthority;
        this.databasePath = ValidateLocalDatabasePath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(this.options.BusyTimeout.TotalSeconds))
        }.ToString();
    }

    public string DatabasePath => databasePath;

    public static SqliteLedgerStore FromRoute(
        StorageRoute route,
        SqliteLedgerOptions? options = null,
        ILedgerDelay? delay = null,
        ILedgerFaultInjector? faultInjector = null,
        TimeProvider? timeProvider = null,
        IStorageRouteAuthority? routeAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new SqliteLedgerStore(
            Path.Combine(route.CanonicalDataRoot, "ledger.db"),
            options,
            delay,
            faultInjector,
            timeProvider,
            routeAuthority);
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException("The ledger path has no parent directory."));
            await ExecuteWithRetryAsync(
                async token =>
                {
                    await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
                    await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", token).ConfigureAwait(false);
                    await new SqliteSchemaMigrator(faultInjector).MigrateAsync(connection, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async Task<LedgerCommitResult> CommitAsync(
        LedgerCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        ValidateProjectionRoute(request);
        if (request.Projection.Revision != request.ExpectedRevision && request.Projection.Revision != checked(request.ExpectedRevision + 1))
        {
            throw new ArgumentException(
                "The projection revision must be exactly one greater than the expected stored revision.",
                nameof(request));
        }

        IAsyncDisposable? workspaceLease = null;
        if (routeAuthority is IWorkspaceWriteCoordinator coordinator)
        {
            workspaceLease = await coordinator.AcquireWorkspaceWriteAsync(
                request.Route.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteWithRetryAsync(
                token => CommitOnceAsync(request, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (workspaceLease is not null)
            {
                await workspaceLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<StoredTurnProjection?> LoadAsync(
        Guid workspaceId,
        string provider,
        string rootSessionId,
        string rootTurnId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootTurnId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT projection_json, projection_hash, stored_at
            FROM turns
            WHERE workspace_id = $workspace AND provider = $provider
              AND root_session_id = $session AND root_turn_id = $turn;
            """;
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        command.Parameters.AddWithValue("$provider", NormalizeProvider(provider));
        command.Parameters.AddWithValue("$session", rootSessionId);
        command.Parameters.AddWithValue("$turn", rootTurnId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var projection = System.Text.Json.JsonSerializer.Deserialize<TurnProjection>(
            reader.GetString(0),
            ContractJson.Options) ?? throw new InvalidDataException("Stored projection JSON is invalid.");
        return new StoredTurnProjection(
            workspaceId,
            projection,
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task<IReadOnlyList<StoredTurnProjection>> LoadSessionAsync(
        Guid workspaceId,
        string provider,
        string rootSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT projection_json, projection_hash, stored_at
            FROM turns
            WHERE workspace_id = $workspace AND provider = $provider AND root_session_id = $session
            ORDER BY stored_at, root_turn_id;
            """;
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        command.Parameters.AddWithValue("$provider", NormalizeProvider(provider));
        command.Parameters.AddWithValue("$session", rootSessionId);
        var result = new List<StoredTurnProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var projection = System.Text.Json.JsonSerializer.Deserialize<TurnProjection>(
                reader.GetString(0), ContractJson.Options)
                ?? throw new InvalidDataException("Stored projection JSON is invalid.");
            result.Add(new StoredTurnProjection(
                workspaceId, projection, reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }
    public async Task<IReadOnlyList<SourceSnapshot>> LoadSourcesAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, generation, content_fingerprint, read_extent, availability, is_complete
            FROM sources WHERE workspace_id = $workspace ORDER BY source_id;
            """;
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        var result = new List<SourceSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SourceSnapshot(
                reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                NullableInt64(reader, 3), (SourceAvailability)reader.GetInt32(4), reader.GetInt32(5) != 0));
        }

        return result;
    }
    public async Task<IReadOnlyList<LedgerDiagnostic>> GetDiagnosticsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code, scope_key, count, occurred_at FROM diagnostics
            WHERE workspace_id = $workspace ORDER BY occurred_at, id;
            """;
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        var result = new List<LedgerDiagnostic>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LedgerDiagnostic(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public async Task<PurgePreview> PreviewPurgeAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPurgePreviewAsync(connection, null, workspaceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurgePreview> PurgeAsync(
        LedgerPurgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Route);
        IAsyncDisposable? workspaceLease = null;
        if (routeAuthority is IWorkspaceWriteCoordinator coordinator)
        {
            workspaceLease = await coordinator.AcquireWorkspaceWriteAsync(
                request.Route.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteWithRetryAsync(
                async token =>
                {
                    if (routeAuthority is not null &&
                        !await routeAuthority.IsActiveAsync(request.Route, token).ConfigureAwait(false))
                    {
                        throw new StorageRouteConflictException(request.Route.WorkspaceId);
                    }

                    await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
                    await using var transaction = connection.BeginTransaction(deferred: false);
                    if (!await EnsureAndValidateRouteAsync(connection, transaction, request.Route, token).ConfigureAwait(false))
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        throw new StorageRouteConflictException(request.Route.WorkspaceId);
                    }

                    var preview = await ReadPurgePreviewAsync(
                        connection, transaction, request.Route.WorkspaceId, token).ConfigureAwait(false);
                    await DeleteWorkspaceRowsAsync(
                        connection, transaction, request.Route.WorkspaceId, request.IncludeDiagnostics, token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return preview;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (workspaceLease is not null)
            {
                await workspaceLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        initializationLock.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode is 5 or 6 &&
                stopwatch.Elapsed + options.RetryDelay <= options.MaximumRetryDuration)
            {
                await delay.DelayAsync(options.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(
                connection,
                $"PRAGMA busy_timeout = {(long)options.BusyTimeout.TotalMilliseconds};",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class StorageRouteConflictException(Guid workspaceId)
    : InvalidOperationException($"The active storage route for workspace '{workspaceId:D}' changed.")
{
    public Guid WorkspaceId { get; } = workspaceId;
}
