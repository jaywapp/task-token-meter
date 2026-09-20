using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskTokenMeter.Adapters.Claude;
using TaskTokenMeter.Adapters.Codex;
using TaskTokenMeter.Cli;
using TaskTokenMeter.Cli.Hooks;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Storage.Migration;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class EndToEndTests
{
    [Theory]
    [InlineData(ProviderKind.Claude, "claude", "two-turn-streaming-alias.jsonl", "synthetic-claude-session-two-turn", 438L, "")]
    [InlineData(ProviderKind.Codex, "codex", "two-turn-snapshots.jsonl", "synthetic-codex-session-two-turn", 300L, "{}")]
    public async Task SyntheticProviderFlowPreservesUsageQualityAndAttributionAcrossCliLedgerMigrationAndHook(
        ProviderKind provider,
        string providerName,
        string fixtureName,
        string sessionId,
        long expectedSessionTokens,
        string expectedHookOutput)
    {
        await using var fixture = new EndToEndFixture(provider, providerName, fixtureName, sessionId);
        var runtime = fixture.CreateRuntime();
        var session = Assert.Single(runtime.Discover(providerName, sessionId));

        var adapterOracle = fixture.ReadAdapterOracle();
        var discovered = await runtime.ReadTurnsAsync(session, fixture.Workspace, default);
        Assert.Equal(2, discovered.Count);
        Assert.Equal(expectedSessionTokens, discovered.Sum(static turn => turn.Usage.ProcessedTokens));
        Assert.All(discovered, static turn =>
        {
            Assert.Equal(MeasurementQuality.Observed, turn.MeasurementQuality);
            Assert.NotEmpty(turn.Membership);
            Assert.All(turn.Membership, static membership =>
            {
                Assert.Equal(TaskTokenMeter.Core.Attribution.AttributionStatus.Attributed, membership.Status);
                Assert.True(membership.IsIncludedInAggregate);
            });
        });
        AssertProjectionSemanticsEqual(adapterOracle, discovered);

        var currentJson = await RunJsonAsync(runtime, fixture.Workspace, "current", providerName, sessionId);
        var currentText = await RunTextAsync(runtime, fixture.Workspace, "current", providerName, sessionId);
        AssertTextAndJsonEqual(currentText, currentJson.RootElement.GetProperty("turns")[0]);

        var lastJson = await RunJsonAsync(runtime, fixture.Workspace, "last", providerName, sessionId);
        Assert.Equal(MeasurementQuality.Provisional.ToString().ToLowerInvariant(),
            lastJson.RootElement.GetProperty("turns")[0].GetProperty("measurement").GetString());
        Assert.Equal(discovered[^1].Usage.ProcessedTokens,
            lastJson.RootElement.GetProperty("turns")[0].GetProperty("processedTokens").GetInt64());

        var turnsJson = await RunJsonAsync(runtime, fixture.Workspace, "turns", providerName, sessionId);
        Assert.Equal(discovered.Count, turnsJson.RootElement.GetProperty("turns").GetArrayLength());
        AssertJsonMatchesProjections(turnsJson.RootElement.GetProperty("turns"), discovered);

        var syncedJson = await RunJsonAsync(runtime, fixture.Workspace, "sync", providerName, sessionId);
        AssertJsonMatchesProjections(syncedJson.RootElement.GetProperty("turns"), discovered);
        fixture.RemoveSource();

        var storedRuntime = fixture.CreateRuntime();
        var stored = await storedRuntime.ReadTurnsAsync(session, fixture.Workspace, default);
        AssertProjectionUsageEqual(discovered, stored);
        Assert.All(stored, static turn =>
        {
            Assert.Equal(MeasurementQuality.Provisional, turn.MeasurementQuality);
            Assert.Contains("stored_fallback", turn.Diagnostics);
        });

        var workspaceMigration = await storedRuntime.MigrateStorageAsync(
            fixture.Workspace, StorageMode.Workspace, false, default);
        Assert.Equal(StorageMode.Workspace, workspaceMigration.ActiveRoute.Mode);
        var afterWorkspaceMigration = await storedRuntime.ReadTurnsAsync(session, fixture.Workspace, default);
        AssertProjectionUsageEqual(stored, afterWorkspaceMigration);

        var globalMigration = await storedRuntime.MigrateStorageAsync(
            fixture.Workspace, StorageMode.Global, false, default);
        Assert.Equal(StorageMode.Global, globalMigration.ActiveRoute.Mode);
        var afterRoundTrip = await storedRuntime.ReadTurnsAsync(session, fixture.Workspace, default);
        AssertProjectionUsageEqual(stored, afterRoundTrip);

        fixture.RestoreSource();
        var diagnostics = new CapturingDiagnostics();
        var launcher = new InProcessWorkerLauncher(fixture.DataRoot, diagnostics);
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            session_id = sessionId,
            turn_id = provider == ProviderKind.Codex ? discovered[^1].TurnKey.RootTurnId : null,
            cwd = fixture.Workspace,
            transcript_path = fixture.SourcePath,
            prompt = "synthetic body ignored by Hook validation",
            tool_input = new { secret = "synthetic ignored value" }
        });
        var hookOutput = new StringWriter(CultureInfo.InvariantCulture);
        var hookCode = await HookEntryPoint.RunAsync(
            provider == ProviderKind.Claude ? HookProvider.Claude : HookProvider.Codex,
            new StringReader(payload),
            hookOutput,
            fixture.Validator(),
            launcher,
            diagnostics,
            fixture.DiagnosticsPath);

        Assert.Equal(0, hookCode);
        Assert.Equal(expectedHookOutput, hookOutput.ToString());
        Assert.Equal(0, launcher.WorkerExitCode);
        Assert.Empty(diagnostics.Codes);

        fixture.RemoveSource();
        var hookStored = await fixture.CreateRuntime().ReadTurnsAsync(session, fixture.Workspace, default);
        AssertProjectionUsageEqual(discovered, hookStored);
    }

    private static async Task<JsonDocument> RunJsonAsync(
        AdapterCliRuntime runtime,
        string workspace,
        string command,
        string provider,
        string session)
    {
        var console = new CapturingConsole { IsInputRedirected = true, IsOutputRedirected = true };
        var exitCode = await CliApplication.RunAsync(
            [command, "--provider", provider, "--session", session, "--workspace", workspace, "--json"],
            runtime,
            console);
        Assert.Equal(0, exitCode);
        Assert.Empty(console.StandardError);
        var result = JsonDocument.Parse(console.StandardOutput);
        Assert.Equal(1, result.RootElement.GetProperty("schemaVersion").GetInt32());
        return result;
    }

    private static async Task<string> RunTextAsync(
        AdapterCliRuntime runtime,
        string workspace,
        string command,
        string provider,
        string session)
    {
        var console = new CapturingConsole { IsInputRedirected = true, IsOutputRedirected = true };
        var exitCode = await CliApplication.RunAsync(
            [command, "--provider", provider, "--session", session, "--workspace", workspace, "--non-interactive"],
            runtime,
            console);
        Assert.Equal(0, exitCode);
        Assert.Empty(console.StandardError);
        return console.StandardOutput;
    }

    private static void AssertTextAndJsonEqual(string text, JsonElement turn)
    {
        Assert.Contains("Measurement   " + turn.GetProperty("measurement").GetString()!.ToLowerInvariant(), text, StringComparison.Ordinal);
        Assert.Contains("scope: " + turn.GetProperty("scope").GetString()!.ToLowerInvariant(), text, StringComparison.Ordinal);
        AssertTextNumber(text, "Fresh input", turn.GetProperty("freshInput"));
        AssertTextNumber(text, "Input (total)", turn.GetProperty("inputTotal"));
        AssertTextNumber(text, "Cache read", turn.GetProperty("cacheRead"));
        AssertTextNumber(text, "Cache write", turn.GetProperty("cacheWrite"));
        AssertTextNumber(text, "Output", turn.GetProperty("output"));
        AssertTextNumber(text, "Reasoning", turn.GetProperty("reasoning"));
        AssertTextNumber(text, "Processed", turn.GetProperty("processedTokens"));
        AssertTextNumber(text, "API calls", turn.GetProperty("apiCalls"));
    }

    private static void AssertTextNumber(string text, string label, JsonElement value)
    {
        var expected = value.ValueKind == JsonValueKind.Null
            ? "N/A"
            : value.GetInt64().ToString("N0", CultureInfo.InvariantCulture);
        Assert.Contains(label.PadRight(14) + expected, text, StringComparison.Ordinal);
    }

    private static void AssertJsonMatchesProjections(JsonElement jsonTurns, IReadOnlyList<TurnProjection> projections)
    {
        var elements = jsonTurns.EnumerateArray().ToArray();
        Assert.Equal(projections.Count, elements.Length);
        for (var index = 0; index < projections.Count; index++)
        {
            var actual = elements[index];
            var expected = projections[index];
            Assert.Equal(expected.TurnKey.RootTurnId, actual.GetProperty("turnId").GetString());
            Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(expected.MeasurementQuality.ToString()), actual.GetProperty("measurement").GetString());
            Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(expected.RootScope.ToString()), actual.GetProperty("scope").GetString());
            Assert.Equal(expected.Usage.InputTotal, NullableInt64(actual.GetProperty("inputTotal")));
            Assert.Equal(expected.Usage.UncachedInput, NullableInt64(actual.GetProperty("freshInput")));
            Assert.Equal(expected.Usage.CacheRead, NullableInt64(actual.GetProperty("cacheRead")));
            Assert.Equal(expected.Usage.CacheWrite, NullableInt64(actual.GetProperty("cacheWrite")));
            Assert.Equal(expected.Usage.Output, NullableInt64(actual.GetProperty("output")));
            Assert.Equal(expected.Usage.Reasoning, NullableInt64(actual.GetProperty("reasoning")));
            Assert.Equal(expected.Usage.ProcessedTokens, NullableInt64(actual.GetProperty("processedTokens")));
            Assert.Equal(expected.Usage.NativeTotal, NullableInt64(actual.GetProperty("nativeTotal")));
            Assert.Equal(expected.ApiCallCount, NullableInt32(actual.GetProperty("apiCalls")));
        }
    }

    private static long? NullableInt64(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    private static int? NullableInt32(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();

    private static void AssertProjectionSemanticsEqual(
        IReadOnlyList<TurnProjection> expected,
        IReadOnlyList<TurnProjection> actual)
    {
        AssertProjectionUsageEqual(expected, actual);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].MeasurementQuality, actual[index].MeasurementQuality);
            Assert.Equal(expected[index].RootScope, actual[index].RootScope);
            Assert.Equal(expected[index].ApiCallCount, actual[index].ApiCallCount);
            Assert.Equal(expected[index].UnknownObservationCount, actual[index].UnknownObservationCount);
            Assert.Equal(expected[index].Membership, actual[index].Membership);
        }
    }

    private static void AssertProjectionUsageEqual(
        IReadOnlyList<TurnProjection> expected,
        IReadOnlyList<TurnProjection> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].TurnKey, actual[index].TurnKey);
            Assert.Equal(expected[index].Usage, actual[index].Usage);
            Assert.Equal(expected[index].KnownSubtotal, actual[index].KnownSubtotal);
            Assert.Equal(expected[index].UnknownObservationCount, actual[index].UnknownObservationCount);
        }
    }

    private sealed class EndToEndFixture : IAsyncDisposable
    {
        private readonly ProviderKind provider;
        private readonly string fixturePath;
        private readonly string root = Path.Combine(Path.GetTempPath(), "task-token-meter-e2e", Guid.NewGuid().ToString("N"));

        public EndToEndFixture(ProviderKind provider, string providerName, string fixtureName, string sessionId)
        {
            this.provider = provider;
            ProviderName = providerName;
            SessionId = sessionId;
            fixturePath = Fixture(providerName, fixtureName);
            Workspace = Path.Combine(root, "한글 workspace");
            DataRoot = Path.Combine(root, "data root");
            SourceRoot = Path.Combine(root, "provider sources");
            SourcePath = Path.Combine(SourceRoot, fixtureName);
            DiagnosticsPath = Path.Combine(root, "diagnostics", "hooks.jsonl");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(SourceRoot);
            File.Copy(fixturePath, SourcePath);
        }

        public string ProviderName { get; }
        public string SessionId { get; }
        public string Workspace { get; }
        public string DataRoot { get; }
        public string SourceRoot { get; }
        public string SourcePath { get; }
        public string DiagnosticsPath { get; }

        public AdapterCliRuntime CreateRuntime() => new(
            DataRoot,
            sourceRoots: new Dictionary<ProviderKind, IReadOnlyList<string>> { [provider] = [SourceRoot] });

        public IReadOnlyList<TurnProjection> ReadAdapterOracle()
        {
            var runtime = CreateRuntime();
            var session = Assert.Single(runtime.Discover(ProviderName, SessionId));
            return runtime.ReadTurnsAsync(session, Workspace, default).GetAwaiter().GetResult();
        }

        public HookContextValidator Validator() => new(
            new Dictionary<HookProvider, IReadOnlyList<string>>
            {
                [provider == ProviderKind.Claude ? HookProvider.Claude : HookProvider.Codex] = [SourceRoot]
            });

        public void RemoveSource() => File.Delete(SourcePath);
        public void RestoreSource() => File.Copy(fixturePath, SourcePath, true);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }

        private static string Fixture(params string[] parts) => Path.GetFullPath(Path.Combine([
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", ..parts]));
    }

    private sealed class InProcessWorkerLauncher(
        string dataRoot,
        IHookDiagnosticSink diagnostics) : IHookWorkerLauncher
    {
        public int? WorkerExitCode { get; private set; }

        public void Start(HookWorkerRequest request)
        {
            WorkerExitCode = HookWorker.RunAsync(
                request,
                () => new AdapterHookAggregationRuntime(dataRoot),
                diagnostics).GetAwaiter().GetResult();
        }
    }

    private sealed class CapturingDiagnostics : IHookDiagnosticSink
    {
        public List<string> Codes { get; } = [];

        public void Record(string code, HookProvider provider, string? eventName, TimeSpan duration) => Codes.Add(code);
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
        public TaskTokenMeter.Cli.Selection.SessionSelectionInput ReadInput() =>
            TaskTokenMeter.Cli.Selection.SessionSelectionInput.EndOfFile;
    }
}
