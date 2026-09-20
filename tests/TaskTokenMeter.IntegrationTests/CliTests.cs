using System.Globalization;
using System.Text.Json;
using TaskTokenMeter.Cli;
using TaskTokenMeter.Cli.Selection;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Storage.Migration;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class CliTests
{
    private static readonly object EnvironmentLock = new();
    [Fact]
    public async Task JsonRequiresExplicitSelectorWithoutReadingInput()
    {
        var console = new TestConsole();
        var result = await CliApplication.RunAsync(["last", "--json"], new TestRuntime(), console);
        var document = JsonDocument.Parse(console.StandardOutput);
        Assert.Equal(4, result);
        Assert.Equal(0, console.ReadCount);
        Assert.Equal("selector_required", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(console.StandardError);
    }

    [Fact]
    public async Task TextAndJsonRenderTheSameProcessedTokens()
    {
        var candidate = new SessionCandidate(ProviderKind.Codex, "session", null, null);
        var projection = Projection(MeasurementQuality.Observed, ExecutionState.Completed, 1200);
        var runtime = new TestRuntime(candidate, projection);
        var text = new TestConsole();
        var json = new TestConsole();
        Assert.Equal(0, await CliApplication.RunAsync(["current", "--provider", "codex", "--session", "session"], runtime, text));
        Assert.Equal(0, await CliApplication.RunAsync(["current", "--json", "--provider", "codex", "--session", "session"], runtime, json));
        using var document = JsonDocument.Parse(json.StandardOutput);
        Assert.Contains("1,200", text.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1200, document.RootElement.GetProperty("turns")[0].GetProperty("processedTokens").GetInt64());
    }

    [Fact]
    public async Task StrictFailsForPartialMeasurementAfterRendering()
    {
        var candidate = new SessionCandidate(ProviderKind.Codex, "session", null, null);
        var console = new TestConsole();
        var result = await CliApplication.RunAsync(["current", "--strict", "--provider", "codex", "--session", "session"], new TestRuntime(candidate, Projection(MeasurementQuality.Partial, ExecutionState.Running, 12)), console);
        Assert.Equal(6, result);
        Assert.Contains("partial", console.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveSelectionUsesCandidateAndDoesNotExposeLabelsInJson()
    {
        var candidate = new SessionCandidate(ProviderKind.Claude, "selected", null, null, "private prompt text");
        var console = new TestConsole(new SessionSelectionInput(SessionSelectionInputKind.Value, "1"));
        var result = await CliApplication.RunAsync(["turns"], new TestRuntime(candidate, Projection(MeasurementQuality.Observed, ExecutionState.Completed, 7)), console);
        Assert.Equal(0, result);
        Assert.Contains("Turn", console.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LastPrefersTerminalAndCurrentIncludesRunning()
    {
        var candidate = new SessionCandidate(ProviderKind.Codex, "session", null, null);
        var completed = Projection(MeasurementQuality.Observed, ExecutionState.Completed, 10) with
        {
            ObservedAt = DateTimeOffset.Parse("2026-09-20T00:00:00Z", CultureInfo.InvariantCulture)
        };
        var failed = Projection(MeasurementQuality.Observed, ExecutionState.Failed, 15) with
        {
            ObservedAt = DateTimeOffset.Parse("2026-09-20T00:30:00Z", CultureInfo.InvariantCulture)
        };
        var running = Projection(MeasurementQuality.Observed, ExecutionState.Running, 20) with
        {
            ObservedAt = DateTimeOffset.Parse("2026-09-20T01:00:00Z", CultureInfo.InvariantCulture)
        };
        var runtime = new TestRuntime(candidate, completed, failed, running);
        var current = new TestConsole();
        var last = new TestConsole();

        Assert.Equal(0, await CliApplication.RunAsync(["current", "--provider", "codex", "--session", "session"], runtime, current));
        Assert.Equal(0, await CliApplication.RunAsync(["last", "--provider", "codex", "--session", "session"], runtime, last));
        Assert.Contains("20", current.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("15", last.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackStatusIsRenderedInTextAndJson()
    {
        var candidate = new SessionCandidate(ProviderKind.Codex, "session", null, null);
        var fallback = Projection(MeasurementQuality.Provisional, ExecutionState.Unknown, 4) with
        {
            Diagnostics = ["stored_fallback"]
        };
        var text = new TestConsole();
        var json = new TestConsole();
        var runtime = new TestRuntime(candidate, fallback);

        Assert.Equal(0, await CliApplication.RunAsync(["turns", "--provider", "codex", "--session", "session"], runtime, text));
        Assert.Equal(0, await CliApplication.RunAsync(["turns", "--json", "--provider", "codex", "--session", "session"], runtime, json));
        Assert.Equal(1, text.StandardOutput.Split("stored_fallback", StringSplitOptions.None).Length - 1);
        using var document = JsonDocument.Parse(json.StandardOutput);
        Assert.Equal("missing", document.RootElement.GetProperty("turns")[0].GetProperty("sourceAvailability").GetString());
    }

    [Fact]
    public async Task HumanTextLinesAreBoundedToEightyColumns()
    {
        var candidate = new SessionCandidate(ProviderKind.Codex, "session", null, null);
        var console = new TestConsole();
        Assert.Equal(0, await CliApplication.RunAsync(["current", "--provider", "codex", "--session", "session"], new TestRuntime(candidate, Projection(MeasurementQuality.Observed, ExecutionState.Completed, 1200)), console));
        Assert.All(console.StandardOutput.Split(["\r\n", "\n"], StringSplitOptions.None).Where(static line => line.Length > 0), static line => Assert.True(line.Length <= 80, line));
    }

    [Fact]
    public async Task RedirectedSelectorsReturnCodeFourWithoutReadingOrAnsi()
    {
        var console = new TestConsole { IsInputRedirected = true, IsOutputRedirected = true };
        Assert.Equal(4, await CliApplication.RunAsync(["current"], new TestRuntime(), console));
        Assert.Equal(0, console.ReadCount);
        Assert.DoesNotContain("\u001b", console.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemConsoleDisablesColorWhenNoColorIsSet()
    {
        lock (EnvironmentLock)
        {
            var previous = Environment.GetEnvironmentVariable("NO_COLOR");
            try { Environment.SetEnvironmentVariable("NO_COLOR", "1"); Assert.False(new SystemCliConsole().IsColorEnabled); }
            finally { Environment.SetEnvironmentVariable("NO_COLOR", previous); }
        }
    }
    [Fact]
    public async Task InvalidArgumentsUseArchitectureExitCode()
    {
        var console = new TestConsole();
        Assert.Equal(2, await CliApplication.RunAsync(["current", "--provider", "unknown"], new TestRuntime(), console));
        Assert.Contains("command_failed", console.StandardError, StringComparison.Ordinal);
    }
    private static TurnProjection Projection(MeasurementQuality quality, ExecutionState state, long processed) => new(
        new RootTurnKey(ProviderKind.Codex, "session", "turn"), 1,
        new TokenUsage(InputTotal: processed, UncachedInput: processed, CacheRead: 0, CacheWrite: 0, Output: 0, Reasoning: 0, ProcessedTokens: processed, NativeTotal: processed),
        processed, 0, 1, processed, state, quality, RootUsageScope.MainOnly, [], [SourceCompleteness.Complete("fixture")], [], DateTimeOffset.Parse("2026-09-20T00:00:00Z", CultureInfo.InvariantCulture));

    private sealed class TestRuntime(params object[] values) : ICliRuntime
    {
        private readonly SessionCandidate[] candidates = values.OfType<SessionCandidate>().ToArray();
        private readonly TurnProjection[] turns = values.OfType<TurnProjection>().ToArray();
        public IReadOnlyList<SessionCandidate> Discover(string? provider, string? sessionId) => candidates;
        public Task<IReadOnlyList<TurnProjection>> ReadTurnsAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TurnProjection>>(turns);
        public Task<IReadOnlyList<TurnProjection>> SyncAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TurnProjection>>(turns);
        public Task<IReadOnlyList<TurnProjection>> RebuildAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TurnProjection>>(turns);
        public Task<StorageStatus> GetStorageStatusAsync(string workspace, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageMigrationResult> MigrateStorageAsync(string workspace, StorageMode destination, bool dryRun, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestConsole(params SessionSelectionInput[] inputs) : ICliConsole
    {
        private readonly Queue<SessionSelectionInput> inputs = new(inputs);
        public bool IsInputRedirected { get; init; }
        public bool IsOutputRedirected { get; init; }
        public bool IsColorEnabled => false;
        public bool IsContinuousIntegration => false;
        public int ReadCount { get; private set; }
        public string StandardOutput { get; private set; } = string.Empty;
        public string StandardError { get; private set; } = string.Empty;
        public void Write(string value) => StandardOutput += value;
        public void WriteError(string value) => StandardError += value;
        public SessionSelectionInput ReadInput() { ReadCount++; return inputs.Count == 0 ? SessionSelectionInput.EndOfFile : inputs.Dequeue(); }
    }
}