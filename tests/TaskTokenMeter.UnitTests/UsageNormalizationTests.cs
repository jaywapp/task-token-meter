using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Usage;
using Xunit;

namespace TaskTokenMeter.UnitTests;

public sealed class UsageNormalizationTests
{
    [Fact]
    public void ClaudeTtlBreakdownUsesTotalOnceAndLeavesReasoningUnknown()
    {
        var native = new NativeTokenUsage(
            InputTotal: 10,
            CacheRead: 20,
            CacheWrite: 30,
            CacheWrite5m: 12,
            CacheWrite1h: 18,
            Output: 7,
            Reasoning: 3);

        var result = UsageNormalizer.Normalize(
        [
            new UsageObservation(ProviderKind.Claude, native, "claude-call", UsageObservationKind.ApiCall)
        ]);

        Assert.Same(native, result.NativeObservations[0].NativeUsage);
        Assert.Equal(10, result.Usage.UncachedInput);
        Assert.Equal(30, result.Usage.CacheWrite);
        Assert.Equal(60, result.Usage.InputTotal);
        Assert.Equal(67, result.Usage.ProcessedTokens);
        Assert.Null(result.Usage.Reasoning);
        Assert.Equal(67, result.KnownSubtotal);
        Assert.Equal(1, result.ApiCallCount);
        Assert.Equal(60, result.MaxObservedInput);
        Assert.Equal(MeasurementQuality.Observed, result.Quality);
    }

    [Fact]
    public void ClaudeTtlMismatchKeepsOnlySafeKnownSubtotal()
    {
        var result = UsageNormalizer.Normalize(
        [
            new UsageObservation(
                ProviderKind.Claude,
                new NativeTokenUsage(InputTotal: 4, CacheRead: 6, CacheWrite: 31, CacheWrite5m: 10, CacheWrite1h: 20, Output: 7),
                "claude-call",
                UsageObservationKind.ApiCall)
        ]);

        Assert.Null(result.Usage.InputTotal);
        Assert.Null(result.Usage.ProcessedTokens);
        Assert.Equal(17, result.KnownSubtotal);
        Assert.Equal(1, result.UnknownObservationCount);
        Assert.Equal(MeasurementQuality.Invalid, result.Quality);
        Assert.Contains("cache_ttl_total_mismatch", result.Diagnostics);
    }

    [Fact]
    public void CodexCachedAndReasoningAreSubsets()
    {
        var result = UsageNormalizer.Normalize(
        [
            new UsageObservation(
                ProviderKind.Codex,
                new NativeTokenUsage(InputTotal: 1000, CacheRead: 600, CacheWrite: 0, Output: 200, Reasoning: 50, NativeTotal: 1200),
                "codex-call",
                UsageObservationKind.ApiCall)
        ]);

        Assert.Equal(400, result.Usage.UncachedInput);
        Assert.Equal(1000, result.Usage.InputTotal);
        Assert.Equal(200, result.Usage.Output);
        Assert.Equal(50, result.Usage.Reasoning);
        Assert.Equal(1200, result.Usage.ProcessedTokens);
        Assert.Equal(1200, result.Usage.NativeTotal);
        Assert.Equal(1200, result.KnownSubtotal);
        Assert.Equal(1000, result.MaxObservedInput);
    }

    [Fact]
    public void DuplicateAndPermutationDoNotChangeNormalizedAggregate()
    {
        var first = new UsageObservation(
            ProviderKind.Codex,
            new NativeTokenUsage(InputTotal: 100, CacheRead: 20, CacheWrite: 0, Output: 25, Reasoning: 5, NativeTotal: 125),
            "a",
            UsageObservationKind.ApiCall);
        var second = new UsageObservation(
            ProviderKind.Codex,
            new NativeTokenUsage(InputTotal: 40, CacheRead: 10, CacheWrite: 0, Output: 10, Reasoning: 2, NativeTotal: 50),
            "b",
            UsageObservationKind.ApiCall);

        var forward = UsageNormalizer.Normalize([first, second, first]);
        var reverse = UsageNormalizer.Normalize([second, first]);

        Assert.Equal(forward.Usage, reverse.Usage);
        Assert.Equal(forward.KnownSubtotal, reverse.KnownSubtotal);
        Assert.Equal(forward.UnknownObservationCount, reverse.UnknownObservationCount);
        Assert.Equal(forward.ApiCallCount, reverse.ApiCallCount);
        Assert.Equal(forward.MaxObservedInput, reverse.MaxObservedInput);
        Assert.Equal(forward.Quality, reverse.Quality);
        Assert.Equal(forward.Diagnostics, reverse.Diagnostics);
        Assert.Equal(2, forward.NativeObservations.Count);
    }

    [Fact]
    public void StableCallIdConflictIsInvalidInsteadOfChoosingAnArbitrarySnapshot()
    {
        var result = UsageNormalizer.Normalize(
        [
            new UsageObservation(ProviderKind.Codex, new NativeTokenUsage(InputTotal: 10, CacheRead: 0, Output: 5, NativeTotal: 15), "same", UsageObservationKind.ApiCall),
            new UsageObservation(ProviderKind.Codex, new NativeTokenUsage(InputTotal: 11, CacheRead: 0, Output: 5, NativeTotal: 16), "same", UsageObservationKind.ApiCall)
        ]);

        Assert.Equal(MeasurementQuality.Invalid, result.Quality);
        Assert.Null(result.Usage.ProcessedTokens);
        Assert.Null(result.ApiCallCount);
        Assert.Contains("stable_call_id_conflict", result.Diagnostics);
    }

    [Fact]
    public void TotalOnlyCodexUsagePreservesNativeTotalWithoutInventingZeroes()
    {
        var result = UsageNormalizer.Normalize(
        [
            new UsageObservation(ProviderKind.Codex, new NativeTokenUsage(NativeTotal: 77), "total-only", UsageObservationKind.ApiCall)
        ]);

        Assert.Null(result.Usage.InputTotal);
        Assert.Null(result.Usage.Output);
        Assert.Null(result.Usage.ProcessedTokens);
        Assert.Equal(77, result.Usage.NativeTotal);
        Assert.Null(result.KnownSubtotal);
        Assert.Equal(1, result.UnknownObservationCount);
        Assert.Equal(MeasurementQuality.Provisional, result.Quality);
        Assert.Contains("total_only_semantics_unknown", result.Diagnostics);
    }
    [Fact]
    public void NullAndNegativeValuesRemainUnknownOrInvalidInsteadOfBecomingZero()
    {
        var partial = UsageNormalizer.Normalize(
        [
            new UsageObservation(
                ProviderKind.Claude,
                new NativeTokenUsage(CacheRead: 0, CacheWrite: 0, Output: 2),
                "partial",
                UsageObservationKind.ApiCall)
        ]);
        var observedZero = UsageNormalizer.Normalize(
        [
            new UsageObservation(ProviderKind.Codex, new NativeTokenUsage(InputTotal: 0, CacheRead: 0, Output: 0, NativeTotal: 0), "zero", UsageObservationKind.ApiCall)
        ]);
        var invalid = UsageNormalizer.Normalize(
        [
            new UsageObservation(
                ProviderKind.Codex,
                new NativeTokenUsage(InputTotal: -1, CacheRead: 0, Output: 0, NativeTotal: 0),
                "invalid",
                UsageObservationKind.ApiCall)
        ]);

        Assert.Null(partial.Usage.InputTotal);
        Assert.Null(partial.Usage.ProcessedTokens);
        Assert.Equal(2, partial.KnownSubtotal);
        Assert.Equal(MeasurementQuality.Partial, partial.Quality);
        Assert.Equal(0, observedZero.Usage.ProcessedTokens);
        Assert.Equal(0, observedZero.KnownSubtotal);
        Assert.Equal(MeasurementQuality.Observed, observedZero.Quality);
        Assert.Equal(-1, invalid.NativeObservations[0].NativeUsage.InputTotal);
        Assert.Null(invalid.Usage.ProcessedTokens);
        Assert.Equal(MeasurementQuality.Invalid, invalid.Quality);
        Assert.Contains("negative_token_value", invalid.Diagnostics);
    }

    [Fact]
    public void CheckedArithmeticRejectsOverflow()
    {
        var observation = new UsageObservation(
            ProviderKind.Claude,
            new NativeTokenUsage(InputTotal: long.MaxValue, CacheRead: 1, CacheWrite: 0, Output: 0),
            "overflow",
            UsageObservationKind.ApiCall);

        Assert.Throws<OverflowException>(() => UsageNormalizer.Normalize([observation]));
    }

    [Fact]
    public void ApiCallMetricsRequireStablePerCallObservations()
    {
        var result = UsageNormalizer.Normalize(
        [
            new UsageObservation(ProviderKind.Codex, new NativeTokenUsage(InputTotal: 15, CacheRead: 5, Output: 5, NativeTotal: 20), "one", UsageObservationKind.ApiCall),
            new UsageObservation(ProviderKind.Codex, new NativeTokenUsage(InputTotal: 40, CacheRead: 10, Output: 5, NativeTotal: 45), null, UsageObservationKind.ApiCall)
        ]);

        Assert.Null(result.ApiCallCount);
        Assert.Null(result.MaxObservedInput);
        Assert.Equal(65, result.Usage.ProcessedTokens);
    }

    public static IEnumerable<object?[]> UnavailableCoverageCases =>
    [
        [null, 10L, "observed_metric_unavailable"],
        [10L, null, "reference_metric_unavailable"],
        [0L, 0L, "reference_denominator_zero"]
    ];

    [Theory]
    [MemberData(nameof(UnavailableCoverageCases))]
    public void CoverageIsNotAvailableWithoutANonZeroDenominator(long? observed, long? reference, string diagnostic)
    {
        var result = CoverageCalculator.Calculate(new CoverageInput(observed, reference, true, true, true, true, true));

        Assert.False(result.IsAvailable);
        Assert.Null(result.Ratio);
        Assert.Equal(diagnostic, result.Diagnostic);
    }

    [Fact]
    public void CoverageRequiresComparableIndependentScopeAndDoesNotClampOverage()
    {
        var unavailable = CoverageCalculator.Calculate(new CoverageInput(10, 10, false, true, true, true, true));
        var overage = CoverageCalculator.Calculate(new CoverageInput(12, 10, true, true, true, true, true));

        Assert.False(unavailable.IsAvailable);
        Assert.Equal("reference_not_independent", unavailable.Diagnostic);
        var scopeMismatch = CoverageCalculator.Calculate(new CoverageInput(10, 10, true, false, true, true, true));
        Assert.False(scopeMismatch.IsAvailable);
        Assert.Equal("coverage_scope_mismatch", scopeMismatch.Diagnostic);
        Assert.True(overage.IsAvailable);
        Assert.Equal(1.2m, overage.Ratio);
        Assert.Equal("coverage_exceeds_reference", overage.Diagnostic);
    }
}
