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

        var records = new List<RawRecord>();
        var metadata = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var tokenRecordCount = 0;
        long sequence = 0;
        var scanned = default(CodexScannedLine);

        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var lines = new CodexJsonlLineReader(path);
            while (lines.MoveNext())
            {
                sequence++;
                var line = lines.Current;
                if (IsBlank(line))
                {
                    continue;
                }

                try
                {
                    CodexLineScanner.Scan(line, ref scanned);
                }
                catch (JsonException)
                {
                    diagnostics.Add("malformed_json_line");
                    continue;
                }

                if (scanned.Kind == CodexLineKind.SessionMeta)
                {
                    if (scanned.MetaThreadId is not null && scanned.SessionId is not null)
                    {
                        metadata[scanned.MetaThreadId] = new SessionMetadata(
                            scanned.SessionId,
                            scanned.MetaParentThreadId,
                            scanned.MetaForkedFromThreadId);
                    }

                    continue;
                }

                if (scanned.Kind != CodexLineKind.TokenUsageRecord)
                {
                    continue;
                }

                tokenRecordCount++;
                if (!scanned.HasCompleteUsage)
                {
                    diagnostics.Add("session_only_usage_unsupported");
                    continue;
                }

                records.Add(new RawRecord(
                    new CodexLineage(
                        scanned.SessionId,
                        scanned.RootTurnId,
                        scanned.ThreadId,
                        scanned.TurnId,
                        null,
                        null,
                        scanned.ResponseId,
                        scanned.OriginExecutionId),
                    scanned.Delta,
                    scanned.TurnSnapshot,
                    scanned.SessionSnapshot,
                    scanned.ObservedAt,
                    sequence));
            }
        }

        if (records.Count == 0)
        {
            diagnostics.Add(tokenRecordCount > 0
                ? "unsupported_record_shape"
                : "no_token_usage_records");
            return new CodexReadResult(
                _capability,
                [],
                [],
                [],
                [],
                diagnostics.Order(StringComparer.Ordinal).ToArray(),
                false);
        }

        // A session_meta line may follow the records it describes, so lineage is completed only once
        // the whole rollout has been read. With no metadata every lookup would miss, so skip it.
        if (metadata.Count > 0)
        {
            for (var index = 0; index < records.Count; index++)
            {
                records[index] = EnrichLineage(records[index], metadata);
            }
        }

        var observations = new List<CodexUsageObservation>(records.Count * 3);
        foreach (var record in records)
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
        var eligibleRecords = new List<RawRecord>(records.Count);

        foreach (var record in records)
        {
            var recordDiagnostics = ValidateLineage(record.Lineage, metadata);
            if (recordDiagnostics is not null)
            {
                unattributed.Add(new CodexUnattributedObservation(
                    record.Lineage,
                    record.TurnSnapshot,
                    MeasurementQuality.Unsupported,
                    recordDiagnostics));
                diagnostics.UnionWith(recordDiagnostics);
                continue;
            }

            if (record.Lineage.OriginExecutionId is null &&
                record.Lineage.ThreadId is not null &&
                metadata.TryGetValue(record.Lineage.ThreadId, out var sessionMetadata) &&
                sessionMetadata.ForkedFromThreadId is not null)
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
        var included = excluded.Count == 0
            ? executionProjections
            : executionProjections
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
        List<RawRecord> records)
    {
        if (records.Count == 0)
        {
            return [];
        }

        // Grouped by hand so the whole rollout does not pass through LINQ's grouping allocations.
        // Groups keep first-appearance order, which is what GroupBy produced.
        var groupIndexByKey = new Dictionary<(string ThreadId, string TurnId), int>(ExecutionKeyComparer.Instance);
        var groups = new List<List<RawRecord>>();
        foreach (var record in records)
        {
            var key = (record.Lineage.ThreadId!, record.Lineage.TurnId!);
            if (!groupIndexByKey.TryGetValue(key, out var groupIndex))
            {
                groupIndex = groups.Count;
                groupIndexByKey.Add(key, groupIndex);
                groups.Add([]);
            }

            groups[groupIndex].Add(record);
        }

        var projections = new ExecutionProjection[groups.Count];
        var sequences = new long[groups.Count];
        for (var index = 0; index < groups.Count; index++)
        {
            projections[index] = BuildExecutionProjection(groups[index]);
            sequences[index] = projections[index].Sequence;
        }

        // Every projection carries the sequence of the last line in its group, and line sequences are
        // unique, so ordering by sequence is total and needs no stable-sort tiebreak.
        Array.Sort(sequences, projections);
        return projections;
    }

    private static ExecutionProjection BuildExecutionProjection(List<RawRecord> group)
    {
        // Records are appended while the rollout is read, so a group is already ascending by sequence.
        var final = group[^1];
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var quality = AssessUsage(final.TurnSnapshot, diagnostics);

        var deltasByResponse = new Dictionary<string, CodexNativeUsage>(StringComparer.Ordinal);
        var allDeltasHaveIdentity = true;
        foreach (var record in group)
        {
            if (record.Lineage.ResponseId is null)
            {
                allDeltasHaveIdentity = false;
                continue;
            }

            deltasByResponse[record.Lineage.ResponseId] = record.Delta;
        }

        int? apiCallCount = allDeltasHaveIdentity ? deltasByResponse.Count : null;
        long? maxObservedInput = null;
        if (!allDeltasHaveIdentity)
        {
            diagnostics.Add("missing_response_id");
            quality = MoreSevere(quality, MeasurementQuality.Partial);
        }

        var validDeltas = new List<CodexNativeUsage>(deltasByResponse.Count);
        foreach (var delta in deltasByResponse.Values)
        {
            // A call delta only contributes its verdict here; its own diagnostics were never surfaced.
            if (AssessUsage(delta, null) == MeasurementQuality.Invalid)
            {
                diagnostics.Add("invalid_call_delta");
                quality = MoreSevere(quality, MeasurementQuality.Partial);
                continue;
            }

            validDeltas.Add(delta);
            if (delta.InputTokens is { } input &&
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

    /// <summary>
    /// Grades one usage object and, when <paramref name="diagnostics"/> is supplied, records why.
    /// </summary>
    /// <remarks>
    /// Called once per call delta, so the previous array plus <see cref="List{T}"/> per invocation was
    /// pure overhead for the common clean record. The checks, their order and their verdicts are
    /// unchanged; only the collection of the reasons became optional.
    /// </remarks>
    private static MeasurementQuality AssessUsage(CodexNativeUsage usage, ICollection<string>? diagnostics)
    {
        var invalid = false;
        if (IsNegative(usage.InputTokens) ||
            IsNegative(usage.CachedInputTokens) ||
            IsNegative(usage.OutputTokens) ||
            IsNegative(usage.ReasoningOutputTokens) ||
            IsNegative(usage.TotalTokens) ||
            IsNegative(usage.CacheWriteInputTokens))
        {
            diagnostics?.Add("negative_token_count");
            invalid = true;
        }

        if (usage.IsTotalOnly)
        {
            diagnostics?.Add("total_only_semantics_unknown");
            return MeasurementQuality.Provisional;
        }

        if (usage.InputTokens is null ||
            usage.CachedInputTokens is null ||
            usage.OutputTokens is null ||
            usage.ReasoningOutputTokens is null ||
            usage.TotalTokens is null ||
            usage.CacheWriteInputTokens is null)
        {
            diagnostics?.Add("incomplete_usage_metrics");
            invalid = true;
        }

        if (usage.InputTokens is { } input &&
            usage.CachedInputTokens is { } cached &&
            cached > input)
        {
            diagnostics?.Add("cached_input_exceeds_input");
            invalid = true;
        }

        if (usage.OutputTokens is { } output &&
            usage.ReasoningOutputTokens is { } reasoning &&
            reasoning > output)
        {
            diagnostics?.Add("reasoning_output_exceeds_output");
            invalid = true;
        }

        if (usage.InputTokens is { } totalInput &&
            usage.OutputTokens is { } totalOutput &&
            usage.TotalTokens is { } total)
        {
            try
            {
                if (checked(totalInput + totalOutput) != total)
                {
                    diagnostics?.Add("total_tokens_mismatch");
                    invalid = true;
                }
            }
            catch (OverflowException)
            {
                diagnostics?.Add("token_count_overflow");
                invalid = true;
            }
        }

        if (invalid)
        {
            return MeasurementQuality.Invalid;
        }

        if (usage.CacheWriteInputTokens > 0)
        {
            diagnostics?.Add("cache_write_semantics_unknown");
            return MeasurementQuality.Provisional;
        }

        return MeasurementQuality.Observed;
    }

    private static bool IsNegative(long? value) => value is { } number && number < 0;

    private static CodexNativeUsage SumUsages(IEnumerable<CodexNativeUsage> usages) =>
        SumUsages(usages as IReadOnlyList<CodexNativeUsage> ?? usages.ToArray());

    /// <summary>
    /// Sums each metric, keeping the rule that one missing value makes the whole metric unknown.
    /// </summary>
    /// <remarks>
    /// The missing values are located before anything is added, because the previous per-field
    /// implementation also refused to sum a metric it had already rejected. Summing first would turn
    /// an unknown metric into an <see cref="OverflowException"/>.
    /// </remarks>
    private static CodexNativeUsage SumUsages(IReadOnlyList<CodexNativeUsage> usages)
    {
        if (usages.Count == 0)
        {
            return new CodexNativeUsage();
        }

        bool hasInput = true, hasCached = true, hasOutput = true;
        bool hasReasoning = true, hasTotal = true, hasCacheWrite = true;
        for (var index = 0; index < usages.Count; index++)
        {
            var usage = usages[index];
            hasInput &= usage.InputTokens is not null;
            hasCached &= usage.CachedInputTokens is not null;
            hasOutput &= usage.OutputTokens is not null;
            hasReasoning &= usage.ReasoningOutputTokens is not null;
            hasTotal &= usage.TotalTokens is not null;
            hasCacheWrite &= usage.CacheWriteInputTokens is not null;
        }

        long input = 0, cached = 0, output = 0, reasoning = 0, total = 0, cacheWrite = 0;
        for (var index = 0; index < usages.Count; index++)
        {
            var usage = usages[index];
            if (hasInput) input = checked(input + usage.InputTokens!.Value);
            if (hasCached) cached = checked(cached + usage.CachedInputTokens!.Value);
            if (hasOutput) output = checked(output + usage.OutputTokens!.Value);
            if (hasReasoning) reasoning = checked(reasoning + usage.ReasoningOutputTokens!.Value);
            if (hasTotal) total = checked(total + usage.TotalTokens!.Value);
            if (hasCacheWrite) cacheWrite = checked(cacheWrite + usage.CacheWriteInputTokens!.Value);
        }

        return new CodexNativeUsage(
            hasInput ? input : null,
            hasCached ? cached : null,
            hasOutput ? output : null,
            hasReasoning ? reasoning : null,
            hasTotal ? total : null,
            hasCacheWrite ? cacheWrite : null);
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

    /// <summary>
    /// Returns the lineage problems of one record, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// Runs once per usage record, and a well formed rollout produces no problems at all, so the list
    /// is only created when there is something to put in it.
    /// </remarks>
    private static List<string>? ValidateLineage(
        CodexLineage lineage,
        IReadOnlyDictionary<string, SessionMetadata> metadata)
    {
        List<string>? diagnostics = null;
        if (lineage.RootSessionId is null)
        {
            (diagnostics ??= []).Add("missing_root_session_id");
        }

        if (lineage.RootTurnId is null)
        {
            (diagnostics ??= []).Add("missing_root_turn_id");
        }

        if (lineage.ThreadId is null)
        {
            (diagnostics ??= []).Add("missing_thread_id");
        }

        if (lineage.TurnId is null)
        {
            (diagnostics ??= []).Add("missing_turn_id");
        }

        if (lineage.ThreadId is not null &&
            metadata.TryGetValue(lineage.ThreadId, out var sessionMetadata) &&
            lineage.RootSessionId is not null &&
            !string.Equals(sessionMetadata.RootSessionId, lineage.RootSessionId, StringComparison.Ordinal))
        {
            (diagnostics ??= []).Add("root_session_lineage_mismatch");
        }

        return diagnostics;
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var value in line)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// One supported usage record, buffered until the whole rollout has been read.
    /// </summary>
    /// <remarks>
    /// A struct because a large rollout buffers one of these per record and none of them outlive the
    /// projection, so there is nothing for a reference type to share.
    /// </remarks>
    private readonly record struct RawRecord(
        CodexLineage Lineage,
        CodexNativeUsage Delta,
        CodexNativeUsage TurnSnapshot,
        CodexNativeUsage SessionSnapshot,
        string? ObservedAt,
        long Sequence);

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
