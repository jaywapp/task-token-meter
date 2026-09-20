using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;

namespace TaskTokenMeter.Core.Attribution;

public static class AttributionResolver
{
    public static AttributionResult Resolve(IEnumerable<ExecutionObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var input = observations.ToArray();
        if (input.Any(static observation => observation is null))
        {
            throw new ArgumentException("Execution observations cannot contain null values.", nameof(observations));
        }

        var canonical = new List<ExecutionObservation>();
        var unattributed = new List<UnattributedObservation>();
        var excluded = new List<ExcludedExecution>();

        foreach (var identityGroup in input
                     .GroupBy(static observation => observation.Identity)
                     .OrderBy(static group => group.Key.Provider)
                     .ThenBy(static group => group.Key.OriginSessionId, StringComparer.Ordinal)
                     .ThenBy(static group => group.Key.ExecutionId, StringComparer.Ordinal))
        {
            ResolveIdentityGroup(identityGroup.ToArray(), canonical, unattributed, excluded);
        }

        var roots = canonical
            .Where(static observation => observation.RootTurn is not null)
            .GroupBy(static observation => observation.RootTurn!)
            .Select(BuildRootGroup)
            .OrderBy(static group => group.RootTurn.Provider)
            .ThenBy(static group => group.RootTurn.RootSessionId, StringComparer.Ordinal)
            .ThenBy(static group => group.RootTurn.RootTurnId, StringComparer.Ordinal)
            .ToArray();

        var diagnostics = roots
            .SelectMany(static root => root.Diagnostics)
            .Concat(unattributed.Select(static item => item.Reason))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new AttributionResult(
            roots,
            unattributed
                .OrderBy(static item => item.Observation.Identity.OriginSessionId, StringComparer.Ordinal)
                .ThenBy(static item => item.Observation.Identity.ExecutionId, StringComparer.Ordinal)
                .ToArray(),
            excluded
                .OrderBy(static item => item.Identity.OriginSessionId, StringComparer.Ordinal)
                .ThenBy(static item => item.Identity.ExecutionId, StringComparer.Ordinal)
                .ToArray(),
            diagnostics);
    }

    private static void ResolveIdentityGroup(
        IReadOnlyList<ExecutionObservation> observations,
        List<ExecutionObservation> canonical,
        ICollection<UnattributedObservation> unattributed,
        List<ExcludedExecution> excluded)
    {
        if (observations.Any(static observation => observation.AttributionIsAmbiguous))
        {
            AddUnattributed(observations, unattributed, "parent_identity_ambiguous", MeasurementQuality.Partial);
            return;
        }

        var nonReplayRoots = observations
            .Where(static observation => !observation.IsReplay)
            .Select(static observation => observation.RootTurn)
            .Distinct()
            .ToArray();

        if (nonReplayRoots.Length > 1 || nonReplayRoots.Length == 1 && nonReplayRoots[0] is null)
        {
            AddUnattributed(observations, unattributed, "origin_identity_root_conflict", MeasurementQuality.Invalid);
            return;
        }

        if (nonReplayRoots.Length == 0)
        {
            foreach (var replay in observations)
            {
                excluded.Add(new ExcludedExecution(
                    replay.Identity,
                    replay.ObservedSessionId,
                    replay.ObservedExecutionId,
                    "origin-replay-excluded"));
            }

            return;
        }

        var selectedRoot = nonReplayRoots[0];

        var selected = observations
            .Where(observation => Equals(observation.RootTurn, selectedRoot))
            .ToArray();
        var latestRevision = selected.Max(static observation => observation.Revision);
        var latest = selected.Where(observation => observation.Revision == latestRevision).ToArray();
        var preferred = latest
            .OrderBy(static observation => observation.IsReplay)
            .ThenByDescending(static observation => observation.IsRootExecution)
            .ThenByDescending(static observation => observation.ObservedAt)
            .First();

        if (latest.Any(observation => !EquivalentMeasurement(preferred, observation)))
        {
            AddUnattributed(observations, unattributed, "execution_revision_conflict", MeasurementQuality.Invalid);
            return;
        }

        canonical.Add(preferred);
        foreach (var duplicate in observations.Where(observation => !ReferenceEquals(observation, preferred)))
        {
            excluded.Add(new ExcludedExecution(
                duplicate.Identity,
                duplicate.ObservedSessionId,
                duplicate.ObservedExecutionId,
                duplicate.IsReplay && !Equals(duplicate.RootTurn, selectedRoot)
                    ? "origin-replay-excluded"
                    : "duplicate-execution-identity"));
        }
    }

    private static AttributedRootGroup BuildRootGroup(IGrouping<RootTurnKey, ExecutionObservation> group)
    {
        var observations = group
            .OrderByDescending(static observation => observation.IsRootExecution)
            .ThenBy(static observation => observation.Identity.OriginSessionId, StringComparer.Ordinal)
            .ThenBy(static observation => observation.Identity.ExecutionId, StringComparer.Ordinal)
            .ToArray();
        var rootScopes = observations
            .Where(static observation => observation.IsRootExecution)
            .Select(static observation => observation.RootScope)
            .Distinct()
            .ToArray();
        if (rootScopes.Length == 0)
        {
            rootScopes = observations.Select(static observation => observation.RootScope).Distinct().ToArray();
        }

        var scope = rootScopes.Length == 1 ? rootScopes[0] : RootUsageScope.Unknown;
        var diagnostics = new List<string>();
        if (rootScopes.Length > 1)
        {
            diagnostics.Add("root_scope_conflict");
        }

        if (!observations.Any(static observation => observation.IsRootExecution))
        {
            diagnostics.Add("root_execution_missing");
        }

        var executions = observations.Select(observation =>
        {
            var status = GetStatus(observation, scope);
            var included = status == AttributionStatus.Attributed &&
                           (scope != RootUsageScope.ChildInclusive || observation.IsRootExecution);
            var evidence = status switch
            {
                AttributionStatus.AttributedAlreadyInRoot => "child-inclusive-capability",
                AttributionStatus.Provisional when scope == RootUsageScope.Unknown => "unknown-root-scope",
                _ => observation.Evidence
            };
            var membership = new Membership(group.Key, observation.Identity, status, evidence, included);
            return new AttributedExecution(observation, membership);
        }).ToArray();

        return new AttributedRootGroup(group.Key, scope, executions, diagnostics);
    }

    private static AttributionStatus GetStatus(
        ExecutionObservation observation,
        RootUsageScope rootScope)
    {
        if (observation.Usage.Quality == MeasurementQuality.Invalid)
        {
            return AttributionStatus.Invalid;
        }

        if (observation.IsRootExecution)
        {
            return AttributionStatus.Attributed;
        }

        return rootScope switch
        {
            RootUsageScope.MainOnly => AttributionStatus.Attributed,
            RootUsageScope.ChildInclusive => AttributionStatus.AttributedAlreadyInRoot,
            RootUsageScope.Unknown => AttributionStatus.Provisional,
            _ => throw new ArgumentOutOfRangeException(nameof(rootScope), rootScope, null)
        };
    }

    private static bool EquivalentMeasurement(ExecutionObservation left, ExecutionObservation right) =>
        Equals(left.RootTurn, right.RootTurn) &&
        left.Usage.Usage == right.Usage.Usage &&
        left.Usage.KnownSubtotal == right.Usage.KnownSubtotal &&
        left.Usage.UnknownObservationCount == right.Usage.UnknownObservationCount &&
        left.Usage.ApiCallCount == right.Usage.ApiCallCount &&
        left.Usage.MaxObservedInput == right.Usage.MaxObservedInput &&
        left.Usage.Quality == right.Usage.Quality &&
        left.RootScope == right.RootScope &&
        left.ExecutionState == right.ExecutionState;

    private static void AddUnattributed(
        IEnumerable<ExecutionObservation> observations,
        ICollection<UnattributedObservation> unattributed,
        string reason,
        MeasurementQuality quality)
    {
        foreach (var observation in observations)
        {
            unattributed.Add(new UnattributedObservation(
                observation,
                quality == MeasurementQuality.Invalid ? AttributionStatus.Invalid : AttributionStatus.Unattributed,
                reason,
                quality));
        }
    }
}
