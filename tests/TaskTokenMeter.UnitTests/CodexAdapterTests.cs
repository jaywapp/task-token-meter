using TaskTokenMeter.Adapters.Codex;
using TaskTokenMeter.Core.Contracts;
using Xunit;

namespace TaskTokenMeter.UnitTests;

public sealed class CodexAdapterTests
{
    [Fact]
    public void CapabilityReportsVerifiedVersionAndUsageKinds()
    {
        var capability = new CodexUsageAdapter().Capability;

        Assert.Equal("0.153.4", capability.ProviderVersion);
        Assert.True(capability.IsVerified);
        Assert.True(capability.TurnSnapshotAuthority);
        Assert.Equal(CodexRootScope.MainOnly, capability.RootScope);
        Assert.Equal(
            [CodexUsageKind.CallDelta, CodexUsageKind.TurnSnapshot, CodexUsageKind.SessionSnapshot],
            capability.UsageKinds);
    }

    [Fact]
    public void ReadDetailedUsesFinalTurnSnapshotWithoutAddingDeltasOrSessionSnapshot()
    {
        var adapter = new CodexUsageAdapter();

        var result = adapter.ReadDetailed(Fixture("codex", "two-turn-snapshots.jsonl"));

        Assert.True(result.IsSupported);
        Assert.Equal(9, result.Observations.Count);
        var first = Assert.Single(result.Turns, turn => turn.RootTurnId == "synthetic-codex-turn-001");
        Assert.Equal(250, first.Usage.ProcessedTokens);
        Assert.Equal(180, first.Usage.InputTotal);
        Assert.Equal(130, first.Usage.UncachedInput);
        Assert.Equal(2, first.ApiCallCount);
        Assert.Equal(100, first.MaxObservedInput);
        Assert.Equal(MeasurementQuality.Observed, first.Quality);

        var second = Assert.Single(result.Turns, turn => turn.RootTurnId == "synthetic-codex-turn-002");
        Assert.Equal(50, second.Usage.ProcessedTokens);
        Assert.Equal(300, result.Turns.Sum(static turn => turn.Usage.ProcessedTokens));
    }

    [Fact]
    public void ReadDetailedRecognizesTheRealRootLevelTokenUsageRecordShape()
    {
        // Codex CLI 0.153.4 rollouts write token_usage_record as the root `type` directly, with
        // session/turn/usage fields as siblings under `payload` — not nested inside an event_msg
        // envelope the way every other fixture in this file models it. Verified against this
        // machine's real rollouts, where the event_msg-wrapped shape matched zero of 776 files.
        var adapter = new CodexUsageAdapter();

        var result = adapter.ReadDetailed(Fixture("codex", "real-root-shape.jsonl"));

        Assert.True(result.IsSupported);
        var turn = Assert.Single(result.Turns);
        Assert.Equal("synthetic-codex-turn-real-root", turn.RootTurnId);
        Assert.Equal(250, turn.Usage.ProcessedTokens);
        Assert.Equal(180, turn.Usage.InputTotal);
        Assert.Equal(130, turn.Usage.UncachedInput);
        Assert.Equal(2, turn.ApiCallCount);
        Assert.Equal(100, turn.MaxObservedInput);
        Assert.Equal(MeasurementQuality.Observed, turn.Quality);
    }

    [Fact]
    public void ReadDetailedPreservesSnapshotAuthorityWhenDeltaValidationFails()
    {
        var result = new CodexUsageAdapter().ReadDetailed(
            Fixture("codex", "delta-snapshot-mismatch.jsonl"));

        var turn = Assert.Single(result.Turns);
        Assert.Equal(120, turn.Usage.ProcessedTokens);
        Assert.Equal(MeasurementQuality.Partial, turn.Quality);
        Assert.Equal(1, turn.UnknownObservationCount);
        Assert.Contains("delta_snapshot_mismatch", turn.Diagnostics);
    }

    [Fact]
    public void ReadDetailedMainOnlyScopeAddsEachNestedExecutionOnce()
    {
        var result = new CodexUsageAdapter().ReadDetailed(Fixture("codex", "nested-child.jsonl"));

        var turn = Assert.Single(result.Turns);
        Assert.Equal("synthetic-codex-root-session-nested", turn.RootSessionId);
        Assert.Equal(80, turn.Usage.ProcessedTokens);
        Assert.Equal(3, turn.ApiCallCount);
        Assert.Equal(40, turn.MaxObservedInput);
        Assert.Equal(3, turn.Membership.Count);
        Assert.Contains(
            result.Observations,
            observation => observation.Lineage.ParentThreadId == "synthetic-codex-child-thread-level-1");
    }

    [Fact]
    public void ReadDetailedChildInclusiveScopeDoesNotAddChildAgain()
    {
        var capability = CodexAdapterCapability.Verified(
            "synthetic-future-version",
            "codex-rollout-child-inclusive-synthetic-v1",
            CodexRootScope.ChildInclusive);
        var adapter = new CodexUsageAdapter(capability);

        var result = adapter.ReadDetailed(
            Fixture("codex", "root-child-inclusive", "main.jsonl"),
            Fixture("codex", "root-child-inclusive", "child.jsonl"));

        var turn = Assert.Single(result.Turns);
        Assert.Equal(185, turn.Usage.ProcessedTokens);
        Assert.Null(turn.ApiCallCount);
        Assert.Contains(
            turn.Membership,
            membership => membership.AttributionStatus == CodexAttributionStatus.AttributedAlreadyInRoot);
    }

    [Fact]
    public void ReadDetailedUnknownScopeReturnsPartialWithoutInventingAggregate()
    {
        var capability = CodexAdapterCapability.Verified(
            "synthetic-unknown-version",
            "codex-rollout-root-scope-unknown-synthetic-v1",
            CodexRootScope.Unknown);
        var adapter = new CodexUsageAdapter(capability);

        var result = adapter.ReadDetailed(
            Fixture("codex", "root-scope-unknown", "main.jsonl"),
            Fixture("codex", "root-scope-unknown", "child.jsonl"));

        var turn = Assert.Single(result.Turns);
        Assert.Equal(MeasurementQuality.Partial, turn.Quality);
        Assert.Null(turn.NativeUsage);
        Assert.Null(turn.Usage.ProcessedTokens);
        Assert.Equal(120, turn.KnownSubtotal);
        Assert.Equal(40, Assert.Single(turn.ChildObservedUsage).TotalTokens);
        Assert.Contains("root_scope_unknown", turn.Diagnostics);
    }

    [Fact]
    public void ReadDetailedPreservesTotalOnlyAndNonzeroCacheWrite()
    {
        var result = new CodexUsageAdapter().ReadDetailed(
            Fixture("codex", "usage-edge-cases.jsonl"));

        var totalOnly = Assert.Single(
            result.Turns,
            turn => turn.RootTurnId == "synthetic-codex-turn-total-only");
        Assert.Equal(77, totalOnly.Usage.NativeTotal);
        Assert.Null(totalOnly.Usage.InputTotal);
        Assert.Null(totalOnly.Usage.ProcessedTokens);
        Assert.Null(totalOnly.KnownSubtotal);
        Assert.Equal(1, totalOnly.UnknownObservationCount);
        Assert.Equal(MeasurementQuality.Provisional, totalOnly.Quality);
        Assert.Contains("total_only_semantics_unknown", totalOnly.Diagnostics);

        var cacheWrite = Assert.Single(
            result.Turns,
            turn => turn.RootTurnId == "synthetic-codex-turn-cache-write");
        Assert.Equal(7, cacheWrite.Usage.CacheWrite);
        Assert.Equal(130, cacheWrite.Usage.ProcessedTokens);
        Assert.Equal(MeasurementQuality.Provisional, cacheWrite.Quality);
        Assert.Contains("cache_write_semantics_unknown", cacheWrite.Diagnostics);
    }

    [Fact]
    public void ReadDetailedDistinguishesUnsupportedIdentityAndInvalidMetrics()
    {
        var result = new CodexUsageAdapter().ReadDetailed(
            Fixture("codex", "usage-edge-cases.jsonl"));

        var missingRoot = Assert.Single(
            result.UnattributedObservations,
            observation => observation.Lineage.TurnId == "synthetic-codex-turn-missing-root");
        Assert.Equal(MeasurementQuality.Unsupported, missingRoot.Quality);
        Assert.Contains("missing_root_turn_id", missingRoot.Diagnostics);

        var cachedInvalid = Assert.Single(
            result.Turns,
            turn => turn.RootTurnId == "synthetic-codex-turn-cached-invalid");
        Assert.Equal(MeasurementQuality.Invalid, cachedInvalid.Quality);
        Assert.Null(cachedInvalid.Usage.ProcessedTokens);
        Assert.Contains("cached_input_exceeds_input", cachedInvalid.Diagnostics);

        var reasoningInvalid = Assert.Single(
            result.Turns,
            turn => turn.RootTurnId == "synthetic-codex-turn-reasoning-invalid");
        Assert.Contains("reasoning_output_exceeds_output", reasoningInvalid.Diagnostics);

        var totalInvalid = Assert.Single(
            result.Turns,
            turn => turn.RootTurnId == "synthetic-codex-turn-total-invalid");
        Assert.Contains("total_tokens_mismatch", totalInvalid.Diagnostics);
    }

    [Fact]
    public void ReadDetailedUsesOriginIdentityForForkReplayAndIsolatesAmbiguousReplay()
    {
        var result = new CodexUsageAdapter().ReadDetailed(
            Fixture("codex", "fork-replay", "parent.jsonl"),
            Fixture("codex", "fork-replay", "fork-with-origin.jsonl"),
            Fixture("codex", "fork-replay", "fork-without-origin.jsonl"));

        Assert.Equal(2, result.Turns.Count);
        Assert.Contains(result.Turns, turn => turn.Usage.ProcessedTokens == 30);
        Assert.Contains(result.Turns, turn => turn.Usage.ProcessedTokens == 10);
        Assert.Single(result.ExcludedExecutions);
        Assert.Contains(
            result.UnattributedObservations,
            observation => observation.Diagnostics.Contains("fork_replay_ambiguous", StringComparer.Ordinal));
    }

    [Fact]
    public void ReadRejectsUnknownVersionAndLegacySessionOnlyShape()
    {
        var source = Fixture("codex", "two-turn-snapshots.jsonl");
        var unknownVersion = new CodexUsageAdapter("0.152.0");

        Assert.False(unknownVersion.CanRead(source));
        Assert.Throws<CodexUnsupportedFormatException>(() => unknownVersion.Read(source));

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"codex-session-only-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(
                temporaryPath,
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\",\"session_id\":\"session\",\"thread_id\":\"thread\",\"thread_token_usage\":{\"total_tokens\":10}}}");
            var adapter = new CodexUsageAdapter();

            Assert.False(adapter.CanRead(temporaryPath));
            var detailed = adapter.ReadDetailed(temporaryPath);
            Assert.False(detailed.IsSupported);
            Assert.Contains("session_only_usage_unsupported", detailed.Diagnostics);
            Assert.Throws<CodexUnsupportedFormatException>(() => adapter.Read(temporaryPath));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string Fixture(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var fixtureRoot = Path.Combine(directory.FullName, "tests", "fixtures");
            if (Directory.Exists(fixtureRoot))
            {
                return Path.Combine([fixtureRoot, .. relativeSegments]);
            }
        }

        throw new DirectoryNotFoundException("Unable to locate tests/fixtures from the test output directory.");
    }
}
