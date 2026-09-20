using System.Text;
using System.Text.Json;
using TaskTokenMeter.Adapters.Claude;
using TaskTokenMeter.Adapters.Codex;
using TaskTokenMeter.Cli;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Storage.Migration;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class AdapterCliRuntimeTests
{
    [Fact]
    public async Task SyncFallbackAndRebuildPreserveStoredRevisionForMissingSource()
    {
        await using var fixture = new RuntimeFixture();
        var runtime = fixture.CreateRuntime();
        var session = Assert.Single(runtime.Discover("codex", "synthetic-codex-session-mismatch"));

        await runtime.SyncAsync(session, fixture.Workspace, default);
        await runtime.SyncAsync(session, fixture.Workspace, default);
        File.Delete(fixture.SourcePath);

        var fallback = Assert.Single(await runtime.RebuildAsync(session, fixture.Workspace, default));
        Assert.Equal(1, fallback.Revision);
        Assert.Contains("stored_fallback", fallback.Diagnostics);
        Assert.Equal(120, fallback.Usage.ProcessedTokens);
    }

    [Fact]
    public async Task SyncUsesStableOpaqueFileManifestAndAdvancesGenerationOnceOnContentChange()
    {
        await using var fixture = new RuntimeFixture();
        var runtime = fixture.CreateRuntime();
        var session = Assert.Single(runtime.Discover("codex", "synthetic-codex-session-mismatch"));
        await runtime.SyncAsync(session, fixture.Workspace, default);
        var status = await runtime.GetStorageStatusAsync(fixture.Workspace, default);
        await using var store = new TaskTokenMeter.Storage.SqliteLedgerStore(Path.Combine(status.ActiveRoute.CanonicalDataRoot, "ledger.db"));
        var first = Assert.Single(await store.LoadSourcesAsync(status.ActiveRoute.WorkspaceId));
        Assert.Matches("^source:codex:[0-9A-F]{64}$", first.SourceId);
        Assert.DoesNotContain(fixture.SourcePath, first.SourceId, StringComparison.Ordinal);
        for (var index = 0; index < 10; index++) await runtime.SyncAsync(session, fixture.Workspace, default);
        var stable = Assert.Single(await store.LoadSourcesAsync(status.ActiveRoute.WorkspaceId));
        Assert.Equal(first, stable);
        await File.AppendAllTextAsync(fixture.SourcePath, Environment.NewLine);
        await runtime.SyncAsync(session, fixture.Workspace, default);
        var changed = Assert.Single(await store.LoadSourcesAsync(status.ActiveRoute.WorkspaceId));
        Assert.Equal(first.Generation + 1, changed.Generation);
        Assert.NotEqual(first.ContentFingerprint, changed.ContentFingerprint);
        await store.DisposeAsync();
        var databaseText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(status.ActiveRoute.CanonicalDataRoot, "ledger.db")));
        Assert.DoesNotContain(fixture.SourcePath, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("token_usage_record", databaseText, StringComparison.Ordinal);
    }
    [Fact]
    public async Task StorageDryRunKeepsRouteAndMigrationSwitchesIt()
    {
        await using var fixture = new RuntimeFixture();
        var runtime = fixture.CreateRuntime();
        var session = Assert.Single(runtime.Discover("codex", "synthetic-codex-session-mismatch"));
        await runtime.SyncAsync(session, fixture.Workspace, default);
        var before = await runtime.GetStorageStatusAsync(fixture.Workspace, default);
        Assert.Equal(StorageMode.Global, before.ActiveRoute.Mode);
        var dryRun = await runtime.MigrateStorageAsync(fixture.Workspace, StorageMode.Workspace, true, default);
        var afterDryRun = await runtime.GetStorageStatusAsync(fixture.Workspace, default);
        var migrated = await runtime.MigrateStorageAsync(fixture.Workspace, StorageMode.Workspace, false, default);

        Assert.Equal(before.ActiveRoute, dryRun.ActiveRoute);
        Assert.Equal(before.ActiveRoute, afterDryRun.ActiveRoute);
        Assert.Equal(StorageMode.Workspace, migrated.ActiveRoute.Mode);
        Assert.NotEqual(before.ActiveRoute.RouteGeneration, migrated.ActiveRoute.RouteGeneration);
    }

    [Fact]
    public async Task UnsupportedConfiguredSourceUsesCodeFiveAndJsonOnlyOnStdout()
    {
        await using var fixture = new RuntimeFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "{\"unsupported\":true}");
        var runtime = fixture.CreateRuntime();
        var console = new CapturingConsole { IsInputRedirected = true, IsOutputRedirected = true };

        var exitCode = await CliApplication.RunAsync(
            ["current", "--provider", "codex", "--session", "synthetic-codex-session-mismatch", "--json"], runtime, console);

        Assert.Equal(5, exitCode);
        using var document = JsonDocument.Parse(console.StandardOutput);
        Assert.Equal("unsupported_schema", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(console.StandardError);
    }

    [Fact]
    public async Task CodexDetailedProjectionPreservesTwoTurnMetadataAndMembership()
    {
        var source = Fixture("codex", "two-turn-snapshots.jsonl");
        var detailed = new CodexUsageAdapter().ReadDetailed(source);
        var runtime = CreateRuntime(ProviderKind.Codex, [source]);
        var session = Assert.Single(runtime.Discover("codex", detailed.Turns[0].RootSessionId));

        var projections = await runtime.ReadTurnsAsync(session, Path.GetTempPath(), default);

        Assert.Equal(2, projections.Count);
        foreach (var projection in projections)
        {
            var expected = Assert.Single(detailed.Turns, turn => turn.RootTurnId == projection.TurnKey.RootTurnId);
            Assert.Equal(RootUsageScope.MainOnly, projection.RootScope);
            Assert.Equal(expected.KnownSubtotal, projection.KnownSubtotal);
            Assert.Equal(expected.UnknownObservationCount, projection.UnknownObservationCount);
            Assert.Equal(expected.Diagnostics.Concat(detailed.Diagnostics).Distinct(), projection.Diagnostics);
            Assert.Matches("^source:codex:[0-9A-F]{64}$", Assert.Single(projection.Sources).SourceId);
            Assert.Equal(expected.Membership.Count, projection.Membership.Count);
            Assert.All(projection.Membership, membership => Assert.Equal(AttributionStatus.Attributed, membership.Status));
            Assert.All(projection.Membership, membership => Assert.True(membership.IsIncludedInAggregate));
        }
    }

    [Fact]
    public async Task ClaudeDetailedProjectionPreservesExecutionDiagnosticsAndMembership()
    {
        var source = Fixture("claude", "two-turn-streaming-alias.jsonl");
        var detailed = new ClaudeAdapter().ReadSnapshot(source);
        Assert.Equal(ClaudeReadStatus.Supported, detailed.Status);
        var runtime = CreateRuntime(ProviderKind.Claude, [source]);
        var session = Assert.Single(runtime.Discover("claude", detailed.Turns[0].RootSessionId));

        var projections = await runtime.ReadTurnsAsync(session, Path.GetTempPath(), default);

        Assert.Equal(detailed.Turns.Count, projections.Count);
        foreach (var projection in projections)
        {
            var expected = Assert.Single(detailed.Turns, turn => turn.RootTurnId == projection.TurnKey.RootTurnId);
            Assert.Equal(RootUsageScope.MainOnly, projection.RootScope);
            Assert.Equal(expected.ExecutionState == ClaudeExecutionState.Completed ? ExecutionState.Completed : ExecutionState.Unknown, projection.ExecutionState);
            Assert.Equal(expected.KnownSubtotal, projection.KnownSubtotal);
            Assert.Equal(expected.Diagnostics.Concat(detailed.Diagnostics).Distinct(), projection.Diagnostics);
            Assert.Equal(expected.Membership.Count, projection.Membership.Count);
            Assert.All(projection.Membership, membership => Assert.Equal(AttributionStatus.Attributed, membership.Status));
        }
    }

    [Fact]
    public async Task ClaudeCrossFileReplayProjectionIsInvariantToInputOrder()
    {
        var sourceA = Fixture("claude", "cross-file-replay", "source-a.jsonl");
        var sourceB = Fixture("claude", "cross-file-replay", "source-b.jsonl");
        var first = await ReadOnlyProjectionAsync(ProviderKind.Claude, [sourceA, sourceB], "synthetic-claude-session-replay");
        var second = await ReadOnlyProjectionAsync(ProviderKind.Claude, [sourceB, sourceA], "synthetic-claude-session-replay");

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    private static AdapterCliRuntime CreateRuntime(ProviderKind provider, IReadOnlyList<string> sources) => new(
        Path.Combine(Path.GetTempPath(), "task-token-meter-runtime", Guid.NewGuid().ToString("N")),
        sourceRoots: new Dictionary<ProviderKind, IReadOnlyList<string>> { [provider] = sources });

    private static async Task<IReadOnlyList<TaskTokenMeter.Core.Projection.TurnProjection>> ReadOnlyProjectionAsync(
        ProviderKind provider, IReadOnlyList<string> sources, string sessionId)
    {
        var runtime = CreateRuntime(provider, sources);
        var session = Assert.Single(runtime.Discover(provider.ToString(), sessionId));
        return await runtime.ReadTurnsAsync(session, Path.GetTempPath(), default);
    }

    private static string Fixture(params string[] parts) => Path.GetFullPath(Path.Combine([
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", ..parts]));
    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "task-token-meter-runtime", Guid.NewGuid().ToString("N"));
        public RuntimeFixture()
        {
            Workspace = Path.Combine(root, "한글 workspace");
            DataRoot = Path.Combine(root, "data");
            SourcePath = Path.Combine(root, "fixture.jsonl");
            Directory.CreateDirectory(Workspace);
            var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "codex", "delta-snapshot-mismatch.jsonl"));
            File.Copy(fixture, SourcePath);
        }

        public string Workspace { get; }
        public string DataRoot { get; }
        public string SourcePath { get; }
        public AdapterCliRuntime CreateRuntime() => new(
            DataRoot,
            sourceRoots: new Dictionary<ProviderKind, IReadOnlyList<string>> { [ProviderKind.Codex] = [SourcePath] });
        public static string Record(int output) => "{\"timestamp\":\"2026-09-20T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\",\"session_id\":\"runtime-session\",\"thread_id\":\"runtime-thread\",\"turn_id\":\"runtime-turn\",\"root_turn_id\":\"runtime-turn\",\"response_id\":\"runtime-response\",\"usage\":{\"input_tokens\":10,\"cached_input_tokens\":0,\"output_tokens\":" + output + ",\"reasoning_output_tokens\":0,\"total_tokens\":" + (10 + output) + ",\"cache_write_input_tokens\":0},\"turn_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":0,\"output_tokens\":" + output + ",\"reasoning_output_tokens\":0,\"total_tokens\":" + (10 + output) + ",\"cache_write_input_tokens\":0}}}";
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingConsole : ICliConsole
    {
        public bool IsInputRedirected { get; init; }
        public bool IsOutputRedirected { get; init; }
        public bool IsColorEnabled => false;
        public string StandardOutput { get; private set; } = string.Empty;
        public string StandardError { get; private set; } = string.Empty;
        public void Write(string value) => StandardOutput += value;
        public void WriteError(string value) => StandardError += value;
        public TaskTokenMeter.Cli.Selection.SessionSelectionInput ReadInput() => TaskTokenMeter.Cli.Selection.SessionSelectionInput.EndOfFile;
    }
}
