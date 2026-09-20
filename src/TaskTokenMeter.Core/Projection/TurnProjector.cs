using System.Globalization;
using System.Text;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Identity;
using TaskTokenMeter.Core.Usage;

namespace TaskTokenMeter.Core.Projection;

public static class TurnProjector
{
    public static ProjectionBatch Project(
        IEnumerable<ExecutionObservation> observations,
        IEnumerable<TurnProjection>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var attribution = AttributionResolver.Resolve(observations);
        var previousByKey = (previous ?? [])
            .GroupBy(static projection => projection.TurnKey)
            .ToDictionary(static group => group.Key, static group => group.Single());
        var turns = attribution.Roots
            .Select(root => ProjectRoot(root, previousByKey.GetValueOrDefault(root.RootTurn)))
            .ToArray();

        return new ProjectionBatch(
            turns,
            attribution.Unattributed,
            attribution.Excluded,
            attribution.Diagnostics);
    }

    public static TurnProjection ProjectRoot(
        RootTurnKey rootTurn,
        IEnumerable<ExecutionObservation> observations,
        TurnProjection? previous = null)
    {
        ArgumentNullException.ThrowIfNull(rootTurn);
        ArgumentNullException.ThrowIfNull(observations);

        if (previous is not null && previous.TurnKey != rootTurn)
        {
            throw new ArgumentException("The previous projection belongs to a different root Turn.", nameof(previous));
        }

        var attribution = AttributionResolver.Resolve(observations);
        var root = attribution.Roots.SingleOrDefault(group => group.RootTurn == rootTurn);
        if (root is null)
        {
            throw new InvalidOperationException("No attributed execution exists for the requested root Turn.");
        }

        return ProjectRoot(root, previous);
    }

    private static TurnProjection ProjectRoot(
        AttributedRootGroup root,
        TurnProjection? previous)
    {
        var included = root.Executions
            .Where(static execution => execution.Membership.IsIncludedInAggregate)
            .Select(static execution => execution.Observation.Usage)
            .ToArray();
        var rootExecution = root.Executions
            .Where(static execution => execution.Observation.IsRootExecution)
            .OrderByDescending(static execution => execution.Observation.Revision)
            .FirstOrDefault();
        var sources = root.Executions
            .Select(static execution => execution.Observation.SourceCompleteness)
            .GroupBy(static source => source.SourceId, StringComparer.Ordinal)
            .Select(static group => MostSevereSource(group.ToArray()))
            .OrderBy(static source => source.SourceId, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = root.Executions
            .SelectMany(static execution => execution.Observation.Usage.Diagnostics)
            .Concat(sources.SelectMany(static source => source.Diagnostics))
            .Concat(root.Diagnostics)
            .ToHashSet(StringComparer.Ordinal);

        var usage = AggregateUsage(included);
        var knownSubtotal = SumKnown(included.Select(static result => result.KnownSubtotal));
        var unknownCount = included.Sum(static result => result.UnknownObservationCount);
        var apiCallCount = SumIfComplete(included.Select(static result => result.ApiCallCount));
        var maxObservedInput = MaxIfComplete(included.Select(static result => result.MaxObservedInput));
        var quality = CombineQuality(root.Executions.Select(static execution => execution.Observation.Usage.Quality));

        foreach (var source in sources)
        {
            quality = MoreSevere(quality, QualityFor(source));
            if (!source.IsComplete)
            {
                unknownCount = checked(unknownCount + 1);
                diagnostics.Add(DiagnosticFor(source));
            }
        }

        if (root.RootScope == RootUsageScope.Unknown)
        {
            var knownRoot = rootExecution?.Observation.Usage.KnownSubtotal;
            usage = new TokenUsage();
            knownSubtotal = knownRoot;
            apiCallCount = null;
            maxObservedInput = null;
            unknownCount = Math.Max(1, unknownCount);
            quality = MoreSevere(quality, MeasurementQuality.Partial);
            diagnostics.Add("root_scope_unknown");
        }

        if (rootExecution is null)
        {
            quality = MoreSevere(quality, MeasurementQuality.Partial);
            diagnostics.Add("root_execution_missing");
        }

        var candidate = new TurnProjection(
            root.RootTurn,
            0,
            usage,
            knownSubtotal,
            unknownCount,
            apiCallCount,
            maxObservedInput,
            ResolveExecutionState(root.Executions, rootExecution, diagnostics),
            quality,
            root.RootScope,
            root.Executions.Select(static execution => execution.Membership).ToArray(),
            sources,
            diagnostics.Order(StringComparer.Ordinal).ToArray(),
            MaxObservedAt(root.Executions));

        if (previous is not null && Equivalent(previous, candidate))
        {
            return previous;
        }

        return candidate with { Revision = previous is null ? 1 : checked(previous.Revision + 1) };
    }

    private static TokenUsage AggregateUsage(UsageNormalizationResult[] results)
    {
        if (results.Length == 0)
        {
            return new TokenUsage();
        }

        return new TokenUsage(
            InputTotal: SumIfComplete(results.Select(static result => result.Usage.InputTotal)),
            UncachedInput: SumIfComplete(results.Select(static result => result.Usage.UncachedInput)),
            CacheRead: SumIfComplete(results.Select(static result => result.Usage.CacheRead)),
            CacheWrite: SumIfComplete(results.Select(static result => result.Usage.CacheWrite)),
            CacheWrite5m: SumIfComplete(results.Select(static result => result.Usage.CacheWrite5m)),
            CacheWrite1h: SumIfComplete(results.Select(static result => result.Usage.CacheWrite1h)),
            Output: SumIfComplete(results.Select(static result => result.Usage.Output)),
            Reasoning: SumIfComplete(results.Select(static result => result.Usage.Reasoning)),
            ProcessedTokens: SumIfComplete(results.Select(static result => result.Usage.ProcessedTokens)),
            NativeTotal: SumIfComplete(results.Select(static result => result.Usage.NativeTotal)));
    }

    private static ExecutionState ResolveExecutionState(
        IReadOnlyList<AttributedExecution> executions,
        AttributedExecution? rootExecution,
        HashSet<string> diagnostics)
    {
        if (rootExecution is not null)
        {
            return rootExecution.Observation.ExecutionState;
        }

        var states = executions
            .Select(static execution => execution.Observation.ExecutionState)
            .Distinct()
            .ToArray();
        if (states.Length == 1)
        {
            return states[0];
        }

        if (states.Length > 1)
        {
            diagnostics.Add("execution_state_ambiguous");
        }

        return ExecutionState.Unknown;
    }

    private static SourceCompleteness MostSevereSource(SourceCompleteness[] sources) =>
        sources
            .OrderByDescending(static source => SourceSeverity(source.Availability))
            .ThenBy(static source => source.IsComplete)
            .First();

    private static MeasurementQuality QualityFor(SourceCompleteness source)
    {
        if (source.IsComplete && source.Availability == SourceAvailability.Available)
        {
            return MeasurementQuality.Observed;
        }

        return source.Availability switch
        {
            SourceAvailability.Pending => MeasurementQuality.Provisional,
            SourceAvailability.Unsupported => MeasurementQuality.Unsupported,
            SourceAvailability.Missing or SourceAvailability.Truncated or SourceAvailability.Unreadable => MeasurementQuality.Partial,
            SourceAvailability.Available => MeasurementQuality.Partial,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Availability, null)
        };
    }

    private static string DiagnosticFor(SourceCompleteness source) =>
        source.Availability switch
        {
            SourceAvailability.Available => "source_incomplete",
            SourceAvailability.Pending => "source_pending",
            SourceAvailability.Missing => "source_missing",
            SourceAvailability.Truncated => "source_truncated",
            SourceAvailability.Unreadable => "source_unreadable",
            SourceAvailability.Unsupported => "source_unsupported",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Availability, null)
        };

    private static int SourceSeverity(SourceAvailability availability) => availability switch
    {
        SourceAvailability.Available => 0,
        SourceAvailability.Pending => 1,
        SourceAvailability.Missing => 2,
        SourceAvailability.Truncated => 2,
        SourceAvailability.Unreadable => 2,
        SourceAvailability.Unsupported => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(availability), availability, null)
    };

    private static MeasurementQuality CombineQuality(IEnumerable<MeasurementQuality> qualities) =>
        qualities.Aggregate(MeasurementQuality.Observed, MoreSevere);

    private static MeasurementQuality MoreSevere(MeasurementQuality left, MeasurementQuality right) =>
        QualitySeverity(right) > QualitySeverity(left) ? right : left;

    private static int QualitySeverity(MeasurementQuality quality) => quality switch
    {
        MeasurementQuality.Observed => 0,
        MeasurementQuality.Provisional => 1,
        MeasurementQuality.Partial => 2,
        MeasurementQuality.Unsupported => 3,
        MeasurementQuality.Invalid => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null)
    };

    private static long? SumIfComplete(IEnumerable<long?> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                return null;
            }

            total = checked(total + value.Value);
        }

        return total;
    }

    private static int? SumIfComplete(IEnumerable<int?> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                return null;
            }

            total = checked(total + value.Value);
        }

        return total;
    }

    private static long? SumKnown(IEnumerable<long?> values)
    {
        long total = 0;
        var hasValue = false;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            total = checked(total + value.Value);
            hasValue = true;
        }

        return hasValue ? total : null;
    }

    private static long? MaxIfComplete(IEnumerable<long?> values)
    {
        long? maximum = null;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                return null;
            }

            maximum = maximum.HasValue ? Math.Max(maximum.Value, value.Value) : value.Value;
        }

        return maximum;
    }

    private static DateTimeOffset? MaxObservedAt(IEnumerable<AttributedExecution> executions)
    {
        DateTimeOffset? maximum = null;
        foreach (var observedAt in executions.Select(static execution => execution.Observation.ObservedAt))
        {
            if (observedAt.HasValue && (!maximum.HasValue || observedAt.Value > maximum.Value))
            {
                maximum = observedAt;
            }
        }

        return maximum;
    }

    private static bool Equivalent(TurnProjection left, TurnProjection right) =>
        string.Equals(Fingerprint(left), Fingerprint(right), StringComparison.Ordinal);

    private static string Fingerprint(TurnProjection projection)
    {
        var builder = new StringBuilder();
        Append(builder, projection.TurnKey.Provider);
        Append(builder, projection.TurnKey.RootSessionId);
        Append(builder, projection.TurnKey.RootTurnId);
        Append(builder, projection.Usage.InputTotal);
        Append(builder, projection.Usage.UncachedInput);
        Append(builder, projection.Usage.CacheRead);
        Append(builder, projection.Usage.CacheWrite);
        Append(builder, projection.Usage.CacheWrite5m);
        Append(builder, projection.Usage.CacheWrite1h);
        Append(builder, projection.Usage.Output);
        Append(builder, projection.Usage.Reasoning);
        Append(builder, projection.Usage.ProcessedTokens);
        Append(builder, projection.Usage.NativeTotal);
        Append(builder, projection.KnownSubtotal);
        Append(builder, projection.UnknownObservationCount);
        Append(builder, projection.ApiCallCount);
        Append(builder, projection.MaxObservedInput);
        Append(builder, projection.ExecutionState);
        Append(builder, projection.MeasurementQuality);
        Append(builder, projection.RootScope);
        Append(builder, projection.ObservedAt);

        foreach (var membership in projection.Membership
                     .OrderBy(static item => item.Execution.OriginSessionId, StringComparer.Ordinal)
                     .ThenBy(static item => item.Execution.ExecutionId, StringComparer.Ordinal))
        {
            Append(builder, membership.Execution.Provider);
            Append(builder, membership.Execution.OriginSessionId);
            Append(builder, membership.Execution.ExecutionId);
            Append(builder, membership.Status);
            Append(builder, membership.Evidence);
            Append(builder, membership.IsIncludedInAggregate);
        }

        foreach (var source in projection.Sources.OrderBy(static item => item.SourceId, StringComparer.Ordinal))
        {
            Append(builder, source.SourceId);
            Append(builder, source.Availability);
            Append(builder, source.IsComplete);
            foreach (var diagnostic in source.Diagnostics.Order(StringComparer.Ordinal))
            {
                Append(builder, diagnostic);
            }
        }

        foreach (var diagnostic in projection.Diagnostics.Order(StringComparer.Ordinal))
        {
            Append(builder, diagnostic);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, object? value)
    {
        var text = value switch
        {
            null => "<null>",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        builder.Append(text?.Length ?? 0).Append(':').Append(text).Append('|');
    }
}
