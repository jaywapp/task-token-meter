using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Core.Usage;

public static class UsageNormalizer
{
    public static UsageNormalizationResult Normalize(IEnumerable<UsageObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var canonical = Canonicalize(observations);
        if (canonical.HasIdentityConflict)
        {
            return new UsageNormalizationResult(
                canonical.Observations,
                new TokenUsage(),
                null,
                1,
                null,
                null,
                MeasurementQuality.Invalid,
                ["stable_call_id_conflict"]);
        }

        if (canonical.Observations.Count == 0)
        {
            return new UsageNormalizationResult(
                canonical.Observations,
                new TokenUsage(),
                0,
                0,
                0,
                null,
                MeasurementQuality.Observed,
                []);
        }

        if (canonical.Observations.Select(observation => observation.Provider).Distinct().Skip(1).Any())
        {
            return new UsageNormalizationResult(
                canonical.Observations,
                new TokenUsage(),
                null,
                1,
                null,
                null,
                MeasurementQuality.Invalid,
                ["provider_mismatch"]);
        }

        var normalized = canonical.Observations.Select(NormalizeObservation).ToArray();
        var usages = normalized.Select(item => item.Usage).ToArray();
        var diagnostics = normalized
            .SelectMany(item => item.Diagnostics)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var hasInvalid = normalized.Any(item => item.IsInvalid);
        var hasUnknown = normalized.Any(item => !item.IsComplete);
        var hasPartial = normalized.Any(item => !item.IsComplete && !item.IsProvisional);
        var hasProvisional = normalized.Any(item => item.IsProvisional);

        var usage = new TokenUsage(
            InputTotal: SumIfComplete(usages.Select(item => item.InputTotal)),
            UncachedInput: SumIfComplete(usages.Select(item => item.UncachedInput)),
            CacheRead: SumIfComplete(usages.Select(item => item.CacheRead)),
            CacheWrite: SumIfComplete(usages.Select(item => item.CacheWrite)),
            CacheWrite5m: SumIfComplete(usages.Select(item => item.CacheWrite5m)),
            CacheWrite1h: SumIfComplete(usages.Select(item => item.CacheWrite1h)),
            Output: SumIfComplete(usages.Select(item => item.Output)),
            Reasoning: SumIfComplete(usages.Select(item => item.Reasoning)),
            ProcessedTokens: hasInvalid || hasUnknown ? null : SumIfComplete(usages.Select(item => item.ProcessedTokens)),
            NativeTotal: SumIfComplete(usages.Select(item => item.NativeTotal)));

        var knownSubtotal = SumKnown(normalized.Select(item => item.KnownSubtotal));
        var apiCallEvidence = canonical.Observations.All(IsStableApiCall);
        int? apiCallCount = apiCallEvidence ? canonical.Observations.Count : null;
        long? maxObservedInput = apiCallEvidence && normalized.All(item => item.InputForApiCall.HasValue)
            ? normalized.Max(item => item.InputForApiCall!.Value)
            : null;

        var quality = hasInvalid
            ? MeasurementQuality.Invalid
            : hasPartial
                ? MeasurementQuality.Partial
                : hasProvisional
                    ? MeasurementQuality.Provisional
                    : MeasurementQuality.Observed;

        return new UsageNormalizationResult(
            canonical.Observations,
            usage,
            knownSubtotal,
            normalized.Count(item => !item.IsComplete),
            apiCallCount,
            maxObservedInput,
            quality,
            diagnostics);
    }

    private static CanonicalObservations Canonicalize(IEnumerable<UsageObservation> observations)
    {
        var identified = new Dictionary<string, UsageObservation>(StringComparer.Ordinal);
        var unidentified = new List<UsageObservation>();
        var conflict = false;

        foreach (var observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentNullException.ThrowIfNull(observation.NativeUsage);

            if (string.IsNullOrWhiteSpace(observation.StableCallId))
            {
                unidentified.Add(observation);
                continue;
            }

            if (!identified.TryAdd(observation.StableCallId, observation) &&
                !EqualityComparer<UsageObservation>.Default.Equals(identified[observation.StableCallId], observation))
            {
                conflict = true;
            }
        }

        var result = identified
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .Concat(unidentified.OrderBy(ObservationSortKey, StringComparer.Ordinal))
            .ToArray();

        return new CanonicalObservations(result, conflict);
    }

    private static string ObservationSortKey(UsageObservation observation)
    {
        var native = observation.NativeUsage;
        return string.Join('|',
            observation.Provider,
            observation.Kind,
            native.InputTotal,
            native.CacheRead,
            native.CacheWrite,
            native.CacheWrite5m,
            native.CacheWrite1h,
            native.Output,
            native.Reasoning,
            native.NativeTotal);
    }

    private static bool IsStableApiCall(UsageObservation observation) =>
        observation.Kind == UsageObservationKind.ApiCall && !string.IsNullOrWhiteSpace(observation.StableCallId);

    private static NormalizedObservation NormalizeObservation(UsageObservation observation)
    {
        return HasNegativeValue(observation.NativeUsage)
            ? Invalid(observation.NativeUsage, "negative_token_value")
            : observation.Provider switch
            {
                ProviderKind.Claude => NormalizeClaude(observation.NativeUsage),
                ProviderKind.Codex => NormalizeCodex(observation.NativeUsage),
                _ => Invalid(observation.NativeUsage, "provider_unsupported")
            };
    }

    private static NormalizedObservation NormalizeClaude(NativeTokenUsage native)
    {
        var diagnostics = new List<string>();
        long? cacheWrite = native.CacheWrite;

        var anyTtl = native.CacheWrite5m.HasValue || native.CacheWrite1h.HasValue;
        if (anyTtl && (!native.CacheWrite5m.HasValue || !native.CacheWrite1h.HasValue))
        {
            cacheWrite = null;
            diagnostics.Add("cache_ttl_breakdown_incomplete");
        }
        else if (native.CacheWrite5m.HasValue && native.CacheWrite1h.HasValue)
        {
            var ttlTotal = checked(native.CacheWrite5m.Value + native.CacheWrite1h.Value);
            if (native.CacheWrite.HasValue && native.CacheWrite.Value != ttlTotal)
            {
                return new NormalizedObservation(
                    new TokenUsage(
                        UncachedInput: native.InputTotal,
                        CacheRead: native.CacheRead,
                        CacheWrite5m: native.CacheWrite5m,
                        CacheWrite1h: native.CacheWrite1h,
                        Output: native.Output,
                        NativeTotal: native.NativeTotal),
                    SumKnown([native.InputTotal, native.CacheRead, native.Output]),
                    null,
                    false,
                    true,
                    false,
                    ["cache_ttl_total_mismatch"]);
            }

            cacheWrite = ttlTotal;
        }

        var complete = native.InputTotal.HasValue && native.CacheRead.HasValue && cacheWrite.HasValue && native.Output.HasValue;
        long? inputTotal = complete
            ? checked(native.InputTotal!.Value + native.CacheRead!.Value + cacheWrite!.Value)
            : null;
        long? processed = complete ? checked(inputTotal!.Value + native.Output!.Value) : null;

        var usage = new TokenUsage(
            InputTotal: inputTotal,
            UncachedInput: native.InputTotal,
            CacheRead: native.CacheRead,
            CacheWrite: cacheWrite,
            CacheWrite5m: native.CacheWrite5m,
            CacheWrite1h: native.CacheWrite1h,
            Output: native.Output,
            Reasoning: null,
            ProcessedTokens: processed,
            NativeTotal: native.NativeTotal);

        return new NormalizedObservation(
            usage,
            processed ?? SumKnown([native.InputTotal, native.CacheRead, native.Output]),
            inputTotal,
            complete,
            false,
            false,
            diagnostics);
    }

    private static NormalizedObservation NormalizeCodex(NativeTokenUsage native)
    {
        if (native.InputTotal.HasValue && native.CacheRead.HasValue && native.CacheRead.Value > native.InputTotal.Value)
        {
            return Invalid(native, "cached_input_exceeds_input");
        }

        if (native.Output.HasValue && native.Reasoning.HasValue && native.Reasoning.Value > native.Output.Value)
        {
            return Invalid(native, "reasoning_output_exceeds_output");
        }

        if (native.InputTotal.HasValue && native.Output.HasValue && native.NativeTotal.HasValue &&
            checked(native.InputTotal.Value + native.Output.Value) != native.NativeTotal.Value)
        {
            return Invalid(native, "total_tokens_mismatch");
        }

        long? uncachedInput = native.InputTotal.HasValue && native.CacheRead.HasValue
            ? checked(native.InputTotal.Value - native.CacheRead.Value)
            : null;
        var complete = native.InputTotal.HasValue && native.CacheRead.HasValue && native.Output.HasValue;
        long? processed = complete ? checked(native.InputTotal!.Value + native.Output!.Value) : null;
        var totalOnly = !native.InputTotal.HasValue && !native.Output.HasValue && native.NativeTotal.HasValue;
        var provisional = native.CacheWrite.GetValueOrDefault() > 0 || totalOnly;
        var diagnostics = provisional ? new[] { "cache_write_semantics_unknown" } : Array.Empty<string>();

        if (totalOnly)
        {
            diagnostics = ["total_only_semantics_unknown"];
        }

        var usage = new TokenUsage(
            InputTotal: native.InputTotal,
            UncachedInput: uncachedInput,
            CacheRead: native.CacheRead,
            CacheWrite: native.CacheWrite,
            CacheWrite5m: native.CacheWrite5m,
            CacheWrite1h: native.CacheWrite1h,
            Output: native.Output,
            Reasoning: native.Reasoning,
            ProcessedTokens: processed,
            NativeTotal: native.NativeTotal);

        return new NormalizedObservation(
            usage,
            processed ?? SumKnown([native.InputTotal, native.Output]),
            native.InputTotal,
            complete,
            false,
            provisional,
            diagnostics);
    }

    private static NormalizedObservation Invalid(NativeTokenUsage native, string diagnostic) =>
        new(
            new TokenUsage(NativeTotal: native.NativeTotal),
            null,
            null,
            false,
            true,
            false,
            [diagnostic]);

    private static bool HasNegativeValue(NativeTokenUsage native) =>
        new[]
        {
            native.InputTotal,
            native.CacheRead,
            native.CacheWrite,
            native.CacheWrite5m,
            native.CacheWrite1h,
            native.Output,
            native.Reasoning,
            native.NativeTotal
        }.Any(value => value < 0);

    private static long? SumIfComplete(IEnumerable<long?> values)
    {
        long sum = 0;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                return null;
            }

            sum = checked(sum + value.Value);
        }

        return sum;
    }

    private static long? SumKnown(IEnumerable<long?> values)
    {
        long sum = 0;
        var hasValue = false;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            sum = checked(sum + value.Value);
            hasValue = true;
        }

        return hasValue ? sum : null;
    }

    private sealed record CanonicalObservations(IReadOnlyList<UsageObservation> Observations, bool HasIdentityConflict);

    private sealed record NormalizedObservation(
        TokenUsage Usage,
        long? KnownSubtotal,
        long? InputForApiCall,
        bool IsComplete,
        bool IsInvalid,
        bool IsProvisional,
        IReadOnlyList<string> Diagnostics);
}
