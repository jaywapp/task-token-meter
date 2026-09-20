using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Core.Usage;
using Xunit;

namespace TaskTokenMeter.UnitTests;

public sealed class AttributionTests
{
    [Fact]
    public void SessionLineageUsesExplicitRootAcrossNestedChildren()
    {
        var result = SessionLineageResolver.Resolve(
            ProviderKind.Codex,
            "child-level-2",
            [
                new SessionLineage(ProviderKind.Codex, "root", "root"),
                new SessionLineage(ProviderKind.Codex, "child-level-1", "root", "root"),
                new SessionLineage(ProviderKind.Codex, "child-level-2", "root", "child-level-1")
            ]);

        Assert.True(result.IsResolved);
        Assert.Equal("root", result.RootSessionId);
        Assert.Equal("explicit-root-session-id", result.Evidence);
    }

    [Fact]
    public void SessionLineageDoesNotGuessAcrossAMissingParent()
    {
        var result = SessionLineageResolver.Resolve(
            ProviderKind.Claude,
            "child",
            [new SessionLineage(ProviderKind.Claude, "child", ParentSessionId: "unknown-parent")]);

        Assert.False(result.IsResolved);
        Assert.Null(result.RootSessionId);
        Assert.Contains("parent_session_lineage_missing", result.Diagnostics);
    }

    [Fact]
    public void MainOnlyProjectionCountsAUniqueExecutionOnceAcrossRootAndChildDetails()
    {
        var root = new RootTurnKey(ProviderKind.Codex, "root-session", "root-turn");
        var rootExecution = Observation("root-origin", "root-execution", root, 120, true);
        var child = Observation("child-origin", "child-execution", root, 40);
        var duplicateChildDetail = Observation(
            "child-origin",
            "child-execution",
            root,
            40,
            observedSessionId: "root-session",
            observedExecutionId: "child-detail-copy");

        var result = TurnProjector.Project([rootExecution, child, duplicateChildDetail]);

        var turn = Assert.Single(result.Turns);
        Assert.Equal(160, turn.Usage.ProcessedTokens);
        Assert.Equal(2, turn.Membership.Count);
        Assert.Single(result.Excluded);
        Assert.All(turn.Membership, static membership => Assert.True(membership.IsIncludedInAggregate));
    }

    [Fact]
    public void MissingOrAmbiguousParentRemainsUnattributed()
    {
        var missingParent = Observation(
            "child-origin",
            "child-execution",
            null,
            40,
            attributionIsAmbiguous: true);

        var result = TurnProjector.Project([missingParent]);

        Assert.Empty(result.Turns);
        var unattributed = Assert.Single(result.Unattributed);
        Assert.Equal(AttributionStatus.Unattributed, unattributed.Status);
        Assert.Equal("parent_identity_ambiguous", unattributed.Reason);
        Assert.Equal(40, unattributed.Observation.Usage.KnownSubtotal);
    }

    [Fact]
    public void ChildInclusiveProjectionKeepsMembershipWithoutAddingChildAgain()
    {
        var root = new RootTurnKey(ProviderKind.Codex, "root-session", "root-turn");
        var observations = new[]
        {
            Observation("root-origin", "root-execution", root, 185, true, RootUsageScope.ChildInclusive),
            Observation("child-origin", "child-execution", root, 40, rootScope: RootUsageScope.ChildInclusive)
        };

        var turn = Assert.Single(TurnProjector.Project(observations).Turns);

        Assert.Equal(185, turn.Usage.ProcessedTokens);
        var childMembership = Assert.Single(turn.Membership, static membership => membership.Execution.ExecutionId == "child-execution");
        Assert.Equal(AttributionStatus.AttributedAlreadyInRoot, childMembership.Status);
        Assert.False(childMembership.IsIncludedInAggregate);
    }

    [Fact]
    public void UnknownRootScopePreservesKnownRootWithoutInventingACompleteTotal()
    {
        var root = new RootTurnKey(ProviderKind.Codex, "root-session", "root-turn");
        var observations = new[]
        {
            Observation("root-origin", "root-execution", root, 120, true, RootUsageScope.Unknown),
            Observation("child-origin", "child-execution", root, 40, rootScope: RootUsageScope.Unknown)
        };

        var turn = Assert.Single(TurnProjector.Project(observations).Turns);

        Assert.Null(turn.Usage.ProcessedTokens);
        Assert.Null(turn.Usage.InputTotal);
        Assert.Equal(120, turn.KnownSubtotal);
        Assert.Equal(1, turn.UnknownObservationCount);
        Assert.Equal(MeasurementQuality.Partial, turn.MeasurementQuality);
        Assert.Contains("root_scope_unknown", turn.Diagnostics);
    }

    [Fact]
    public void ExecutionStateAndMeasurementQualityRemainIndependent()
    {
        var root = new RootTurnKey(ProviderKind.Claude, "root-session", "root-turn");
        var pending = new SourceCompleteness(
            "child-source",
            SourceAvailability.Pending,
            false,
            ["child_source_pending"]);
        var observation = Observation(
            "root-session",
            "root-execution",
            root,
            20,
            true,
            provider: ProviderKind.Claude,
            executionState: ExecutionState.Completed,
            source: pending);

        var turn = Assert.Single(TurnProjector.Project([observation]).Turns);

        Assert.Equal(ExecutionState.Completed, turn.ExecutionState);
        Assert.Equal(MeasurementQuality.Provisional, turn.MeasurementQuality);
        Assert.Equal(1, turn.UnknownObservationCount);
        Assert.Contains("child_source_pending", turn.Diagnostics);
    }

    [Fact]
    public void LateChildReplacesTheSameCompletedRootWithANewRevision()
    {
        var root = new RootTurnKey(ProviderKind.Claude, "root-session", "root-turn");
        var pendingRoot = Observation(
            "root-session",
            "root-execution",
            root,
            20,
            true,
            provider: ProviderKind.Claude,
            executionState: ExecutionState.Completed,
            source: new SourceCompleteness("child-source", SourceAvailability.Pending, false, ["child_source_pending"]));
        var first = TurnProjector.ProjectRoot(root, [pendingRoot]);

        var completeRoot = Observation(
            "root-session",
            "root-execution",
            root,
            20,
            true,
            provider: ProviderKind.Claude,
            executionState: ExecutionState.Completed,
            source: SourceCompleteness.Complete("main-source"));
        var child = Observation(
            "child-session",
            "child-execution",
            root,
            10,
            provider: ProviderKind.Claude,
            executionState: ExecutionState.Completed,
            source: SourceCompleteness.Complete("child-source"));
        var second = TurnProjector.ProjectRoot(root, [completeRoot, child], first);
        var repeated = TurnProjector.ProjectRoot(root, [completeRoot, child], second);

        Assert.Equal(root, first.TurnKey);
        Assert.Equal(root, second.TurnKey);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.Equal(2, repeated.Revision);
        Assert.Equal(20, first.Usage.ProcessedTokens);
        Assert.Equal(30, second.Usage.ProcessedTokens);
        Assert.Equal(ExecutionState.Completed, second.ExecutionState);
        Assert.Equal(MeasurementQuality.Observed, second.MeasurementQuality);
    }

    [Fact]
    public void OriginIdentityExcludesAnEvidencedForkReplayButKeepsNewForkWork()
    {
        var parentRoot = new RootTurnKey(ProviderKind.Codex, "parent-session", "parent-turn");
        var forkRoot = new RootTurnKey(ProviderKind.Codex, "fork-session", "fork-turn");
        var parent = Observation("origin-session", "origin-execution", parentRoot, 30, true);
        var replay = Observation(
            "origin-session",
            "origin-execution",
            forkRoot,
            30,
            observedSessionId: "fork-session",
            observedExecutionId: "copied-execution",
            isReplay: true);
        var newForkWork = Observation("fork-session", "new-execution", forkRoot, 10, true);

        var result = TurnProjector.Project([parent, replay, newForkWork]);

        Assert.Equal(2, result.Turns.Count);
        Assert.Equal(30, Assert.Single(result.Turns, turn => turn.TurnKey == parentRoot).Usage.ProcessedTokens);
        Assert.Equal(10, Assert.Single(result.Turns, turn => turn.TurnKey == forkRoot).Usage.ProcessedTokens);
        Assert.Contains(result.Excluded, static item => item.Reason == "origin-replay-excluded");
    }

    [Fact]
    public void ReplayWithoutAnOriginObservationIsExcluded()
    {
        var root = new RootTurnKey(ProviderKind.Codex, "fork-session", "fork-turn");
        var replay = Observation(
            "origin-session",
            "origin-execution",
            root,
            30,
            true,
            isReplay: true);

        var result = TurnProjector.Project([replay]);

        Assert.Empty(result.Turns);
        Assert.Empty(result.Unattributed);
        var excluded = Assert.Single(result.Excluded);
        Assert.Equal("origin-replay-excluded", excluded.Reason);
    }

    [Fact]
    public void SameRevisionWithConflictingRootScopesIsInvalid()
    {
        var root = new RootTurnKey(ProviderKind.Codex, "root-session", "root-turn");
        var mainOnly = Observation(
            "origin-session",
            "origin-execution",
            root,
            30,
            true,
            RootUsageScope.MainOnly);
        var childInclusive = Observation(
            "origin-session",
            "origin-execution",
            root,
            30,
            true,
            RootUsageScope.ChildInclusive);

        var result = TurnProjector.Project([mainOnly, childInclusive]);

        Assert.Empty(result.Turns);
        Assert.Equal(2, result.Unattributed.Count);
        Assert.All(result.Unattributed, static item =>
        {
            Assert.Equal(AttributionStatus.Invalid, item.Status);
            Assert.Equal("execution_revision_conflict", item.Reason);
        });
        Assert.Contains("execution_revision_conflict", result.Diagnostics);
    }
    private static ExecutionObservation Observation(
        string originSessionId,
        string executionId,
        RootTurnKey? root,
        long processedTokens,
        bool isRoot = false,
        RootUsageScope rootScope = RootUsageScope.MainOnly,
        ProviderKind provider = ProviderKind.Codex,
        ExecutionState executionState = ExecutionState.Unknown,
        SourceCompleteness? source = null,
        string? observedSessionId = null,
        string? observedExecutionId = null,
        bool isReplay = false,
        bool attributionIsAmbiguous = false)
    {
        var native = provider == ProviderKind.Claude
            ? new NativeTokenUsage(
                InputTotal: processedTokens,
                CacheRead: 0,
                CacheWrite: 0,
                CacheWrite5m: 0,
                CacheWrite1h: 0,
                Output: 0)
            : new NativeTokenUsage(
                InputTotal: processedTokens,
                CacheRead: 0,
                CacheWrite: 0,
                Output: 0,
                Reasoning: 0,
                NativeTotal: processedTokens);
        var usage = UsageNormalizer.Normalize(
        [
            new UsageObservation(provider, native, executionId, UsageObservationKind.ApiCall)
        ]);

        return new ExecutionObservation(
            new ExecutionIdentity(provider, originSessionId, executionId),
            observedSessionId ?? originSessionId,
            observedExecutionId ?? executionId,
            root,
            isRoot,
            rootScope,
            usage,
            executionState,
            source ?? SourceCompleteness.Complete($"source-{originSessionId}-{executionId}"),
            isRoot ? "root-record" : "explicit-parent-lineage",
            observedAt: new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero),
            isReplay: isReplay,
            attributionIsAmbiguous: attributionIsAmbiguous);
    }
}
