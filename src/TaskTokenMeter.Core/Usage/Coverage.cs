namespace TaskTokenMeter.Core.Usage;

public sealed record CoverageInput(
    long? Observed,
    long? Reference,
    bool HasIndependentReference,
    bool SamePeriod,
    bool SameModel,
    bool SameMetric,
    bool SameExecutionScope);

public sealed record CoverageResult(decimal? Ratio, string? Diagnostic)
{
    public bool IsAvailable => Ratio.HasValue;
}

public static class CoverageCalculator
{
    public static CoverageResult Calculate(CoverageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.Observed.HasValue)
        {
            return new CoverageResult(null, "observed_metric_unavailable");
        }

        if (!input.Reference.HasValue)
        {
            return new CoverageResult(null, "reference_metric_unavailable");
        }

        if (input.Observed.Value < 0 || input.Reference.Value < 0)
        {
            return new CoverageResult(null, "negative_metric_invalid");
        }

        if (input.Reference.Value == 0)
        {
            return new CoverageResult(null, "reference_denominator_zero");
        }

        if (!input.HasIndependentReference)
        {
            return new CoverageResult(null, "reference_not_independent");
        }

        if (!input.SamePeriod || !input.SameModel || !input.SameMetric || !input.SameExecutionScope)
        {
            return new CoverageResult(null, "coverage_scope_mismatch");
        }

        var ratio = (decimal)input.Observed.Value / input.Reference.Value;
        return input.Observed.Value > input.Reference.Value
            ? new CoverageResult(ratio, "coverage_exceeds_reference")
            : new CoverageResult(ratio, null);
    }
}
