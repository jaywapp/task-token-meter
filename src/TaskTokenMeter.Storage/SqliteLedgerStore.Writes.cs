using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Storage;

public sealed partial class SqliteLedgerStore
{
    private async Task<LedgerCommitResult> CommitOnceAsync(
        LedgerCommitRequest request,
        CancellationToken cancellationToken)
    {
        await faultInjector.OnLedgerFaultPointAsync(
            LedgerFaultPoint.BeforeTransaction, cancellationToken).ConfigureAwait(false);
        if (routeAuthority is not null &&
            !await routeAuthority.IsActiveAsync(request.Route, cancellationToken).ConfigureAwait(false))
        {
            return new LedgerCommitResult(LedgerCommitStatus.RouteConflict, 0, "route_generation_conflict");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var committed = false;
        try
        {
            await faultInjector.OnLedgerFaultPointAsync(
                LedgerFaultPoint.AfterTransactionStarted, cancellationToken).ConfigureAwait(false);
            if (!await EnsureAndValidateRouteAsync(
                    connection, transaction, request.Route, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new LedgerCommitResult(LedgerCommitStatus.RouteConflict, 0, "route_generation_conflict");
            }

            await faultInjector.OnLedgerFaultPointAsync(
                LedgerFaultPoint.AfterRouteValidated, cancellationToken).ConfigureAwait(false);
            var current = await ReadCurrentTurnAsync(
                connection, transaction, request, cancellationToken).ConfigureAwait(false);
            var degraded = request.Sources.Where(static source => !source.IsAuthoritative).ToArray();
            if (degraded.Length > 0)
            {
                foreach (var source in degraded)
                {
                    await InsertDiagnosticAsync(
                        connection,
                        transaction,
                        request.Route.WorkspaceId,
                        ScopeKey(request.Projection),
                        DiagnosticFor(source.Availability),
                        cancellationToken).ConfigureAwait(false);
                }

                await TrimDiagnosticsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LedgerCommitResult(
                    LedgerCommitStatus.Preserved,
                    current?.Revision ?? 0,
                    "source_not_authoritative");
            }

            if (!await SourcesAreCurrentAsync(
                    connection,
                    transaction,
                    request.Route.WorkspaceId,
                    request.Sources,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new LedgerCommitResult(
                    LedgerCommitStatus.SourceConflict,
                    current?.Revision ?? 0,
                    "source_generation_conflict");
            }

            var json = JsonSerializer.Serialize(request.Projection, ContractJson.Options);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            var unchanged = current is not null &&
                string.Equals(current.Hash, hash, StringComparison.Ordinal);
            if (!unchanged && (current?.Revision ?? 0) != request.ExpectedRevision)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new LedgerCommitResult(
                    LedgerCommitStatus.RevisionConflict,
                    current?.Revision ?? 0,
                    "expected_revision_conflict");
            }

            await WriteSourcesAsync(
                connection, transaction, request.Route.WorkspaceId, request.Sources, cancellationToken)
                .ConfigureAwait(false);
            await faultInjector.OnLedgerFaultPointAsync(
                LedgerFaultPoint.AfterSourcesWritten, cancellationToken).ConfigureAwait(false);
            if (!unchanged)
            {
                await WriteProjectionAsync(
                    connection, transaction, request, hash, json, cancellationToken).ConfigureAwait(false);
                foreach (var diagnostic in request.Projection.Diagnostics.Distinct(StringComparer.Ordinal))
                {
                    await InsertDiagnosticAsync(
                        connection,
                        transaction,
                        request.Route.WorkspaceId,
                        ScopeKey(request.Projection),
                        diagnostic,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await faultInjector.OnLedgerFaultPointAsync(
                LedgerFaultPoint.AfterProjectionWritten, cancellationToken).ConfigureAwait(false);
            await TrimDiagnosticsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await faultInjector.OnLedgerFaultPointAsync(
                LedgerFaultPoint.BeforeCommit, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            await faultInjector.OnLedgerFaultPointAsync(
                LedgerFaultPoint.AfterCommit, cancellationToken).ConfigureAwait(false);
            return new LedgerCommitResult(
                unchanged ? LedgerCommitStatus.Unchanged : LedgerCommitStatus.Committed,
                unchanged ? current!.Revision : request.Projection.Revision);
        }
        catch
        {
            if (!committed && transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<bool> EnsureAndValidateRouteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StorageRoute route,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO workspace_routes (
                    workspace_id, mode, canonical_data_root, route_generation, active_store_id)
                VALUES ($workspace, $mode, $root, $generation, $store);
                """;
            insert.Parameters.AddWithValue("$workspace", WorkspaceText(route.WorkspaceId));
            insert.Parameters.AddWithValue("$mode", (int)route.Mode);
            insert.Parameters.AddWithValue("$root", Path.GetFullPath(route.CanonicalDataRoot));
            insert.Parameters.AddWithValue("$generation", route.RouteGeneration);
            insert.Parameters.AddWithValue("$store", route.ActiveStoreId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT mode, canonical_data_root, route_generation, active_store_id
            FROM workspace_routes WHERE workspace_id = $workspace;
            """;
        select.Parameters.AddWithValue("$workspace", WorkspaceText(route.WorkspaceId));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetInt32(0) == (int)route.Mode &&
            PathsEqual(reader.GetString(1), route.CanonicalDataRoot) &&
            reader.GetInt64(2) == route.RouteGeneration &&
            string.Equals(reader.GetString(3), route.ActiveStoreId, StringComparison.Ordinal);
    }

    private static async Task<CurrentTurn?> ReadCurrentTurnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerCommitRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, projection_hash FROM turns
            WHERE workspace_id = $workspace AND provider = $provider
              AND root_session_id = $session AND root_turn_id = $turn;
            """;
        AddTurnKeyParameters(command, request.Route.WorkspaceId, request.Projection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CurrentTurn(reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async Task<bool> SourcesAreCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        IReadOnlyList<SourceSnapshot> sources,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT generation, content_fingerprint, read_extent FROM sources
                WHERE workspace_id = $workspace AND source_id = $source;
                """;
            command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
            command.Parameters.AddWithValue("$source", source.SourceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var generation = reader.GetInt64(0);
            if (generation > source.Generation ||
                generation == source.Generation &&
                (!string.Equals(reader.GetString(1), source.ContentFingerprint, StringComparison.Ordinal) ||
                 NullableInt64(reader, 2) != source.ReadExtent))
            {
                return false;
            }
        }

        return true;
    }

    private async Task WriteSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        IReadOnlyList<SourceSnapshot> sources,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sources (
                    workspace_id, source_id, generation, content_fingerprint, read_extent,
                    availability, is_complete, updated_at)
                VALUES ($workspace, $source, $generation, $fingerprint, $extent,
                    $availability, $complete, $updated)
                ON CONFLICT(workspace_id, source_id) DO UPDATE SET
                    generation = excluded.generation,
                    content_fingerprint = excluded.content_fingerprint,
                    read_extent = excluded.read_extent,
                    availability = excluded.availability,
                    is_complete = excluded.is_complete,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$workspace", WorkspaceText(workspaceId));
            command.Parameters.AddWithValue("$source", source.SourceId);
            command.Parameters.AddWithValue("$generation", source.Generation);
            command.Parameters.AddWithValue("$fingerprint", source.ContentFingerprint!);
            command.Parameters.AddWithValue("$extent", (object?)source.ReadExtent ?? DBNull.Value);
            command.Parameters.AddWithValue("$availability", (int)source.Availability);
            command.Parameters.AddWithValue("$complete", source.IsComplete ? 1 : 0);
            command.Parameters.AddWithValue("$updated", Timestamp(timeProvider.GetUtcNow()));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerCommitRequest request,
        string hash,
        string json,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO turns (
                workspace_id, provider, root_session_id, root_turn_id,
                revision, projection_hash, projection_json, stored_at)
            VALUES ($workspace, $provider, $session, $turn, $revision, $hash, $json, $stored)
            ON CONFLICT(workspace_id, provider, root_session_id, root_turn_id) DO UPDATE SET
                revision = excluded.revision, projection_hash = excluded.projection_hash,
                projection_json = excluded.projection_json, stored_at = excluded.stored_at;
            """;
        AddTurnKeyParameters(command, request.Route.WorkspaceId, request.Projection);
        command.Parameters.AddWithValue("$revision", request.Projection.Revision);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$stored", Timestamp(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record CurrentTurn(long Revision, string Hash);
}
