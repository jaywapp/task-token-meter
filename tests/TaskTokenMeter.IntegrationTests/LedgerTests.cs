using Microsoft.Data.Sqlite;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Storage;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class LedgerTests
{
    [Theory]
    [InlineData(StorageMode.Global)]
    [InlineData(StorageMode.Workspace)]
    public async Task EightConcurrentWritersCommitExactlyOneRowPerTurn(StorageMode mode)
    {
        await using var fixture = await LedgerFixture.CreateAsync(mode);
        var tasks = Enumerable.Range(0, 8)
            .Select(index => fixture.Store.CommitAsync(
                fixture.Request($"turn-{index}", 1, 0, $"source-{index}", 1, index + 1)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.Equal(LedgerCommitStatus.Committed, result.Status));
        var preview = await fixture.Store.PreviewPurgeAsync(fixture.WorkspaceId);
        Assert.Equal(8, preview.TurnCount);
        Assert.Equal(8, preview.SourceCount);
    }

    [Theory]
    [InlineData(StorageMode.Global)]
    [InlineData(StorageMode.Workspace)]
    public async Task SameSnapshotCommittedTenTimesIsIdempotent(StorageMode mode)
    {
        await using var fixture = await LedgerFixture.CreateAsync(mode);
        var request = fixture.Request("same-turn", 1, 0, "same-source", 1, 42);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => fixture.Store.CommitAsync(request)));

        Assert.Single(results, result => result.Status == LedgerCommitStatus.Committed);
        Assert.Equal(9, results.Count(result => result.Status == LedgerCommitStatus.Unchanged));
        var stored = await fixture.Store.LoadAsync(
            fixture.WorkspaceId, "codex", "root-session", "same-turn");
        Assert.Equal(1, stored?.Projection.Revision);
        Assert.Equal(42, stored?.Projection.Usage.ProcessedTokens);
    }

    [Theory]
    [InlineData(StorageMode.Global)]
    [InlineData(StorageMode.Workspace)]
    public async Task OlderSourceCompletionCannotReplaceNewerRevision(StorageMode mode)
    {
        await using var fixture = await LedgerFixture.CreateAsync(mode);
        await fixture.Store.CommitAsync(fixture.Request("turn", 1, 0, "source", 1, 10));
        var latest = await fixture.Store.CommitAsync(fixture.Request("turn", 2, 1, "source", 2, 20));

        var stale = await fixture.Store.CommitAsync(fixture.Request("turn", 1, 0, "source", 1, 10));

        Assert.Equal(LedgerCommitStatus.Committed, latest.Status);
        Assert.Equal(LedgerCommitStatus.SourceConflict, stale.Status);
        var stored = await fixture.Store.LoadAsync(fixture.WorkspaceId, "codex", "root-session", "turn");
        Assert.Equal(2, stored?.Projection.Revision);
        Assert.Equal(20, stored?.Projection.Usage.ProcessedTokens);
    }

    [Theory]
    [InlineData(SourceAvailability.Missing)]
    [InlineData(SourceAvailability.Truncated)]
    [InlineData(SourceAvailability.Unreadable)]
    public async Task DegradedSourceNeverOverwritesLastKnownGoodProjection(SourceAvailability availability)
    {
        await using var fixture = await LedgerFixture.CreateAsync(StorageMode.Global);
        await fixture.Store.CommitAsync(fixture.Request("turn", 1, 0, "source", 1, 75));
        var degraded = new LedgerCommitRequest(
            fixture.Route,
            Projection("turn", 2, 0),
            1,
            [new SourceSnapshot("source", 2, null, null, availability, false)]);

        var result = await fixture.Store.CommitAsync(degraded);

        Assert.Equal(LedgerCommitStatus.Preserved, result.Status);
        var stored = await fixture.Store.LoadAsync(fixture.WorkspaceId, "codex", "root-session", "turn");
        Assert.Equal(1, stored?.Projection.Revision);
        Assert.Equal(75, stored?.Projection.Usage.ProcessedTokens);
        Assert.Contains(
            await fixture.Store.GetDiagnosticsAsync(fixture.WorkspaceId),
            diagnostic => diagnostic.Code == ExpectedDiagnostic(availability));
    }

    [Theory]
    [InlineData(LedgerFaultPoint.AfterSourcesWritten)]
    [InlineData(LedgerFaultPoint.AfterProjectionWritten)]
    [InlineData(LedgerFaultPoint.BeforeCommit)]
    public async Task FailedOrTerminatedWriteRollsBackTheWholeTransaction(LedgerFaultPoint faultPoint)
    {
        var injector = new TestFaultInjector(faultPoint, new IOException("simulated write failure"));
        await using var fixture = await LedgerFixture.CreateAsync(StorageMode.Global, injector: injector);

        await Assert.ThrowsAsync<IOException>(
            () => fixture.Store.CommitAsync(fixture.Request("turn", 1, 0, "source", 1, 99)));

        Assert.Null(await fixture.Store.LoadAsync(fixture.WorkspaceId, "codex", "root-session", "turn"));
        var preview = await fixture.Store.PreviewPurgeAsync(fixture.WorkspaceId);
        Assert.Equal(0, preview.TurnCount);
        Assert.Equal(0, preview.SourceCount);
    }

    [Fact]
    public async Task RouteGenerationAndStoreIdentityRejectStaleWriter()
    {
        await using var fixture = await LedgerFixture.CreateAsync(StorageMode.Global);
        await fixture.Store.CommitAsync(fixture.Request("turn-1", 1, 0, "source-1", 1, 1));
        var changedRoute = fixture.Route with
        {
            RouteGeneration = fixture.Route.RouteGeneration + 1,
            ActiveStoreId = "replacement-store"
        };
        var request = new LedgerCommitRequest(
            changedRoute,
            Projection("turn-2", 1, 2),
            0,
            [new SourceSnapshot("source-2", 1, "hash-2", 2, SourceAvailability.Available, true)]);

        var result = await fixture.Store.CommitAsync(request);

        Assert.Equal(LedgerCommitStatus.RouteConflict, result.Status);
        Assert.Null(await fixture.Store.LoadAsync(fixture.WorkspaceId, "codex", "root-session", "turn-2"));
    }

    [Fact]
    public async Task SharedRouteAuthorityRejectsWriterAfterAnotherStoreBecomesActive()
    {
        var authority = new MutableRouteAuthority();
        await using var fixture = await LedgerFixture.CreateAsync(
            StorageMode.Global,
            routeAuthority: authority);
        authority.ActiveRoute = fixture.Route;
        await fixture.Store.CommitAsync(fixture.Request("turn-1", 1, 0, "source-1", 1, 1));
        authority.ActiveRoute = fixture.Route with
        {
            Mode = StorageMode.Workspace,
            RouteGeneration = 2,
            ActiveStoreId = "workspace-store"
        };

        var result = await fixture.Store.CommitAsync(
            fixture.Request("turn-2", 1, 0, "source-2", 1, 2));

        Assert.Equal(LedgerCommitStatus.RouteConflict, result.Status);
    }

    [Fact]
    public async Task ExplicitPurgeReturnsPreviewAndRemovesOnlyRequestedWorkspaceData()
    {
        await using var fixture = await LedgerFixture.CreateAsync(StorageMode.Workspace);
        await fixture.Store.CommitAsync(fixture.Request("turn", 1, 0, "source", 1, 5));
        var preview = await fixture.Store.PreviewPurgeAsync(fixture.WorkspaceId);

        var purged = await fixture.Store.PurgeAsync(new LedgerPurgeRequest(fixture.Route));

        Assert.Equal(preview, purged);
        Assert.Equal(1, purged.TurnCount);
        Assert.Equal(1, purged.SourceCount);
        Assert.Null(await fixture.Store.LoadAsync(fixture.WorkspaceId, "codex", "root-session", "turn"));
    }

    [Fact]
    public async Task BusyFailureUsesBoundedRetrySeam()
    {
        var injector = new TestFaultInjector(
            LedgerFaultPoint.BeforeTransaction,
            new SqliteException("simulated busy", 5));
        var delay = new RecordingDelay();
        await using var fixture = await LedgerFixture.CreateAsync(
            StorageMode.Global,
            new SqliteLedgerOptions
            {
                BusyTimeout = TimeSpan.Zero,
                RetryDelay = TimeSpan.FromMilliseconds(1),
                MaximumRetryDuration = TimeSpan.FromSeconds(1)
            },
            delay,
            injector);

        var result = await fixture.Store.CommitAsync(fixture.Request("turn", 1, 0, "source", 1, 8));

        Assert.Equal(LedgerCommitStatus.Committed, result.Status);
        Assert.Equal(1, delay.CallCount);
    }

    [Fact]
    public async Task DiagnosticRetentionDoesNotDeleteLedgerProjection()
    {
        await using var fixture = await LedgerFixture.CreateAsync(
            StorageMode.Global,
            new SqliteLedgerOptions
            {
                DiagnosticRetention = new DiagnosticRetentionOptions
                {
                    MaximumAge = TimeSpan.FromDays(14),
                    MaximumApproximateBytes = 1
                }
            });
        await fixture.Store.CommitAsync(fixture.Request("turn", 1, 0, "source", 1, 12));
        var degraded = new LedgerCommitRequest(
            fixture.Route,
            Projection("turn", 2, 0),
            1,
            [new SourceSnapshot(
                "source",
                2,
                null,
                null,
                SourceAvailability.Missing,
                false)]);

        await fixture.Store.CommitAsync(degraded);

        Assert.Empty(await fixture.Store.GetDiagnosticsAsync(fixture.WorkspaceId));
        var stored = await fixture.Store.LoadAsync(fixture.WorkspaceId, "codex", "root-session", "turn");
        Assert.Equal(12, stored?.Projection.Usage.ProcessedTokens);
    }

    [Fact]
    public async Task MigrationFailureLeavesPreviousSchemaUsable()
    {
        var root = LedgerFixture.CreateRoot();
        var path = Path.Combine(root, "ledger.db");
        try
        {
            await using (var original = new SqliteLedgerStore(path))
            {
                await original.EnsureInitializedAsync();
            }

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE diagnostics; PRAGMA user_version = 1;";
                await command.ExecuteNonQueryAsync();
            }

            var injector = new TestFaultInjector(
                SchemaMigrationFaultPoint.AfterStepApplied,
                2,
                new IOException("simulated migration failure"));
            await using var failing = new SqliteLedgerStore(path, faultInjector: injector);
            await Assert.ThrowsAsync<IOException>(() => failing.EnsureInitializedAsync());

            await using var verification = new SqliteConnection($"Data Source={path}");
            await verification.OpenAsync();
            await using var versionCommand = verification.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            Assert.Equal(1L, await versionCommand.ExecuteScalarAsync());
            await using var tableCommand = verification.CreateCommand();
            tableCommand.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='diagnostics';";
            Assert.Equal(0L, await tableCommand.ExecuteScalarAsync());
        }
        finally
        {
            LedgerFixture.DeleteRoot(root);
        }
    }

    private static TurnProjection Projection(string turnId, long revision, long processedTokens) =>
        new(
            new RootTurnKey(ProviderKind.Codex, "root-session", turnId),
            revision,
            new TokenUsage(
                InputTotal: processedTokens,
                UncachedInput: processedTokens,
                CacheRead: 0,
                CacheWrite: 0,
                Output: 0,
                Reasoning: 0,
                ProcessedTokens: processedTokens,
                NativeTotal: processedTokens),
            processedTokens,
            0,
            1,
            processedTokens,
            ExecutionState.Completed,
            MeasurementQuality.Observed,
            RootUsageScope.MainOnly,
            [],
            [SourceCompleteness.Complete("projection-source")],
            [],
            new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));

    private static string ExpectedDiagnostic(SourceAvailability availability) => availability switch
    {
        SourceAvailability.Missing => "source_missing",
        SourceAvailability.Truncated => "source_truncated",
        SourceAvailability.Unreadable => "source_unreadable",
        _ => throw new ArgumentOutOfRangeException(nameof(availability))
    };

    private sealed class LedgerFixture : IAsyncDisposable
    {
        private LedgerFixture(string root, Guid workspaceId, StorageRoute route, SqliteLedgerStore store)
        {
            Root = root;
            WorkspaceId = workspaceId;
            Route = route;
            Store = store;
        }

        public string Root { get; }
        public Guid WorkspaceId { get; }
        public StorageRoute Route { get; }
        public SqliteLedgerStore Store { get; }

        public static async Task<LedgerFixture> CreateAsync(
            StorageMode mode,
            SqliteLedgerOptions? options = null,
            ILedgerDelay? delay = null,
            ILedgerFaultInjector? injector = null,
            IStorageRouteAuthority? routeAuthority = null)
        {
            var root = CreateRoot();
            var dataRoot = mode == StorageMode.Global
                ? Path.Combine(root, "global")
                : Path.Combine(root, "workspace", ".token-meter");
            var workspaceId = Guid.NewGuid();
            var route = new StorageRoute(workspaceId, mode, dataRoot, 1, $"{mode.ToString().ToLowerInvariant()}-store");
            var store = SqliteLedgerStore.FromRoute(
                route, options, delay, injector, routeAuthority: routeAuthority);
            await store.EnsureInitializedAsync();
            return new LedgerFixture(root, workspaceId, route, store);
        }

        public LedgerCommitRequest Request(
            string turnId,
            long revision,
            long expectedRevision,
            string sourceId,
            long sourceGeneration,
            long processedTokens) =>
            new(
                Route,
                Projection(turnId, revision, processedTokens),
                expectedRevision,
                [new SourceSnapshot(
                    sourceId,
                    sourceGeneration,
                    $"hash-{sourceGeneration}",
                    sourceGeneration,
                    SourceAvailability.Available,
                    true)]);

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            DeleteRoot(Root);
        }

        public static string CreateRoot()
        {
            var parent = Path.Combine(Path.GetTempPath(), "task-token-meter-ledger-tests");
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        public static void DeleteRoot(string root)
        {
            var fullRoot = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "task-token-meter-ledger-tests"));
            if (!fullRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the test root.");
            }

            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private sealed class RecordingDelay : ILedgerDelay
    {
        public int CallCount { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableRouteAuthority : IStorageRouteAuthority
    {
        public StorageRoute? ActiveRoute { get; set; }

        public Task<bool> IsActiveAsync(
            StorageRoute route,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(route == ActiveRoute);
    }

    private sealed class TestFaultInjector : ILedgerFaultInjector
    {
        private readonly LedgerFaultPoint? ledgerPoint;
        private readonly SchemaMigrationFaultPoint? migrationPoint;
        private readonly int migrationVersion;
        private readonly Exception exception;
        private int thrown;

        public TestFaultInjector(LedgerFaultPoint point, Exception exception)
        {
            ledgerPoint = point;
            this.exception = exception;
        }

        public TestFaultInjector(
            SchemaMigrationFaultPoint point,
            int migrationVersion,
            Exception exception)
        {
            migrationPoint = point;
            this.migrationVersion = migrationVersion;
            this.exception = exception;
        }

        public Task OnLedgerFaultPointAsync(LedgerFaultPoint point, CancellationToken cancellationToken)
        {
            if (point == ledgerPoint && Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw exception;
            }

            return Task.CompletedTask;
        }

        public Task OnSchemaMigrationFaultPointAsync(
            SchemaMigrationFaultPoint point,
            int targetVersion,
            CancellationToken cancellationToken)
        {
            if (point == migrationPoint &&
                targetVersion == migrationVersion &&
                Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw exception;
            }

            return Task.CompletedTask;
        }
    }
}
