using System.Text.Json;
using TaskTokenMeter.Adapters.Claude;
using TaskTokenMeter.Core.Contracts;
using Xunit;

namespace TaskTokenMeter.UnitTests;

public sealed class ClaudeAdapterTests
{
    private readonly ClaudeAdapter adapter = new();

    [Fact]
    public void StreamingSnapshotsAndRequestMessageAliasesProduceOneCallPerTurn()
    {
        var result = adapter.ReadSnapshot(Fixture("claude", "two-turn-streaming-alias.jsonl"));

        Assert.Equal(ClaudeReadStatus.Supported, result.Status);
        Assert.True(adapter.CanRead(Fixture("claude", "two-turn-streaming-alias.jsonl")));
        Assert.Equal(2, adapter.Read(Fixture("claude", "two-turn-streaming-alias.jsonl")).Count);
        Assert.Collection(
            result.Turns,
            first =>
            {
                Assert.Equal("synthetic-claude-prompt-001", first.RootTurnId);
                Assert.Equal(338, first.Usage.Output);
                Assert.Equal(373, first.Usage.ProcessedTokens);
                Assert.Equal(35, first.Usage.InputTotal);
                Assert.Equal(1, first.ApiCallCount);
                Assert.Equal(35, first.MaxObservedInput);
                Assert.Null(first.Usage.Reasoning);
                Assert.Equal(7, first.NativeUsage!.ThinkingTokens);
            },
            second =>
            {
                Assert.Equal("synthetic-claude-prompt-002", second.RootTurnId);
                Assert.Equal(25, second.Usage.Output);
                Assert.Equal(65, second.Usage.ProcessedTokens);
                Assert.Equal(1, second.ApiCallCount);
                Assert.Contains("request-message-alias", second.Membership.Select(item => item.Evidence));
            });
    }

    [Fact]
    public void CrossFileReplayIsInvariantToInputFileOrder()
    {
        var sourceA = Fixture("claude", "cross-file-replay", "source-a.jsonl");
        var sourceB = Fixture("claude", "cross-file-replay", "source-b.jsonl");

        var forward = adapter.ReadSnapshot([sourceA, sourceB]);
        var reverse = adapter.ReadSnapshot([sourceB, sourceA]);

        Assert.Equal(JsonSerializer.Serialize(forward), JsonSerializer.Serialize(reverse));
        var turn = Assert.Single(forward.Turns);
        Assert.Equal(15, turn.Usage.Output);
        Assert.Equal(22, turn.Usage.ProcessedTokens);
        Assert.Equal(1, turn.ApiCallCount);
        Assert.Contains("duplicate_entry_uuid_excluded", turn.Diagnostics);
        Assert.Contains("logical_call_replay_collapsed", turn.Diagnostics);
    }

    [Fact]
    public void AliasConflictDoesNotMixFieldWiseMaxima()
    {
        var result = adapter.ReadSnapshot(Fixture("claude", "alias-conflict.jsonl"));

        Assert.Equal(ClaudeReadStatus.Invalid, result.Status);
        var turn = Assert.Single(result.Turns);
        Assert.Equal(MeasurementQuality.Invalid, turn.Quality);
        Assert.Null(turn.Usage.UncachedInput);
        Assert.Null(turn.Usage.Output);
        Assert.Null(turn.Usage.ProcessedTokens);
        Assert.Null(turn.ApiCallCount);
        Assert.Contains("invalid_alias_conflict", turn.Diagnostics);
    }

    [Fact]
    public void MissingPromptIdentityPreservesUsageAsUnattributed()
    {
        var path = Fixture("claude", "missing-prompt-meta.jsonl");

        var result = adapter.ReadSnapshot(path);

        Assert.Equal(ClaudeReadStatus.Partial, result.Status);
        Assert.Empty(result.Turns);
        var observation = Assert.Single(result.Unattributed);
        Assert.Equal(5, observation.Usage.ProcessedTokens);
        Assert.Equal(ClaudeAttributionStatus.Unattributed, observation.Membership.AttributionStatus);
        Assert.Contains("missing_prompt_identity", observation.Diagnostics);
        Assert.Throws<ClaudeSourceException>(() => adapter.Read(path));
    }

    [Fact]
    public void MainAndChildCallsUsePromptAndParentMetadata()
    {
        var result = adapter.ReadSnapshot(
        [
            Fixture("claude", "main-child", "main.jsonl"),
            Fixture("claude", "main-child", "child.jsonl")
        ]);

        var turn = Assert.Single(result.Turns);
        Assert.Equal("synthetic-claude-session-main-child", turn.RootSessionId);
        Assert.Equal("synthetic-claude-prompt-main-child", turn.RootTurnId);
        Assert.Equal(24, turn.Usage.ProcessedTokens);
        Assert.Equal(2, turn.ApiCallCount);
        Assert.Equal(10, turn.MaxObservedInput);
        var child = Assert.Single(turn.Membership, item => item.AgentId == "synthetic-claude-agent-a");
        Assert.Equal("synthetic-claude-session-main-child", child.ParentSessionId);
        Assert.Equal("synthetic-claude-session-child", child.OriginSessionId);
    }

    [Fact]
    public void ResumeAndForkUseUuidEvidenceWithoutGlobalAliasDeduplication()
    {
        var directory = Fixture("claude", "resume-fork");
        var result = adapter.ReadSnapshot(
        [
            Path.Combine(directory, "session-main.jsonl"),
            Path.Combine(directory, "session-resume.jsonl"),
            Path.Combine(directory, "session-fork.jsonl"),
            Path.Combine(directory, "session-events.json")
        ]);

        Assert.Equal(3, result.Turns.Count);
        var resumedA = Assert.Single(result.Turns, item => item.RootTurnId == "synthetic-claude-prompt-lineage-a");
        var resumedB = Assert.Single(result.Turns, item => item.RootTurnId == "synthetic-claude-prompt-lineage-b");
        var fork = Assert.Single(result.Turns, item => item.RootTurnId == "synthetic-claude-prompt-fork");
        Assert.Equal(10, resumedA.Usage.ProcessedTokens);
        Assert.Equal(11, resumedB.Usage.ProcessedTokens);
        Assert.Equal(11, fork.Usage.ProcessedTokens);
        Assert.Contains("duplicate_entry_uuid_excluded", resumedA.Diagnostics);
        Assert.Contains("copied_entry_uuid_excluded", fork.Diagnostics);
        Assert.Contains("fork_parent_unknown", fork.Diagnostics);
    }

    [Fact]
    public void CacheTtlBreakdownIsPreservedAndMismatchRemainsUnknown()
    {
        var result = adapter.ReadSnapshot(Fixture("claude", "cache-ttl.jsonl"));

        Assert.Equal(ClaudeReadStatus.Invalid, result.Status);
        var valid = Assert.Single(result.Turns, item => item.RootTurnId.EndsWith("ttl-valid", StringComparison.Ordinal));
        var invalid = Assert.Single(result.Turns, item => item.RootTurnId.EndsWith("ttl-invalid", StringComparison.Ordinal));
        Assert.Equal(30, valid.Usage.CacheWrite);
        Assert.Equal(10, valid.Usage.CacheWrite5m);
        Assert.Equal(20, valid.Usage.CacheWrite1h);
        Assert.Equal(47, valid.Usage.ProcessedTokens);
        Assert.Null(invalid.Usage.CacheWrite);
        Assert.Null(invalid.Usage.InputTotal);
        Assert.Null(invalid.Usage.ProcessedTokens);
        Assert.Equal(17, invalid.KnownSubtotal);
        Assert.Equal(1, invalid.UnknownObservationCount);
        Assert.Contains("cache_ttl_total_mismatch", invalid.Diagnostics);
    }

    [Fact]
    public void LateChildReplacesProvisionalProjectionWithoutChangingRootKey()
    {
        var directory = Fixture("claude", "late-child");
        var main = Path.Combine(directory, "revision-1-main.jsonl");
        var child = Path.Combine(directory, "revision-2-child.jsonl");
        var hooks = Path.Combine(directory, "hooks.json");

        var revisionOne = Assert.Single(adapter.ReadSnapshot([main, hooks]).Turns);
        var revisionTwo = Assert.Single(adapter.ReadSnapshot([main, child, hooks]).Turns);

        Assert.Equal(revisionOne.RootSessionId, revisionTwo.RootSessionId);
        Assert.Equal(revisionOne.RootTurnId, revisionTwo.RootTurnId);
        Assert.Equal(20, revisionOne.Usage.ProcessedTokens);
        Assert.Equal(MeasurementQuality.Provisional, revisionOne.Quality);
        Assert.Contains("child_source_pending", revisionOne.Diagnostics);
        Assert.Equal(30, revisionTwo.Usage.ProcessedTokens);
        Assert.Equal(MeasurementQuality.Observed, revisionTwo.Quality);
        Assert.Equal(ClaudeExecutionState.Completed, revisionTwo.ExecutionState);
    }

    [Fact]
    public void CorruptMiddleLineAndIncompleteTailPreserveKnownSubtotal()
    {
        var result = adapter.ReadSnapshot(Fixture("claude", "source-integrity.json"));

        Assert.Equal(ClaudeReadStatus.Partial, result.Status);
        var turn = Assert.Single(result.Turns);
        Assert.Equal(9, turn.Usage.ProcessedTokens);
        Assert.Equal(2, turn.UnknownObservationCount);
        Assert.Equal(MeasurementQuality.Partial, turn.Quality);
        Assert.Contains("malformed_json_line", turn.Diagnostics);
        Assert.Contains("incomplete_tail", turn.Diagnostics);
    }

    [Fact]
    public void UnsupportedVersionAndMalformedOnlySourceAreNotReportedAsZero()
    {
        var unsupported = new ClaudeAdapter("2.1.999")
            .ReadSnapshot(Fixture("claude", "two-turn-streaming-alias.jsonl"));
        Assert.Equal(ClaudeReadStatus.Unsupported, unsupported.Status);
        Assert.Contains("unsupported_provider_version", unsupported.Diagnostics);

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "{incomplete");
            var malformed = adapter.ReadSnapshot(path);
            Assert.Equal(ClaudeReadStatus.Invalid, malformed.Status);
            Assert.Empty(malformed.Turns);
            Assert.Contains("incomplete_tail", malformed.Diagnostics);
            Assert.False(adapter.CanRead(path));
            Assert.Throws<ClaudeSourceException>(() => adapter.Read(path));

            File.WriteAllText(path, "{\"type\":\"event_msg\",\"payload\":{}}\n");
            var unsupportedShape = adapter.ReadSnapshot(path);
            Assert.Equal(ClaudeReadStatus.Unsupported, unsupportedShape.Status);
            Assert.Contains("unsupported_record_shape", unsupportedShape.Diagnostics);
            Assert.Empty(unsupportedShape.Turns);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingCallAliasesRemainUnattributedAndBodiesAreNotRetained()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                "{\"type\":\"user\",\"uuid\":\"user\",\"sessionId\":\"session\",\"promptId\":\"prompt\",\"message\":{\"role\":\"user\",\"content\":\"private prompt\"}}",
                "{\"type\":\"assistant\",\"uuid\":\"assistant\",\"sessionId\":\"session\",\"parentUuid\":\"user\",\"message\":{\"role\":\"assistant\",\"model\":\"model\",\"content\":\"private answer\",\"usage\":{\"input_tokens\":1,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":2,\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":0}}}}"
            ]);

            var result = adapter.ReadSnapshot(path);

            Assert.Equal(ClaudeReadStatus.Partial, result.Status);
            Assert.Empty(result.Turns);
            var observation = Assert.Single(result.Unattributed);
            Assert.Equal(3, observation.Usage.ProcessedTokens);
            Assert.Contains("missing_call_identity", observation.Diagnostics);
            var serialized = JsonSerializer.Serialize(result);
            Assert.DoesNotContain("private prompt", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("private answer", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("content", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Fixture(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "tests",
            "fixtures"));
        return Path.Combine([root, .. parts]);
    }
}


