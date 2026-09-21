using System.Text;
using TaskTokenMeter.Adapters.Codex;
using TaskTokenMeter.Core.Contracts;
using Xunit;

namespace TaskTokenMeter.UnitTests;

/// <summary>
/// Covers how a rollout file is turned into lines and records, independently of what the projection
/// then does with them. The adapter reads the raw UTF-8 bytes of a rollout, so the line framing, the
/// byte order mark, oversized lines and JSON property order are all part of its contract now.
/// </summary>
public sealed class CodexRolloutReadingTests
{
    private const string Usage =
        "{\"input_tokens\":10,\"cached_input_tokens\":0,\"output_tokens\":5," +
        "\"reasoning_output_tokens\":0,\"total_tokens\":15,\"cache_write_input_tokens\":0}";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EveryLineBreakStreamReaderRecognizesStillSeparatesRecords(string lineBreak)
    {
        using var rollout = new TemporaryRollout(
            UsageRecord("turn-a", "response-a") + lineBreak +
            UsageRecord("turn-b", "response-b") + lineBreak);

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.True(result.IsSupported);
        Assert.Equal(2, result.Turns.Count);
        Assert.DoesNotContain("malformed_json_line", result.Diagnostics);
    }

    [Fact]
    public void AByteOrderMarkDoesNotHideTheFirstRecord()
    {
        var content = new List<byte>([0xEF, 0xBB, 0xBF]);
        content.AddRange(Encoding.UTF8.GetBytes(UsageRecord("turn-a", "response-a") + "\n"));
        using var rollout = new TemporaryRollout([.. content]);

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.True(result.IsSupported);
        Assert.Equal(15, Assert.Single(result.Turns).Usage.ProcessedTokens);
    }

    [Fact]
    public void BlankLinesAndAMissingFinalNewlineAreTolerated()
    {
        using var rollout = new TemporaryRollout(
            "\n   \n" + UsageRecord("turn-a", "response-a") + "\n\n\t\n" + UsageRecord("turn-b", "response-b"));

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.Equal(2, result.Turns.Count);
        Assert.DoesNotContain("malformed_json_line", result.Diagnostics);
    }

    [Fact]
    public void ARecordFollowingALineLargerThanTheReadBufferIsStillProjected()
    {
        var padding = new string('x', 3 * 1024 * 1024);
        using var rollout = new TemporaryRollout(
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"synthetic_noop\",\"padding\":\"" + padding + "\"}}\n" +
            UsageRecord("turn-a", "response-a") + "\n");

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.True(result.IsSupported);
        Assert.Equal(15, Assert.Single(result.Turns).Usage.ProcessedTokens);
    }

    [Fact]
    public void RecordsAreReadWhicheverOrderTheirPropertiesAppearIn()
    {
        var reordered =
            "{\"payload\":{\"thread_token_usage\":" + Usage + ",\"turn_token_usage\":" + Usage +
            ",\"usage\":" + Usage + ",\"response_id\":\"response-a\",\"root_turn_id\":\"turn-a\"" +
            ",\"turn_id\":\"turn-a\",\"thread_id\":\"thread-a\",\"session_id\":\"session-a\"" +
            ",\"type\":\"token_usage_record\"},\"type\":\"event_msg\"" +
            ",\"timestamp\":\"2026-02-01T00:00:00Z\"}";
        using var rollout = new TemporaryRollout(reordered + "\n");

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        var turn = Assert.Single(result.Turns);
        Assert.Equal("session-a", turn.RootSessionId);
        Assert.Equal("turn-a", turn.RootTurnId);
        Assert.Equal(15, turn.Usage.ProcessedTokens);
        Assert.Equal(1, turn.ApiCallCount);
        Assert.Equal("2026-02-01T00:00:00Z", turn.ObservedAt);
        Assert.Equal(MeasurementQuality.Observed, turn.Quality);
    }

    [Fact]
    public void SessionMetadataIsAppliedToRecordsThatPrecedeIt()
    {
        using var rollout = new TemporaryRollout(
            UsageRecord("turn-a", "response-a") + "\n" +
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-a\",\"session_id\":\"session-a\"" +
            ",\"parent_thread_id\":\"thread-parent\"}}\n");

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.Single(result.Turns);
        Assert.All(result.Observations, observation => Assert.Equal("thread-parent", observation.Lineage.ParentThreadId));
    }

    [Fact]
    public void SyntaxErrorsBeyondTheFieldsThatMatterAreStillReported()
    {
        using var rollout = new TemporaryRollout(
            UsageRecord("turn-a", "response-a") + "\n" +
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"synthetic_noop\"},}\n" +
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\"}} trailing\n");

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.True(result.IsSupported);
        Assert.Single(result.Turns);
        Assert.Contains("malformed_json_line", result.Diagnostics);
    }

    [Fact]
    public void AMalformedNonObjectLineIsReportedInsteadOfFailingTheRead()
    {
        using var rollout = new TemporaryRollout("[1,\n" + UsageRecord("turn-a", "response-a") + "\n");

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.Single(result.Turns);
        Assert.Contains("malformed_json_line", result.Diagnostics);
    }

    [Fact]
    public void AWellFormedNonObjectLineStillFailsTheReadAsItAlwaysHas()
    {
        // A non-object root used to reach JsonElement.TryGetProperty, which rejects it outright, and
        // the CLI turns that into its generic failure exit code. Reading the bytes directly must not
        // quietly start tolerating a rollout the previous reader refused.
        using var rollout = new TemporaryRollout("[1,2]\n" + UsageRecord("turn-a", "response-a") + "\n");

        Assert.Throws<InvalidOperationException>(() => new CodexUsageAdapter().ReadDetailed(rollout.Path));
    }

    [Fact]
    public void AMetricThatIsNotAnIntegerRejectsTheWholeRecord()
    {
        using var rollout = new TemporaryRollout(
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\",\"session_id\":\"session-a\"" +
            ",\"thread_id\":\"thread-a\",\"turn_id\":\"turn-a\",\"root_turn_id\":\"turn-a\"" +
            ",\"response_id\":\"response-a\",\"usage\":{\"input_tokens\":1.5,\"total_tokens\":2}" +
            ",\"turn_token_usage\":" + Usage + ",\"thread_token_usage\":" + Usage + "}}\n");

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.False(result.IsSupported);
        Assert.Contains("session_only_usage_unsupported", result.Diagnostics);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public void TheFirstOccurrenceOfARepeatedPropertyWins()
    {
        using var rollout = new TemporaryRollout(
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\",\"session_id\":\"session-a\"" +
            ",\"session_id\":\"session-b\",\"thread_id\":\"thread-a\",\"turn_id\":\"turn-a\"" +
            ",\"root_turn_id\":\"turn-a\",\"response_id\":\"response-a\",\"usage\":" + Usage +
            ",\"turn_token_usage\":" + Usage + ",\"thread_token_usage\":" + Usage + "}}\n");

        var result = new CodexUsageAdapter().ReadDetailed(rollout.Path);

        Assert.Equal("session-a", Assert.Single(result.Turns).RootSessionId);
    }

    private static string UsageRecord(string turnId, string responseId) =>
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\",\"session_id\":\"session-a\"" +
        ",\"thread_id\":\"thread-a\",\"turn_id\":\"" + turnId + "\",\"root_turn_id\":\"" + turnId +
        "\",\"response_id\":\"" + responseId + "\",\"usage\":" + Usage +
        ",\"turn_token_usage\":" + Usage + ",\"thread_token_usage\":" + Usage + "}}";

    private sealed class TemporaryRollout : IDisposable
    {
        public TemporaryRollout(string content)
            : this(new UTF8Encoding(false).GetBytes(content))
        {
        }

        public TemporaryRollout(byte[] content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"codex-rollout-{Guid.NewGuid():N}.jsonl");
            File.WriteAllBytes(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
