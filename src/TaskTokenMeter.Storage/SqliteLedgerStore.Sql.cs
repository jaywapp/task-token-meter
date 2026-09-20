using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Projection;

namespace TaskTokenMeter.Storage;

public sealed partial class SqliteLedgerStore
{
    private async Task InsertDiagnosticAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        string scopeKey,
        string code,
        CancellationToken cancellationToken)
    {
        var approximateBytes = Encoding.UTF8.GetByteCount(scopeKey) + Encoding.UTF8.GetByteCount(code) + 64;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO diagnostics (
                workspace_id, scope_key, code, count, occurred_at, approximate_bytes)
            VALUES ($workspace, $scope, $code, 1, $occurred, $bytes);
            """;
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$occurred", Timestamp(timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$bytes", approximateBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TrimDiagnosticsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - options.DiagnosticRetention.MaximumAge;
        await using (var ageCommand = connection.CreateCommand())
        {
            ageCommand.Transaction = transaction;
            ageCommand.CommandText = "DELETE FROM diagnostics WHERE occurred_at < $cutoff;";
            ageCommand.Parameters.AddWithValue("$cutoff", Timestamp(cutoff));
            await ageCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var sizeCommand = connection.CreateCommand();
        sizeCommand.Transaction = transaction;
        sizeCommand.CommandText = """
            DELETE FROM diagnostics
            WHERE id IN (
                SELECT id FROM (
                    SELECT id,
                        SUM(approximate_bytes) OVER (
                            ORDER BY occurred_at DESC, id DESC
                            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS retained_bytes
                    FROM diagnostics
                )
                WHERE retained_bytes > $maximum
            );
            """;
        sizeCommand.Parameters.AddWithValue("$maximum", options.DiagnosticRetention.MaximumApproximateBytes);
        await sizeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PurgePreview> ReadPurgePreviewAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var turns = await CountAsync(connection, transaction, "turns", workspaceId, cancellationToken).ConfigureAwait(false);
        var sources = await CountAsync(connection, transaction, "sources", workspaceId, cancellationToken).ConfigureAwait(false);
        var diagnostics = await CountAsync(
            connection, transaction, "diagnostics", workspaceId, cancellationToken).ConfigureAwait(false);
        return new PurgePreview(workspaceId, turns, sources, diagnostics);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE workspace_id = $workspace;";
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task DeleteWorkspaceRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var table in includeDiagnostics
                     ? new[] { "turns", "sources", "diagnostics" }
                     : new[] { "turns", "sources" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE workspace_id = $workspace;";
            command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddTurnKeyParameters(
        SqliteCommand command,
        Guid workspaceId,
        TurnProjection projection)
    {
        command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
        command.Parameters.AddWithValue("$provider", NormalizeProvider(projection.TurnKey.Provider.ToString()));
        command.Parameters.AddWithValue("$session", projection.TurnKey.RootSessionId);
        command.Parameters.AddWithValue("$turn", projection.TurnKey.RootTurnId);
    }

    private static void ValidateProjectionRoute(LedgerCommitRequest request)
    {
        if (request.Route.WorkspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace ID cannot be empty.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Route.CanonicalDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Route.ActiveStoreId);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Route.RouteGeneration);
    }

    private static string ValidateLocalDatabasePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Network filesystem ledger paths are not supported.");
        }

        var root = Path.GetPathRoot(fullPath);
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(root))
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network)
            {
                throw new NotSupportedException("Network filesystem ledger paths are not supported.");
            }
        }

        return fullPath;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string WorkspaceText(Guid workspaceId) => workspaceId.ToString("D");

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant();

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string ScopeKey(TurnProjection projection) =>
        $"{NormalizeProvider(projection.TurnKey.Provider.ToString())}:{projection.TurnKey.RootSessionId}:{projection.TurnKey.RootTurnId}";

    private static string DiagnosticFor(SourceAvailability availability) => availability switch
    {
        SourceAvailability.Available => "source_incomplete",
        SourceAvailability.Pending => "source_pending",
        SourceAvailability.Missing => "source_missing",
        SourceAvailability.Truncated => "source_truncated",
        SourceAvailability.Unreadable => "source_unreadable",
        SourceAvailability.Unsupported => "source_unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(availability), availability, null)
    };
}
