using System.Text.Json;
using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Adapters.Codex;

public sealed class CodexUsageAdapter : IUsageAdapter
{
    private readonly CodexAdapterCapability _capability;

    public CodexUsageAdapter()
        : this(CodexAdapterCapability.V01534)
    {
    }

    public CodexUsageAdapter(string providerVersion)
        : this(providerVersion == CodexAdapterCapability.V01534.ProviderVersion
            ? CodexAdapterCapability.V01534
            : CodexAdapterCapability.Unsupported(providerVersion, "unknown"))
    {
    }

    public CodexUsageAdapter(CodexAdapterCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        _capability = capability;
    }

    public ProviderKind Provider => ProviderKind.Codex;

    public CodexAdapterCapability Capability => _capability;

    public bool CanRead(string sourcePath)
    {
        if (!_capability.IsVerified || string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        try
        {
            return ExpandSourcePaths([sourcePath]).Any(ContainsSupportedRecord);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<TurnSnapshot> Read(string sourcePath)
    {
        var result = ReadDetailed(sourcePath);
        if (!result.IsSupported)
        {
            throw new CodexUnsupportedFormatException(
                $"The source is not a supported Codex {_capability.ProviderVersion} token_usage_record rollout.");
        }

        if (result.Turns.Count == 0 && result.UnattributedObservations.Count > 0)
        {
            throw new CodexUnsupportedFormatException(
                "The Codex rollout contains usage records without the identity required for Turn projection.");
        }

        return result.ToTurnSnapshots();
    }

    public CodexReadResult ReadDetailed(params string[] sourcePaths) =>
        ReadDetailed((IEnumerable<string>)sourcePaths);

    public CodexReadResult ReadDetailed(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        if (!_capability.IsVerified)
        {
            return UnsupportedResult("unsupported_provider_version");
        }

        var paths = ExpandSourcePaths(sourcePaths).ToArray();
        if (paths.Length == 0)
        {
            return UnsupportedResult("source_not_found");
        }

        var observations = new List<CodexUsageObservation>();
        var rawRecords = new List<RawRecord>();
        var metadata = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var tokenRecordCount = 0;
        var supportedRecordCount = 0;
        long sequence = 0;

        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                sequence++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!TryGetString(root, "type", out var recordType) ||
                        !root.TryGetProperty("payload", out var payload) ||
                        payload.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (recordType == "session_meta")
                    {
                        ReadSessionMetadata(payload, metadata);
                        continue;
                    }

                    if (recordType != "event_msg" ||
                        !TryGetString(payload, "type", out var payloadType) ||
                        payloadType != "token_usage_record")
                    {
                        continue;
                    }

                    tokenRecordCount++;
                    if (!TryReadUsage(payload, "usage", out var delta) ||
                        !TryReadUsage(payload, "turn_token_usage", out var turnSnapshot) ||
                        !TryReadUsage(payload, "thread_token_usage", out var sessionSnapshot))
                    {
                        diagnostics.Add("session_only_usage_unsupported");
                        continue;
                    }

                    supportedRecordCount++;
                    var lineage = new CodexLineage(
                        ReadString(payload, "session_id"),
                        ReadString(payload, "root_turn_id"),
                        ReadString(payload, "thread_id"),
                        ReadString(payload, "turn_id"),
                        null,
                        null,
                        ReadString(payload, "response_id"),
                        ReadString(payload, "origin_execution_id"));
                    var observedAt = ReadString(root, "timestamp");

                    observations.Add(new CodexUsageObservation(
                        CodexUsageKind.CallDelta,
                        lineage,
                        delta,
                        sequence));
                    observations.Add(new CodexUsageObservation(
                        CodexUsageKind.TurnSnapshot,
                        lineage,
                        turnSnapshot,
                        sequence));
                    observations.Add(new CodexUsageObservation(
                        CodexUsageKind.SessionSnapshot,
                        lineage,
                        sessionSnapshot,
                        sequence));
                    rawRecords.Add(new RawRecord(
                        lineage,
                        delta,
                        turnSnapshot,
                        sessionSnapshot,
                        observedAt,
                        sequence));
                }
                catch (JsonException)
                {
                    diagnostics.Add("malformed_json_line");
                }
            }
        }

        if (supportedRecordCount == 0)
        {
            diagnostics.Add(tokenRecordCount > 0
                ? "unsupported_record_shape"
                : "no_token_usage_records");
            return new CodexReadResult(
                _capability,
                [],
                observations,
                [],
                [],
                diagnostics.Order(StringComparer.Ordinal).ToArray(),
                false);
        }

        var enrichedRecords = rawRecords
            .Select(record => EnrichLineage(record, metadata))
            .ToArray();
        observations.Clear();
        foreach (var record in enrichedRecords)
        {
            observations.Add(new CodexUsageObservation(
                CodexUsageKind.CallDelta,
                record.Lineage,
                record.Delta,
                record.Sequence));
            observations.Add(new CodexUsageObservation(
                CodexUsageKind.TurnSnapshot,
                record.Lineage,
                record.TurnSnapshot,
                record.Sequence));
            observations.Add(new CodexUsageObservation(
                CodexUsageKind.SessionSnapshot,
                record.Lineage,
                record.SessionSnapshot,
                record.Sequence));
        }

        var unattributed = new List<CodexUnattributedObservation>();
        var eligibleRecords = new List<RawRecord>();

        foreach (var record in enrichedRecords)
        {
            var recordDiagnostics = ValidateLineage(record.Lineage, metadata);
            var sessionMetadata = record.Lineage.ThreadId is not null &&
                                  metadata.TryGetValue(record.Lineage.ThreadId, out var found)
                ? found
                : null;

            if (recordDiagnostics.Count > 0)
            {
                unattributed.Add(new CodexUnattributedObservation(
                    record.Lineage,
                    record.TurnSnapshot,
                    MeasurementQuality.Unsupported,
                    recordDiagnostics));
                diagnostics.UnionWith(recordDiagnostics);
                continue;
            }

            if (sessionMetadata?.ForkedFromThreadId is not null &&
                record.Lineage.OriginExecutionId is null)
            {
                const string diagnostic = "fork_replay_ambiguous";
                unattributed.Add(new CodexUnattributedObservation(
                    record.Lineage,
                    record.TurnSnapshot,
                    MeasurementQuality.Partial,
                    [diagnostic]));
                diagnostics.Add(diagnostic);
                continue;
            }

            eligibleRecords.Add(record);
        }

        var executionProjections = BuildExecutionProjections(eligibleRecords);
        var excluded = ExcludeForkReplays(executionProjections, metadata);
        var included = executionProjections
            .Where(item => !excluded.Any(excludedItem =>
                excludedItem.ExecutionId == item.ExecutionId &&
                excludedItem.SessionId == item.RootSessionId &&
                excludedItem.ThreadId == item.ThreadId &&
                excludedItem.TurnId == item.TurnId))
            .ToArray();
        if (excluded.Count > 0)
        {
            diagnostics.Add("fork_replay_origin_excluded");
        }

        var turns = BuildRootTurns(included, metadata);
        foreach (var turn in turns)
        {
            diagnostics.UnionWith(turn.Diagnostics);
        }

        return new CodexReadResult(
            _capability,
            turns,
            observations,
            unattributed,
            excluded,
            diagnostics.Order(StringComparer.Ordinal).ToArray(),
            true);
    }

    private CodexReadResult UnsupportedResult(string diagnostic) => new(
        _capability,
        [],
        [],
        [],
        [],
        [diagnostic],
        false);

    private CodexTurnResult[] BuildRootTurns(
        IReadOnlyList<ExecutionProjection> executions,
        IReadOnlyDictionary<string, SessionMetadata> metadata)
    {
        return executions
            .GroupBy(
                static execution => (execution.RootSessionId, execution.RootTurnId),
                RootKeyComparer.Instance)
            .OrderBy(static group => group.Min(static execution => execution.Sequence))
            .Select(group => BuildRootTurn(group.ToArray(), metadata))
            .ToArray();
    }

    private CodexTurnResult BuildRootTurn(
        IReadOnlyList<ExecutionProjection> executions,
        IReadOnlyDictionary<string, SessionMetadata> metadata)
    {
        var rootSessionId = executions[0].RootSessionId;
        var rootTurnId = executions[0].RootTurnId;
        var rootExecution = executions.FirstOrDefault(execution => execution.TurnId == rootTurnId);
        var childExecutions = executions.Where(execution => execution != rootExecution).ToArray();
        var diagnostics = new HashSet<string>(
            executions.SelectMany(static execution => execution.Diagnostics),
            StringComparer.Ordinal);
        var membership = BuildMembership(executions, rootExecution, metadata);

        if (_capability.RootScope == CodexRootScope.Unknown)
        {
            diagnostics.Add("root_scope_unknown");
            return new CodexTurnResult(
                rootSessionId,
                rootTurnId,
                null,
                rootExecution?.NativeUsage,
                childExecutions.Select(static execution => execution.NativeUsage).ToArray(),
                new TokenUsage(),
                MeasurementQuality.Partial,
                null,
                null,
                rootExecution?.NativeUsage.TotalTokens,
                1,
                membership,
                diagnostics.Order(StringComparer.Ordinal).ToArray(),
                executions.MaxBy(static execution => execution.Sequence)?.ObservedAt);
        }

        IReadOnlyList<ExecutionProjection> authoritativeExecutions;
        if (_capability.RootScope == CodexRootScope.ChildInclusive)
        {
            authoritativeExecutions = rootExecution is null ? [] : [rootExecution];
            if (rootExecution is null)
            {
                diagnostics.Add("missing_root_execution");
            }
        }
        else
        {
            authoritativeExecutions = executions;
        }

        if (authoritativeExecutions.Count == 0)
        {
            return new CodexTurnResult(
                rootSessionId,
                rootTurnId,
                null,
                null,
                childExecutions.Select(static execution => execution.NativeUsage).ToArray(),
                new TokenUsage(),
                MeasurementQuality.Partial,
                null,
                null,
                null,
                1,
                membership,
                diagnostics.Order(StringComparer.Ordinal).ToArray(),
                executions.MaxBy(static execution => execution.Sequence)?.ObservedAt);
        }

        var invalid = authoritativeExecutions.Any(static execution => execution.Quality == MeasurementQuality.Invalid);
        var nativeUsage = authoritativeExecutions.Count == 1
            ? authoritativeExecutions[0].NativeUsage
            : SumUsages(authoritativeExecutions.Select(static execution => execution.NativeUsage));
        var quality = CombineQuality(authoritativeExecutions.Select(static execution => execution.Quality));
        if (invalid)
        {
            quality = MeasurementQuality.Invalid;
        }

        var apiCallCount = _capability.RootScope == CodexRootScope.ChildInclusive
            ? null
            : SumNullable(authoritativeExecutions.Select(static execution => execution.ApiCallCount));
        var maxObservedInput = _capability.RootScope == CodexRootScope.ChildInclusive
            ? null
            : MaxNullable(authoritativeExecutions.Select(static execution => execution.MaxObservedInput));
        var usage = invalid ? new TokenUsage() : nativeUsage.ToTokenUsage();
        var unknownCount = diagnostics.Contains("delta_snapshot_mismatch") ||
                           diagnostics.Contains("total_only_semantics_unknown")
            ? 1
            : 0;
        var knownSubtotal = invalid || nativeUsage.IsTotalOnly
            ? null
            : nativeUsage.TotalTokens;

        return new CodexTurnResult(
            rootSessionId,
            rootTurnId,
            nativeUsage,
            rootExecution?.NativeUsage,
            childExecutions.Select(static execution => execution.NativeUsage).ToArray(),
            usage,
            quality,
            apiCallCount,
            maxObservedInput,
            knownSubtotal,
            unknownCount,
            membership,
            diagnostics.Order(StringComparer.Ordinal).ToArray(),
            authoritativeExecutions.MaxBy(static execution => execution.Sequence)?.ObservedAt);
    }

    private CodexMembership[] BuildMembership(
        IReadOnlyList<ExecutionProjection> executions,
        ExecutionProjection? rootExecution,
        IReadOnlyDictionary<string, SessionMetadata> metadata)
    {
        return executions.Select(execution =>
        {
            var isChild = execution != rootExecution;
            var status = _capability.RootScope switch
            {
                CodexRootScope.ChildInclusive when isChild => CodexAttributionStatus.AttributedAlreadyInRoot,
                CodexRootScope.Unknown when isChild => CodexAttributionStatus.Provisional,
                _ when execution.Quality == MeasurementQuality.Invalid => CodexAttributionStatus.Invalid,
                _ => CodexAttributionStatus.Attributed
            };
            var evidence = execution.OriginExecutionId is not null
                ? "origin-execution-id"
                : isChild && _capability.RootScope == CodexRootScope.MainOnly
                    ? "main-only-child-union"
                    : isChild && _capability.RootScope == CodexRootScope.ChildInclusive
                        ? "child-inclusive-capability"
                        : isChild && metadata.ContainsKey(execution.ThreadId)
                            ? "parent-edge-and-record-session"
                            : "record-session-and-root-turn";

            return new CodexMembership(
                execution.ExecutionId,
                execution.RootSessionId,
                status,
                evidence);
        }).ToArray();
    }

    private static ExecutionProjection[] BuildExecutionProjections(
        IEnumerable<RawRecord> records)
    {
        return records
            .GroupBy(static record => (record.Lineage.ThreadId!, record.Lineage.TurnId!), ExecutionKeyComparer.Instance)
            .Select(BuildExecutionProjection)
            .OrderBy(static projection => projection.Sequence)
            .ToArray();
    }

    private static ExecutionProjection BuildExecutionProjection(IGrouping<(string ThreadId, string TurnId), RawRecord> group)
    {
        var ordered = group.OrderBy(static record => record.Sequence).ToArray();
        var final = ordered[^1];
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var snapshotAssessment = AssessUsage(final.TurnSnapshot);
        diagnostics.UnionWith(snapshotAssessment.Diagnostics);

        var deltasByResponse = new Dictionary<string, RawRecord>(StringComparer.Ordinal);
        var allDeltasHaveIdentity = true;
        foreach (var record in ordered)
        {
            if (record.Lineage.ResponseId is null)
            {
                allDeltasHaveIdentity = false;
                continue;
            }

            deltasByResponse[record.Lineage.ResponseId] = record;
        }

        var quality = snapshotAssessment.Quality;
        int? apiCallCount = allDeltasHaveIdentity ? deltasByResponse.Count : null;
        long? maxObservedInput = null;
        if (!allDeltasHaveIdentity)
        {
            diagnostics.Add("missing_response_id");
            quality = MoreSevere(quality, MeasurementQuality.Partial);
        }

        var validDeltas = new List<CodexNativeUsage>();
        foreach (var deltaRecord in deltasByResponse.Values)
        {
            var deltaAssessment = AssessUsage(deltaRecord.Delta);
            if (deltaAssessment.Quality == MeasurementQuality.Invalid)
            {
                diagnostics.Add("invalid_call_delta");
                quality = MoreSevere(quality, MeasurementQuality.Partial);
                continue;
            }

            validDeltas.Add(deltaRecord.Delta);
            if (deltaRecord.Delta.InputTokens is { } input &&
                (maxObservedInput is null || input > maxObservedInput))
            {
                maxObservedInput = input;
            }
        }

        if (allDeltasHaveIdentity && validDeltas.Count == deltasByResponse.Count)
        {
            var deltaSum = SumUsages(validDeltas);
            if (deltaSum != final.TurnSnapshot)
            {
                diagnostics.Add("delta_snapshot_mismatch");
                quality = MoreSevere(quality, MeasurementQuality.Partial);
            }
        }

        return new ExecutionProjection(
            final.Lineage.RootSessionId!,
            final.Lineage.RootTurnId!,
            final.Lineage.ThreadId!,
            final.Lineage.TurnId!,
            final.Lineage.OriginExecutionId,
            final.Lineage.OriginExecutionId ?? $"{final.Lineage.ThreadId}/{final.Lineage.TurnId}",
            final.TurnSnapshot,
            quality,
            apiCallCount,
            maxObservedInput,
            diagnostics.Order(StringComparer.Ordinal).ToArray(),
            final.ObservedAt,
            final.Sequence);
    }

    private static List<CodexExcludedExecution> ExcludeForkReplays(
        IReadOnlyList<ExecutionProjection> executions,
        IReadOnlyDictionary<string, SessionMetadata> metadata)
    {
        var excluded = new List<CodexExcludedExecution>();
        foreach (var originGroup in executions
                     .Where(static execution => execution.OriginExecutionId is not null)
                     .GroupBy(static execution => execution.OriginExecutionId!, StringComparer.Ordinal))
        {
            var distinctExecutions = originGroup
                .GroupBy(
                    static execution => (execution.ThreadId, execution.TurnId),
                    ExecutionKeyComparer.Instance)
                .Select(static group => group.First())
                .ToArray();
            if (distinctExecutions.Length < 2)
            {
                continue;
            }

            var canonical = distinctExecutions
                .OrderBy(execution => metadata.TryGetValue(execution.ThreadId, out var session) &&
                                      session.ForkedFromThreadId is not null ? 1 : 0)
                .ThenBy(static execution => execution.Sequence)
                .First();
            foreach (var replay in distinctExecutions.Where(execution => execution != canonical))
            {
                excluded.Add(new CodexExcludedExecution(
                    replay.ExecutionId,
                    replay.RootSessionId,
                    replay.ThreadId,
                    replay.TurnId,
                    "same-origin-execution-id"));
            }
        }

        return excluded;
    }

    private static UsageAssessment AssessUsage(CodexNativeUsage usage)
    {
        var diagnostics = new List<string>();
        var values = new long?[]
        {
            usage.InputTokens,
            usage.CachedInputTokens,
            usage.OutputTokens,
            usage.ReasoningOutputTokens,
            usage.TotalTokens,
            usage.CacheWriteInputTokens
        };

        if (values.Any(static value => value < 0))
        {
            diagnostics.Add("negative_token_count");
        }

        if (usage.IsTotalOnly)
        {
            diagnostics.Add("total_only_semantics_unknown");
            return new UsageAssessment(MeasurementQuality.Provisional, diagnostics);
        }

        if (values.Any(static value => value is null))
        {
            diagnostics.Add("incomplete_usage_metrics");
        }

        if (usage.InputTokens is { } input &&
            usage.CachedInputTokens is { } cached &&
            cached > input)
        {
            diagnostics.Add("cached_input_exceeds_input");
        }

        if (usage.OutputTokens is { } output &&
            usage.ReasoningOutputTokens is { } reasoning &&
            reasoning > output)
        {
            diagnostics.Add("reasoning_output_exceeds_output");
        }

        if (usage.InputTokens is { } totalInput &&
            usage.OutputTokens is { } totalOutput &&
            usage.TotalTokens is { } total)
        {
            try
            {
                if (checked(totalInput + totalOutput) != total)
                {
                    diagnostics.Add("total_tokens_mismatch");
                }
            }
            catch (OverflowException)
            {
                diagnostics.Add("token_count_overflow");
            }
        }

        if (diagnostics.Count > 0)
        {
            return new UsageAssessment(MeasurementQuality.Invalid, diagnostics);
        }

        if (usage.CacheWriteInputTokens > 0)
        {
            diagnostics.Add("cache_write_semantics_unknown");
            return new UsageAssessment(MeasurementQuality.Provisional, diagnostics);
        }

        return new UsageAssessment(MeasurementQuality.Observed, diagnostics);
    }

    private static CodexNativeUsage SumUsages(IEnumerable<CodexNativeUsage> usages)
    {
        var items = usages.ToArray();
        return new CodexNativeUsage(
            SumField(items, static usage => usage.InputTokens),
            SumField(items, static usage => usage.CachedInputTokens),
            SumField(items, static usage => usage.OutputTokens),
            SumField(items, static usage => usage.ReasoningOutputTokens),
            SumField(items, static usage => usage.TotalTokens),
            SumField(items, static usage => usage.CacheWriteInputTokens));
    }

    private static long? SumField(
        CodexNativeUsage[] usages,
        Func<CodexNativeUsage, long?> selector)
    {
        if (usages.Length == 0 || usages.Any(usage => selector(usage) is null))
        {
            return null;
        }

        long total = 0;
        foreach (var usage in usages)
        {
            total = checked(total + selector(usage)!.Value);
        }

        return total;
    }

    private static int? SumNullable(IEnumerable<int?> values)
    {
        var items = values.ToArray();
        if (items.Any(static value => value is null))
        {
            return null;
        }

        var total = 0;
        foreach (var value in items)
        {
            total = checked(total + value!.Value);
        }

        return total;
    }

    private static long? MaxNullable(IEnumerable<long?> values)
    {
        var items = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
        return items.Length == 0 ? null : items.Max();
    }

    private static MeasurementQuality CombineQuality(IEnumerable<MeasurementQuality> qualities) =>
        qualities.Aggregate(MeasurementQuality.Observed, MoreSevere);

    private static MeasurementQuality MoreSevere(MeasurementQuality left, MeasurementQuality right) =>
        Severity(right) > Severity(left) ? right : left;

    private static int Severity(MeasurementQuality quality) => quality switch
    {
        MeasurementQuality.Observed => 0,
        MeasurementQuality.Provisional => 1,
        MeasurementQuality.Partial => 2,
        MeasurementQuality.Unsupported => 3,
        MeasurementQuality.Invalid => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null)
    };

    private static RawRecord EnrichLineage(
        RawRecord record,
        IReadOnlyDictionary<string, SessionMetadata> metadata)
    {
        if (record.Lineage.ThreadId is null ||
            !metadata.TryGetValue(record.Lineage.ThreadId, out var sessionMetadata))
        {
            return record;
        }

        return record with
        {
            Lineage = record.Lineage with
            {
                ParentThreadId = sessionMetadata.ParentThreadId,
                ForkedFromThreadId = sessionMetadata.ForkedFromThreadId
            }
        };
    }

    private static List<string> ValidateLineage(
        CodexLineage lineage,
        IReadOnlyDictionary<string, SessionMetadata> metadata)
    {
        var diagnostics = new List<string>();
        if (lineage.RootSessionId is null)
        {
            diagnostics.Add("missing_root_session_id");
        }

        if (lineage.RootTurnId is null)
        {
            diagnostics.Add("missing_root_turn_id");
        }

        if (lineage.ThreadId is null)
        {
            diagnostics.Add("missing_thread_id");
        }

        if (lineage.TurnId is null)
        {
            diagnostics.Add("missing_turn_id");
        }

        if (lineage.ThreadId is not null &&
            metadata.TryGetValue(lineage.ThreadId, out var sessionMetadata) &&
            lineage.RootSessionId is not null &&
            !string.Equals(sessionMetadata.RootSessionId, lineage.RootSessionId, StringComparison.Ordinal))
        {
            diagnostics.Add("root_session_lineage_mismatch");
        }

        return diagnostics;
    }

    private static void ReadSessionMetadata(
        JsonElement payload,
        IDictionary<string, SessionMetadata> metadata)
    {
        var threadId = ReadString(payload, "id");
        var rootSessionId = ReadString(payload, "session_id");
        if (threadId is null || rootSessionId is null)
        {
            return;
        }

        metadata[threadId] = new SessionMetadata(
            rootSessionId,
            ReadString(payload, "parent_thread_id"),
            ReadString(payload, "forked_from_id"));
    }

    private static bool TryReadUsage(
        JsonElement payload,
        string propertyName,
        out CodexNativeUsage usage)
    {
        usage = new CodexNativeUsage();
        if (!payload.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadInt64(element, "input_tokens", out var input) ||
            !TryReadInt64(element, "cached_input_tokens", out var cached) ||
            !TryReadInt64(element, "output_tokens", out var output) ||
            !TryReadInt64(element, "reasoning_output_tokens", out var reasoning) ||
            !TryReadInt64(element, "total_tokens", out var total) ||
            !TryReadInt64(element, "cache_write_input_tokens", out var cacheWrite))
        {
            return false;
        }

        usage = new CodexNativeUsage(input, cached, output, reasoning, total, cacheWrite);
        return total is not null || input is not null || output is not null;
    }

    private static bool TryReadInt64(JsonElement element, string propertyName, out long? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number))
        {
            return false;
        }

        value = number;
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGetString(element, propertyName, out var value) ? value : null;

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool ContainsSupportedRecord(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!TryGetString(root, "type", out var recordType) ||
                    recordType != "event_msg" ||
                    !root.TryGetProperty("payload", out var payload) ||
                    !TryGetString(payload, "type", out var payloadType) ||
                    payloadType != "token_usage_record")
                {
                    continue;
                }

                if (TryReadUsage(payload, "usage", out _) &&
                    TryReadUsage(payload, "turn_token_usage", out _) &&
                    TryReadUsage(payload, "thread_token_usage", out _))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Continue looking for a complete supported record.
            }
        }

        return false;
    }

    private static IEnumerable<string> ExpandSourcePaths(IEnumerable<string> sourcePaths)
    {
        foreach (var sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            if (File.Exists(sourcePath))
            {
                yield return Path.GetFullPath(sourcePath);
                continue;
            }

            if (Directory.Exists(sourcePath))
            {
                foreach (var file in Directory.EnumerateFiles(sourcePath, "*.jsonl", SearchOption.AllDirectories)
                             .Order(StringComparer.Ordinal))
                {
                    yield return Path.GetFullPath(file);
                }
            }
        }
    }

    private sealed record SessionMetadata(
        string RootSessionId,
        string? ParentThreadId,
        string? ForkedFromThreadId);

    private sealed record RawRecord(
        CodexLineage Lineage,
        CodexNativeUsage Delta,
        CodexNativeUsage TurnSnapshot,
        CodexNativeUsage SessionSnapshot,
        string? ObservedAt,
        long Sequence);

    private sealed record UsageAssessment(
        MeasurementQuality Quality,
        IReadOnlyList<string> Diagnostics);

    private sealed record ExecutionProjection(
        string RootSessionId,
        string RootTurnId,
        string ThreadId,
        string TurnId,
        string? OriginExecutionId,
        string ExecutionId,
        CodexNativeUsage NativeUsage,
        MeasurementQuality Quality,
        int? ApiCallCount,
        long? MaxObservedInput,
        IReadOnlyList<string> Diagnostics,
        string? ObservedAt,
        long Sequence);

    private sealed class RootKeyComparer : IEqualityComparer<(string RootSessionId, string RootTurnId)>
    {
        public static RootKeyComparer Instance { get; } = new();

        public bool Equals(
            (string RootSessionId, string RootTurnId) x,
            (string RootSessionId, string RootTurnId) y) =>
            string.Equals(x.RootSessionId, y.RootSessionId, StringComparison.Ordinal) &&
            string.Equals(x.RootTurnId, y.RootTurnId, StringComparison.Ordinal);

        public int GetHashCode((string RootSessionId, string RootTurnId) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.RootSessionId),
                StringComparer.Ordinal.GetHashCode(obj.RootTurnId));
    }

    private sealed class ExecutionKeyComparer : IEqualityComparer<(string ThreadId, string TurnId)>
    {
        public static ExecutionKeyComparer Instance { get; } = new();

        public bool Equals(
            (string ThreadId, string TurnId) x,
            (string ThreadId, string TurnId) y) =>
            string.Equals(x.ThreadId, y.ThreadId, StringComparison.Ordinal) &&
            string.Equals(x.TurnId, y.TurnId, StringComparison.Ordinal);

        public int GetHashCode((string ThreadId, string TurnId) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ThreadId),
                StringComparer.Ordinal.GetHashCode(obj.TurnId));
    }
}
