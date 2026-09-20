using Microsoft.Data.Sqlite;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Storage;
using TaskTokenMeter.Storage.Migration;
using TaskTokenMeter.Storage.Routing;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class StorageMigrationTests
{
    [Fact]
    public async Task GlobalWorkspaceGlobalRoundTripPreservesProjectionAndSupersededUpdates()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.CommitAsync(fixture.InitialRoute, revision: 1, expectedRevision: 0, processedTokens: 100);

        var toWorkspace = await fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));
        var workspaceProjection = await fixture.LoadAsync(toWorkspace.ActiveRoute);

        Assert.Equal(100, workspaceProjection.Projection.Usage.ProcessedTokens);
        Assert.Equal(MeasurementQuality.Observed, workspaceProjection.Projection.MeasurementQuality);
        Assert.Equal(2, workspaceProjection.Projection.Membership.Count);
        Assert.Contains(workspaceProjection.Projection.Membership, item =>
            item.Execution.OriginSessionId == "child-session" && item.Execution.ExecutionId == "child-execution");

        await fixture.CommitAsync(toWorkspace.ActiveRoute, revision: 2, expectedRevision: 1, processedTokens: 175);
        var toGlobal = await fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Global));
        var globalProjection = await fixture.LoadAsync(toGlobal.ActiveRoute);

        Assert.Equal(2, globalProjection.Projection.Revision);
        Assert.Equal(175, globalProjection.Projection.Usage.ProcessedTokens);
        Assert.Equal(MeasurementQuality.Observed, globalProjection.Projection.MeasurementQuality);
        Assert.Equal(workspaceProjection.Projection.Membership, globalProjection.Projection.Membership);
        Assert.NotEqual(fixture.InitialRoute.RouteGeneration, toGlobal.ActiveRoute.RouteGeneration);
        Assert.True(await fixture.Registry.IsActiveAsync(toGlobal.ActiveRoute));
    }

    [Fact]
    public async Task MigrationPreservesAnotherWorkspaceInSharedGlobalLedgerAndRegistry()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 20);
        var otherWorkspace = Path.Combine(fixture.Root, "other-workspace");
        Directory.CreateDirectory(otherWorkspace);
        var other = await fixture.Registry.InitializeAsync(
            otherWorkspace, StorageMode.Global, fixture.GlobalRoot);
        await fixture.CommitAsync(other.ActiveRoute, 1, 0, 33, "other-turn", "other-source");

        var concurrentWorkspace = Path.Combine(fixture.Root, "concurrent-workspace");
        Directory.CreateDirectory(concurrentWorkspace);
        var migrationTask = fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));
        var concurrentRegistrationTask = fixture.Registry.InitializeAsync(
            concurrentWorkspace,
            StorageMode.Global,
            fixture.GlobalRoot);
        await Task.WhenAll(migrationTask, concurrentRegistrationTask);
        var concurrentRegistration = await concurrentRegistrationTask;

        var otherStored = await fixture.LoadAsync(other.ActiveRoute, "other-turn");
        Assert.Equal(33, otherStored.Projection.Usage.ProcessedTokens);
        Assert.True(await fixture.Registry.IsActiveAsync(other.ActiveRoute));
        Assert.True(await fixture.Registry.IsActiveAsync(concurrentRegistration.ActiveRoute));
    }

    [Fact]
    public async Task RepeatingCompletedMigrationIsIdempotent()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 50);
        var first = await fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));

        var retry = await fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));

        Assert.Equal(StorageMigrationOutcome.AlreadyActive, retry.Outcome);
        Assert.Equal(first.ActiveRoute, retry.ActiveRoute);
        Assert.Equal(50, (await fixture.LoadAsync(retry.ActiveRoute)).Projection.Usage.ProcessedTokens);
    }

    [Fact]
    public async Task DryRunAndStatusDoNotChangeTheActiveRoute()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 50);

        var result = await fixture.Service.MigrateAsync(
            new StorageMigrationRequest(
                fixture.WorkspaceRoot,
                StorageMode.Workspace,
                DryRun: true));
        var status = await fixture.Service.GetStatusAsync(fixture.WorkspaceRoot);

        Assert.Equal(StorageMigrationOutcome.DryRun, result.Outcome);
        Assert.Equal(fixture.InitialRoute, result.ActiveRoute);
        Assert.True(await fixture.Registry.IsActiveAsync(fixture.InitialRoute));
        Assert.Equal(1, status.ActiveRecords.TurnCount);
        Assert.False(File.Exists(Path.Combine(fixture.WorkspaceRoot, ".token-meter", "ledger.db")));
    }
    [Fact]
    public async Task IndependentDestinationConflictLeavesSourceActive()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 50);
        var destinationRoot = Path.Combine(fixture.WorkspaceRoot, ".token-meter");
        var independentRoute = new StorageRoute(
            fixture.InitialRoute.WorkspaceId,
            StorageMode.Workspace,
            destinationRoot,
            91,
            "independent-store");
        await fixture.CommitAsync(independentRoute, 1, 0, 999, useAuthority: false);

        await Assert.ThrowsAsync<StorageMigrationConflictException>(() => fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace)));

        Assert.True(await fixture.Registry.IsActiveAsync(fixture.InitialRoute));
        Assert.Equal(50, (await fixture.LoadAsync(fixture.InitialRoute)).Projection.Usage.ProcessedTokens);
    }

    [Theory]
    [InlineData(StorageMigrationFaultPoint.AfterPlanJournal)]
    [InlineData(StorageMigrationFaultPoint.AfterBackup)]
    [InlineData(StorageMigrationFaultPoint.BeforeDestinationCommit)]
    [InlineData(StorageMigrationFaultPoint.AfterDestinationCommit)]
    [InlineData(StorageMigrationFaultPoint.BeforeRouteSwitch)]
    [InlineData(StorageMigrationFaultPoint.AfterRouteSwitch)]
    [InlineData(StorageMigrationFaultPoint.BeforeJournalComplete)]
    public async Task RetryAfterEveryCommitBoundaryConverges(StorageMigrationFaultPoint point)
    {
        await using var fixture = await MigrationFixture.CreateAsync(new ThrowOnceMigrationFault(point));
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 70);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace)));

        var recoveryService = fixture.CreateService();
        var recovered = await recoveryService.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));

        Assert.Contains(recovered.Outcome, new[] { StorageMigrationOutcome.Migrated, StorageMigrationOutcome.AlreadyActive });
        Assert.Equal(StorageMode.Workspace, recovered.ActiveRoute.Mode);
        Assert.Equal(70, (await fixture.LoadAsync(recovered.ActiveRoute)).Projection.Usage.ProcessedTokens);
    }

    [Fact]
    public async Task LateWriterUsingSupersededRouteIsRejected()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 10);
        await using var lateStore = SqliteLedgerStore.FromRoute(
            fixture.InitialRoute, routeAuthority: fixture.Registry);
        await lateStore.EnsureInitializedAsync();

        await fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));
        var result = await lateStore.CommitAsync(fixture.Request(fixture.InitialRoute, 2, 1, 20));

        Assert.Equal(LedgerCommitStatus.RouteConflict, result.Status);
    }

    [Fact]
    public async Task WriterDuringMigrationWaitsAndIsRejectedAfterRouteSwitch()
    {
        var fault = new BlockingMigrationFault(StorageMigrationFaultPoint.BeforeRouteSwitch);
        await using var fixture = await MigrationFixture.CreateAsync(fault);
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 10);
        var migration = fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));
        await fault.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var writer = SqliteLedgerStore.FromRoute(
            fixture.InitialRoute, routeAuthority: fixture.Registry);
        var write = writer.CommitAsync(fixture.Request(fixture.InitialRoute, 2, 1, 20));
        await Task.Delay(50);
        Assert.False(write.IsCompleted);

        fault.Release.TrySetResult();
        var migrated = await migration;
        var result = await write;

        Assert.Equal(StorageMode.Workspace, migrated.ActiveRoute.Mode);
        Assert.Equal(LedgerCommitStatus.RouteConflict, result.Status);
        Assert.Equal(10, (await fixture.LoadAsync(migrated.ActiveRoute)).Projection.Usage.ProcessedTokens);
    }

    [Fact]
    public async Task SourceChangeBeforeRouteSwitchKeepsOldRouteAndRetryReplansJournal()
    {
        var fault = new CallbackMigrationFault(StorageMigrationFaultPoint.BeforeRouteSwitch);
        await using var fixture = await MigrationFixture.CreateAsync(fault);
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 10);
        fault.Callback = () => fixture.CommitAsync(
            fixture.InitialRoute, 2, 1, 25, useAuthority: false);

        await Assert.ThrowsAsync<StorageMigrationConflictException>(() => fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace)));

        Assert.True(await fixture.Registry.IsActiveAsync(fixture.InitialRoute));
        Assert.Equal(25, (await fixture.LoadAsync(fixture.InitialRoute)).Projection.Usage.ProcessedTokens);

        var recovered = await fixture.CreateService().MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));
        Assert.Equal(25, (await fixture.LoadAsync(recovered.ActiveRoute)).Projection.Usage.ProcessedTokens);
    }

    [Fact]
    public async Task DiskFailureLeavesSourceActiveAndRetrySucceeds()
    {
        await using var fixture = await MigrationFixture.CreateAsync(
            new ThrowOnceMigrationFault(StorageMigrationFaultPoint.BeforeDestinationCommit));
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 88);

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace)));
        Assert.True(await fixture.Registry.IsActiveAsync(fixture.InitialRoute));

        var recoveryService = fixture.CreateService();
        var result = await recoveryService.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace));
        Assert.Equal(88, (await fixture.LoadAsync(result.ActiveRoute)).Projection.Usage.ProcessedTokens);
    }

    [Fact]
    public async Task GitExcludeFailureStopsBeforeDestinationWrite()
    {
        await using var fixture = await MigrationFixture.CreateAsync(
            gitExcludeManager: new FailingGitExcludeManager());
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 44);

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace)));

        Assert.True(await fixture.Registry.IsActiveAsync(fixture.InitialRoute));
        Assert.False(File.Exists(Path.Combine(fixture.WorkspaceRoot, ".token-meter", "ledger.db")));
    }

    [Fact]
    public async Task ReadOnlyDestinationProbeStopsBeforeRouteSwitch()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.CommitAsync(fixture.InitialRoute, 1, 0, 29);
        var fileSystem = new DenyWorkspaceWriteFileSystem(
            new PhysicalStorageFileSystem(), Path.Combine(fixture.WorkspaceRoot, ".token-meter"));
        var service = fixture.CreateService(fileSystem: fileSystem);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MigrateAsync(
            new StorageMigrationRequest(fixture.WorkspaceRoot, StorageMode.Workspace)));

        Assert.True(await fixture.Registry.IsActiveAsync(fixture.InitialRoute));
    }

    [Fact]
    public async Task GitExcludeAppendPreservesExistingEntriesAndLineEndings()
    {
        var root = MigrationFixture.CreateRoot();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var exclude = Path.Combine(workspace, ".git", "info", "exclude");
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            await File.WriteAllTextAsync(exclude, "# local\r\n/build/\r\n");
            var manager = new GitExcludeManager(commandRunner: new EmptyGitCommandRunner());

            await manager.EnsureExcludedAsync(workspace);
            await manager.EnsureExcludedAsync(workspace);

            var contents = await File.ReadAllTextAsync(exclude);
            Assert.Equal("# local\r\n/build/\r\n/.token-meter/\r\n", contents);
        }
        finally
        {
            MigrationFixture.DeleteRoot(root);
        }
    }

    private sealed class MigrationFixture : IAsyncDisposable
    {
        private MigrationFixture(
            string root,
            FileStorageLockManager lockManager,
            FileStorageRouteRegistry registry,
            RegisteredStorageRoute registration,
            StorageMigrationService service,
            IGitExcludeManager? gitExcludeManager)
        {
            Root = root;
            WorkspaceRoot = registration.CanonicalWorkspaceRoot;
            GlobalRoot = registration.ActiveRoute.CanonicalDataRoot;
            LockManager = lockManager;
            Registry = registry;
            InitialRoute = registration.ActiveRoute;
            Service = service;
            this.gitExcludeManager = gitExcludeManager;
        }

        private readonly IGitExcludeManager? gitExcludeManager;
        public string Root { get; }
        public string WorkspaceRoot { get; }
        public string GlobalRoot { get; }
        public FileStorageLockManager LockManager { get; }
        public FileStorageRouteRegistry Registry { get; }
        public StorageRoute InitialRoute { get; }
        public StorageMigrationService Service { get; }

        public static async Task<MigrationFixture> CreateAsync(
            IStorageMigrationFaultInjector? faultInjector = null,
            IGitExcludeManager? gitExcludeManager = null)
        {
            var root = CreateRoot();
            var workspace = Path.Combine(root, "workspace");
            var global = Path.Combine(root, "global");
            Directory.CreateDirectory(workspace);
            var locks = new FileStorageLockManager(Path.Combine(root, "state", "locks"));
            var registry = new FileStorageRouteRegistry(Path.Combine(root, "state", "config.json"), locks);
            var registration = await registry.InitializeAsync(workspace, StorageMode.Global, global);
            var service = new StorageMigrationService(
                registry,
                locks,
                global,
                Path.Combine(root, "state", "migration"),
                gitExcludeManager,
                faultInjector: faultInjector);
            return new MigrationFixture(root, locks, registry, registration, service, gitExcludeManager);
        }

        public StorageMigrationService CreateService(
            IStorageFileSystem? fileSystem = null) =>
            new(
                Registry,
                LockManager,
                GlobalRoot,
                Path.Combine(Root, "state", "migration"),
                gitExcludeManager,
                fileSystem);

        public async Task CommitAsync(
            StorageRoute route,
            long revision,
            long expectedRevision,
            long processedTokens,
            string turnId = "root-turn",
            string sourceId = "source",
            bool useAuthority = true)
        {
            await using var store = SqliteLedgerStore.FromRoute(
                route,
                routeAuthority: useAuthority ? Registry : null);
            await store.EnsureInitializedAsync();
            var request = Request(route, revision, expectedRevision, processedTokens, turnId, sourceId);
            var result = await store.CommitAsync(request);
            Assert.Equal(LedgerCommitStatus.Committed, result.Status);
        }

        public LedgerCommitRequest Request(
            StorageRoute route,
            long revision,
            long expectedRevision,
            long processedTokens,
            string turnId = "root-turn",
            string sourceId = "source")
        {
            var expectedPrefix = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            Assert.StartsWith(
                expectedPrefix,
                Path.GetFullPath(route.CanonicalDataRoot),
                StringComparison.OrdinalIgnoreCase);
            return new LedgerCommitRequest(
                route,
                Projection(turnId, revision, processedTokens),
                expectedRevision,
                [new SourceSnapshot(sourceId, revision, $"hash-{revision}", revision, SourceAvailability.Available, true)]);
        }

        public async Task<StoredTurnProjection> LoadAsync(StorageRoute route, string turnId = "root-turn")
        {
            var expectedPrefix = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            Assert.StartsWith(
                expectedPrefix,
                Path.GetFullPath(route.CanonicalDataRoot),
                StringComparison.OrdinalIgnoreCase);
            await using var store = SqliteLedgerStore.FromRoute(route);
            return await store.LoadAsync(route.WorkspaceId, "codex", "root-session", turnId)
                ?? throw new Xunit.Sdk.XunitException("Expected a stored projection.");
        }

        private static TurnProjection Projection(string turnId, long revision, long processedTokens)
        {
            var root = new RootTurnKey(ProviderKind.Codex, "root-session", turnId);
            return new TurnProjection(
                root,
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
                2,
                processedTokens,
                ExecutionState.Completed,
                MeasurementQuality.Observed,
                RootUsageScope.MainOnly,
                [
                    new Membership(
                        root,
                        new ExecutionIdentity(ProviderKind.Codex, "root-session", "root-execution"),
                        AttributionStatus.Attributed,
                        "root",
                        true),
                    new Membership(
                        root,
                        new ExecutionIdentity(ProviderKind.Codex, "child-session", "child-execution"),
                        AttributionStatus.Attributed,
                        "parent-metadata",
                        true)
                ],
                [SourceCompleteness.Complete("projection-source")],
                [],
                new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            DeleteRoot(Root);
            return ValueTask.CompletedTask;
        }

        public static string CreateRoot()
        {
            var parent = Path.Combine(Path.GetTempPath(), "task-token-meter-migration-tests");
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        public static void DeleteRoot(string root)
        {
            var fullRoot = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "task-token-meter-migration-tests"));
            if (!fullRoot.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the migration test root.");
            }

            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private sealed class BlockingMigrationFault(StorageMigrationFaultPoint target) : IStorageMigrationFaultInjector
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnFaultPointAsync(StorageMigrationFaultPoint point, CancellationToken cancellationToken)
        {
            if (point != target)
            {
                return;
            }

            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CallbackMigrationFault(StorageMigrationFaultPoint target) : IStorageMigrationFaultInjector
    {
        private int invoked;
        public Func<Task>? Callback { get; set; }

        public async Task OnFaultPointAsync(StorageMigrationFaultPoint point, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point == target && Interlocked.Exchange(ref invoked, 1) == 0 && Callback is not null)
            {
                await Callback();
            }
        }
    }

    private sealed class ThrowOnceMigrationFault(StorageMigrationFaultPoint target) : IStorageMigrationFaultInjector
    {
        private int thrown;

        public Task OnFaultPointAsync(StorageMigrationFaultPoint point, CancellationToken cancellationToken)
        {
            if (point == target && Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new IOException($"Simulated failure at {point}.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingGitExcludeManager : IGitExcludeManager
    {
        public Task<GitExcludePlan> PreviewAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitExcludePlan(true, Path.Combine(workspaceRoot, ".git", "info", "exclude"), true, false));

        public Task<GitExcludePlan> EnsureExcludedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated Git exclude failure.");
    }

    private sealed class EmptyGitCommandRunner : IGitCommandRunner
    {
        public Task<IReadOnlyList<string>> ListTrackedStorageFilesAsync(
            string workspaceRoot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class DenyWorkspaceWriteFileSystem(
        IStorageFileSystem inner,
        string deniedRoot) : IStorageFileSystem
    {
        private readonly string deniedRoot = Path.GetFullPath(deniedRoot);

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public void WriteAllText(string path, string contents) => inner.WriteAllText(path, contents);
        public void MoveFile(string source, string destination, bool overwrite) => inner.MoveFile(source, destination, overwrite);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public string GetFullPath(string path) => inner.GetFullPath(path);

        public Stream OpenFile(string path, FileMode mode, FileAccess access, FileShare share)
        {
            if (access != FileAccess.Read && Path.GetFullPath(path).StartsWith(
                    deniedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Simulated read-only destination.");
            }

            return inner.OpenFile(path, mode, access, share);
        }
    }
}
