using Microsoft.Data.Sqlite;

namespace TaskTokenMeter.Storage;

public sealed class SqliteSchemaMigrator(ILedgerFaultInjector? faultInjector = null)
{
    public const int CurrentSchemaVersion = 2;

    private readonly ILedgerFaultInjector faultInjector = faultInjector ?? new NoLedgerFaultInjector();

    public async Task MigrateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var version = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"Ledger schema version {version} is newer than supported version {CurrentSchemaVersion}.");
            }

            for (var target = version + 1; target <= CurrentSchemaVersion; target++)
            {
                await ApplyStepAsync(connection, transaction, target, cancellationToken).ConfigureAwait(false);
                await faultInjector.OnSchemaMigrationFaultPointAsync(
                    SchemaMigrationFaultPoint.AfterStepApplied,
                    target,
                    cancellationToken).ConfigureAwait(false);
                await SetVersionAsync(connection, transaction, target, cancellationToken).ConfigureAwait(false);
            }

            await faultInjector.OnSchemaMigrationFaultPointAsync(
                SchemaMigrationFaultPoint.BeforeCommit,
                CurrentSchemaVersion,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task SetVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task ApplyStepAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int targetVersion,
        CancellationToken cancellationToken) =>
        targetVersion switch
        {
            1 => ExecuteAsync(connection, transaction, Version1Sql, cancellationToken),
            2 => ExecuteAsync(connection, transaction, Version2Sql, cancellationToken),
            _ => throw new NotSupportedException($"No migration exists for schema version {targetVersion}.")
        };

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string Version1Sql = """
        CREATE TABLE workspace_routes (
            workspace_id TEXT NOT NULL PRIMARY KEY,
            mode INTEGER NOT NULL,
            canonical_data_root TEXT NOT NULL,
            route_generation INTEGER NOT NULL CHECK (route_generation >= 0),
            active_store_id TEXT NOT NULL
        );

        CREATE TABLE turns (
            workspace_id TEXT NOT NULL,
            provider TEXT NOT NULL,
            root_session_id TEXT NOT NULL,
            root_turn_id TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            projection_hash TEXT NOT NULL,
            projection_json TEXT NOT NULL,
            stored_at TEXT NOT NULL,
            PRIMARY KEY (workspace_id, provider, root_session_id, root_turn_id),
            FOREIGN KEY (workspace_id) REFERENCES workspace_routes(workspace_id) ON DELETE CASCADE
        );

        CREATE TABLE sources (
            workspace_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            generation INTEGER NOT NULL CHECK (generation >= 0),
            content_fingerprint TEXT NOT NULL,
            read_extent INTEGER NULL CHECK (read_extent IS NULL OR read_extent >= 0),
            availability INTEGER NOT NULL,
            is_complete INTEGER NOT NULL CHECK (is_complete IN (0, 1)),
            updated_at TEXT NOT NULL,
            PRIMARY KEY (workspace_id, source_id),
            FOREIGN KEY (workspace_id) REFERENCES workspace_routes(workspace_id) ON DELETE CASCADE
        );

        CREATE INDEX ix_turns_workspace_observed
            ON turns(workspace_id, stored_at);
        """;

    private const string Version2Sql = """
        CREATE TABLE diagnostics (
            id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL,
            scope_key TEXT NOT NULL,
            code TEXT NOT NULL,
            count INTEGER NOT NULL CHECK (count > 0),
            occurred_at TEXT NOT NULL,
            approximate_bytes INTEGER NOT NULL CHECK (approximate_bytes >= 0),
            FOREIGN KEY (workspace_id) REFERENCES workspace_routes(workspace_id) ON DELETE CASCADE
        );

        CREATE INDEX ix_diagnostics_workspace_occurred
            ON diagnostics(workspace_id, occurred_at DESC, id DESC);
        """;
}
