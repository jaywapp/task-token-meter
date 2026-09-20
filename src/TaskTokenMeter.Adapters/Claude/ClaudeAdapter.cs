using System.Globalization;
using System.Text;
using System.Text.Json;
using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Adapters.Claude;

public sealed class ClaudeAdapter : IUsageAdapter
{
    public const string SupportedProviderVersion = "2.1.278";
    public const string SupportedRecordShape = "claude-transcript-assistant-usage-v2.1.278";

    private static readonly StringComparer IdComparer = StringComparer.Ordinal;
    private readonly string providerVersion;

    public ClaudeAdapter(string providerVersion = SupportedProviderVersion)
    {
        this.providerVersion = providerVersion;
    }

    public ProviderKind Provider => ProviderKind.Claude;

    public ClaudeAdapterCapabilities Capabilities { get; } = new(
        SupportedProviderVersion,
        SupportedRecordShape,
        "transcript-prompt-id",
        "request-message-alias",
        "lineage",
        "attributed-child-sources",
        "ttl-breakdown",
        "none",
        "entry-uuid",
        "claude-hook-empty-stdout-v2.1.278");

    public bool CanRead(string sourcePath)
    {
        try
        {
            var result = ReadSnapshot(sourcePath);
            return result.Status is ClaudeReadStatus.Supported or ClaudeReadStatus.Partial;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public IReadOnlyList<TurnSnapshot> Read(string sourcePath)
    {
        var result = ReadSnapshot(sourcePath);
        if (result.Status is ClaudeReadStatus.Unsupported or ClaudeReadStatus.Invalid)
        {
            throw new ClaudeSourceException(
                $"Claude source could not be projected ({result.Status.ToString().ToLowerInvariant()}).");
        }

        if (result.Turns.Count == 0 && result.Unattributed.Count > 0)
        {
            throw new ClaudeSourceException("Claude source contains usage that cannot be attributed to a turn.");
        }

        return result.Turns
            .Select(turn => new TurnSnapshot(
                "claude-code",
                turn.RootSessionId,
                turn.RootTurnId,
                turn.Usage,
                turn.Quality,
                turn.ApiCallCount,
                turn.MaxObservedInput,
                turn.ObservedAt))
            .ToArray();
    }

    public ClaudeReadResult ReadSnapshot(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        IEnumerable<string> paths = Directory.Exists(sourcePath)
            ? Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Where(IsCandidateFile)
            : [sourcePath];

        return ReadSnapshot(paths);
    }

    public ClaudeReadResult ReadSnapshot(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var paths = sourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
        {
            return UnsupportedResult("source_missing");
        }

        if (!string.Equals(providerVersion, SupportedProviderVersion, StringComparison.Ordinal))
        {
            return UnsupportedResult("unsupported_provider_version");
        }

        var captured = paths.Select(Capture).ToArray();
        return Parse(captured);
    }

    private ClaudeReadResult Parse(IReadOnlyList<CapturedSource> sources)
    {
        var parseState = new ParseState();
        foreach (var source in sources)
        {
            ParseSource(source, parseState);
        }

        if (parseState.DeclaredProviderVersions.Any(
                version => !string.Equals(version, SupportedProviderVersion, StringComparison.Ordinal)))
        {
            return UnsupportedResult("unsupported_provider_version");
        }

        if (parseState.Records.Count == 0)
        {
            if (parseState.SourceDiagnostics.Count > 0)
            {
                return new ClaudeReadResult(
                    ClaudeReadStatus.Invalid,
                    Capabilities,
                    [],
                    [],
                    OrderedDistinct(parseState.SourceDiagnostics));
            }

            return UnsupportedResult("unsupported_record_shape");
        }

        var dedupe = DeduplicateEntries(parseState.Records, parseState.ForkSessions);
        var attributed = AttributeRecords(dedupe.Records);
        var callResults = ResolveCalls(attributed.UsageObservations, dedupe);
        var unattributed = BuildUnattributed(attributed.UnattributedUsage);
        unattributed.AddRange(BuildUnattributed(callResults.Where(call => call.Kind == CallKind.MissingCallIdentity)));
        var turns = BuildTurns(callResults, attributed, parseState, dedupe);

        var diagnostics = new List<string>(parseState.SourceDiagnostics);
        diagnostics.AddRange(callResults
            .SelectMany(call => call.Diagnostics)
            .Where(IsGlobalCallDiagnostic));
        diagnostics.AddRange(unattributed
            .SelectMany(item => item.Diagnostics)
            .Where(IsGlobalUnattributedDiagnostic));
        if (dedupe.Records.Any(record => record.Kind == RecordKind.MissingPrompt) &&
            dedupe.Records.Any(record => record.Kind is RecordKind.NonHuman or RecordKind.Ignored))
        {
            diagnostics.Add("non_human_entry_does_not_open_turn");
        }
        if (parseState.ForkSessions.Count > 0)
        {
            diagnostics.Add("fork_parent_unknown");
        }

        var status = DetermineStatus(turns, unattributed, diagnostics);
        return new ClaudeReadResult(
            status,
            Capabilities,
            turns,
            unattributed,
            OrderedDistinct(diagnostics));
    }

    private static ClaudeReadStatus DetermineStatus(
        List<ClaudeTurnObservation> turns,
        List<ClaudeUnattributedObservation> unattributed,
        List<string> diagnostics)
    {
        if (turns.Count == 0 && unattributed.Count == 0)
        {
            return diagnostics.Count == 0 ? ClaudeReadStatus.Unsupported : ClaudeReadStatus.Invalid;
        }

        if (turns.Any(turn => turn.Quality == MeasurementQuality.Invalid) ||
            unattributed.Any(item => item.Quality == MeasurementQuality.Invalid))
        {
            return ClaudeReadStatus.Invalid;
        }

        if (unattributed.Count > 0 ||
            turns.Any(turn => turn.Quality is MeasurementQuality.Partial or MeasurementQuality.Provisional) ||
            diagnostics.Any(IsSourceIntegrityDiagnostic))
        {
            return ClaudeReadStatus.Partial;
        }

        return ClaudeReadStatus.Supported;
    }

    private static CapturedSource Capture(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Claude source does not exist.", path);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > Array.MaxLength)
        {
            throw new IOException("Claude source is too large to snapshot safely.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        var bytesRead = 0;
        while (bytesRead < bytes.Length)
        {
            var read = stream.Read(bytes, bytesRead, bytes.Length - bytesRead);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var text = encoding.GetString(bytes, 0, bytesRead);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return new CapturedSource(path, text);
    }

    private static bool IsCandidateFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static void ParseSource(CapturedSource source, ParseState state)
    {
        if (Path.GetExtension(source.Path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            ParseJsonLines(source, state);
            return;
        }

        if (!Path.GetExtension(source.Path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            state.SourceDiagnostics.Add("unsupported_source_extension");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(source.Text);
            var root = document.RootElement;
            AddDeclaredVersion(root, state);

            if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
            {
                ParseSourceContainer(source.Path, records, state);
                return;
            }

            if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            {
                ParseEvents(events, state);
                return;
            }

            state.IgnoredSupplementalSources++;
        }
        catch (JsonException)
        {
            state.SourceDiagnostics.Add("malformed_json_source");
        }
    }

    private static void ParseJsonLines(CapturedSource source, ParseState state)
    {
        var normalized = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var lastContentLine = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var hasTrailingNewline = normalized.EndsWith('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var record = ParseRecord(document.RootElement, source.Path, index + 1);
                if (record is not null)
                {
                    state.Records.Add(record);
                }
                else
                {
                    state.RecognizedInvalidRecords++;
                }
            }
            catch (JsonException)
            {
                state.SourceDiagnostics.Add(index == lastContentLine && !hasTrailingNewline
                    ? "incomplete_tail"
                    : "malformed_json_line");
            }
        }
    }

    private static void ParseSourceContainer(string sourcePath, JsonElement records, ParseState state)
    {
        foreach (var item in records.EnumerateArray())
        {
            var lineNumber = ReadInt32(item, "lineNumber") ?? 0;
            var itemState = ReadString(item, "state");
            if (string.Equals(itemState, "valid", StringComparison.Ordinal) &&
                item.TryGetProperty("record", out var recordElement))
            {
                var record = ParseRecord(recordElement, sourcePath, lineNumber);
                if (record is not null)
                {
                    state.Records.Add(record);
                }

                continue;
            }

            if (string.Equals(itemState, "incomplete-tail", StringComparison.Ordinal))
            {
                state.SourceDiagnostics.Add("incomplete_tail");
            }
            else
            {
                state.SourceDiagnostics.Add("malformed_json_line");
            }
        }
    }

    private static void AddDeclaredVersion(JsonElement root, ParseState state)
    {
        var version = ReadString(root, "providerVersion");
        if (!string.IsNullOrWhiteSpace(version))
        {
            state.DeclaredProviderVersions.Add(version);
        }
    }

    private static void ParseEvents(JsonElement events, ParseState state)
    {
        foreach (var item in events.EnumerateArray())
        {
            var eventName = ReadString(item, "hook_event_name");
            var sessionId = ReadString(item, "session_id");
            var promptId = ReadString(item, "prompt_id");
            var agentId = ReadString(item, "agent_id");
            var source = ReadString(item, "source");
            var backgroundTasks = ReadStrings(item, "background_tasks");
            state.Events.Add(new HookEvent(eventName, sessionId, promptId, agentId, source, backgroundTasks));

            if (string.Equals(eventName, "SessionStart", StringComparison.Ordinal) &&
                string.Equals(source, "fork", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(sessionId))
            {
                state.ForkSessions.Add(sessionId);
            }
        }
    }

    private static ParsedRecord? ParseRecord(JsonElement root, string sourcePath, int lineNumber)
    {
        var type = ReadString(root, "type");
        var sessionId = ReadString(root, "sessionId");
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var uuid = ReadString(root, "uuid");
        var parentUuid = ReadString(root, "parentUuid");
        var parentSessionId = ReadString(root, "parentSessionId");
        var agentId = ReadString(root, "agentId");
        var promptId = ReadString(root, "promptId");
        var syntheticEntryKind = ReadString(root, "syntheticEntryKind");
        var isMeta = ReadBoolean(root, "isMeta") ?? false;
        var timestamp = ReadTimestamp(root, "timestamp");

        root.TryGetProperty("message", out var message);
        var role = message.ValueKind == JsonValueKind.Object ? ReadString(message, "role") : null;
        var messageId = message.ValueKind == JsonValueKind.Object ? ReadString(message, "id") : null;
        var model = message.ValueKind == JsonValueKind.Object ? ReadString(message, "model") : null;
        var requestId = ReadString(root, "requestId");

        ParsedUsage? usage = null;
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("usage", out var usageElement) &&
            usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = ParseUsage(usageElement);
        }

        var kind = ClassifyRecord(type, role, promptId, syntheticEntryKind, isMeta, message);
        if (kind == RecordKind.Ignored && usage is null)
        {
            return new ParsedRecord(
                sourcePath,
                lineNumber,
                timestamp,
                RecordKind.Ignored,
                uuid,
                parentUuid,
                sessionId,
                parentSessionId,
                agentId,
                promptId,
                requestId,
                messageId,
                model,
                null);
        }

        if (usage is not null)
        {
            kind = RecordKind.AssistantUsage;
        }

        return new ParsedRecord(
            sourcePath,
            lineNumber,
            timestamp,
            kind,
            uuid,
            parentUuid,
            sessionId,
            parentSessionId,
            agentId,
            promptId,
            requestId,
            messageId,
            model,
            usage);
    }

    private static RecordKind ClassifyRecord(
        string type,
        string? role,
        string? promptId,
        string? syntheticEntryKind,
        bool isMeta,
        JsonElement message)
    {
        if (!string.Equals(type, "user", StringComparison.Ordinal) ||
            !string.Equals(role, "user", StringComparison.Ordinal))
        {
            return RecordKind.Ignored;
        }

        if (isMeta || string.Equals(syntheticEntryKind, "task-notification", StringComparison.Ordinal))
        {
            return string.Equals(syntheticEntryKind, "task-notification", StringComparison.Ordinal)
                ? RecordKind.TaskNotification
                : RecordKind.NonHuman;
        }

        if (!string.IsNullOrWhiteSpace(syntheticEntryKind))
        {
            if (!string.Equals(syntheticEntryKind, "human-prompt", StringComparison.Ordinal))
            {
                return RecordKind.NonHuman;
            }

            return string.IsNullOrWhiteSpace(promptId) ? RecordKind.MissingPrompt : RecordKind.HumanPrompt;
        }

        if (ContainsToolResult(message))
        {
            return RecordKind.NonHuman;
        }

        return string.IsNullOrWhiteSpace(promptId) ? RecordKind.MissingPrompt : RecordKind.HumanPrompt;
    }

    private static bool ContainsToolResult(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                string.Equals(ReadString(item, "type"), "tool_result", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ParsedUsage ParseUsage(JsonElement usage)
    {
        long? cacheWrite5m = null;
        long? cacheWrite1h = null;
        if (usage.TryGetProperty("cache_creation", out var cacheCreation) &&
            cacheCreation.ValueKind == JsonValueKind.Object)
        {
            cacheWrite5m = ReadInt64(cacheCreation, "ephemeral_5m_input_tokens");
            cacheWrite1h = ReadInt64(cacheCreation, "ephemeral_1h_input_tokens");
        }

        long? thinkingTokens = null;
        if (usage.TryGetProperty("output_tokens_details", out var outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object)
        {
            thinkingTokens = ReadInt64(outputDetails, "thinking_tokens");
        }

        return new ParsedUsage(
            ReadInt64(usage, "input_tokens"),
            ReadInt64(usage, "cache_creation_input_tokens"),
            ReadInt64(usage, "cache_read_input_tokens"),
            ReadInt64(usage, "output_tokens"),
            cacheWrite5m,
            cacheWrite1h,
            thinkingTokens);
    }

    private static DedupeResult DeduplicateEntries(
        IReadOnlyList<ParsedRecord> records,
        HashSet<string> forkSessions)
    {
        var duplicateSessions = new HashSet<string>(IdComparer);
        var copiedSessions = new HashSet<string>(IdComparer);
        var duplicateUuids = new HashSet<string>(IdComparer);
        var selected = new List<ParsedRecord>();

        foreach (var record in records.Where(record => string.IsNullOrWhiteSpace(record.Uuid)))
        {
            selected.Add(record);
        }

        foreach (var group in records
                     .Where(record => !string.IsNullOrWhiteSpace(record.Uuid))
                     .GroupBy(record => record.Uuid!, IdComparer))
        {
            var ordered = group
                .OrderBy(record => forkSessions.Contains(record.SessionId) ? 1 : 0)
                .ThenBy(record => string.IsNullOrWhiteSpace(record.ParentUuid) ? 1 : 0)
                .ThenBy(record => record.Timestamp ?? DateTimeOffset.MaxValue)
                .ThenBy(record => record.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.LineNumber)
                .ToArray();
            selected.Add(ordered[0]);

            if (ordered.Length == 1)
            {
                continue;
            }

            duplicateUuids.Add(group.Key);
            var keptSession = ordered[0].SessionId;
            foreach (var duplicate in ordered.Skip(1))
            {
                if (string.Equals(duplicate.SessionId, keptSession, StringComparison.Ordinal))
                {
                    duplicateSessions.Add(keptSession);
                }
                else
                {
                    copiedSessions.Add(duplicate.SessionId);
                }
            }
        }

        var orderedSelected = selected
            .OrderBy(record => record.Timestamp ?? DateTimeOffset.MaxValue)
            .ThenBy(record => record.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.LineNumber)
            .ToArray();
        return new DedupeResult(orderedSelected, duplicateSessions, copiedSessions, duplicateUuids);
    }

    private static AttributionResult AttributeRecords(IReadOnlyList<ParsedRecord> records)
    {
        var contextByEntry = new Dictionary<string, PromptContext>(IdComparer);
        var activeBySession = new Dictionary<string, PromptContext>(IdComparer);
        var missingPromptSessions = new HashSet<string>(IdComparer);
        var usage = new List<UsageObservation>();
        var unattributed = new List<UnattributedUsage>();
        var notifications = new HashSet<TurnKey>();

        foreach (var record in records)
        {
            if (record.Kind == RecordKind.HumanPrompt && !string.IsNullOrWhiteSpace(record.PromptId))
            {
                var promptContext = new PromptContext(
                    record.ParentSessionId ?? record.SessionId,
                    record.PromptId,
                    record.SessionId,
                    record.ParentSessionId,
                    record.AgentId);
                activeBySession[record.SessionId] = promptContext;
                missingPromptSessions.Remove(record.SessionId);
                if (!string.IsNullOrWhiteSpace(record.Uuid))
                {
                    contextByEntry[record.Uuid] = promptContext;
                }

                continue;
            }

            if (record.Kind == RecordKind.MissingPrompt)
            {
                activeBySession.Remove(record.SessionId);
                missingPromptSessions.Add(record.SessionId);
                continue;
            }

            if (record.Kind == RecordKind.TaskNotification)
            {
                if (activeBySession.TryGetValue(record.SessionId, out var notificationContext))
                {
                    notifications.Add(new TurnKey(notificationContext.RootSessionId, notificationContext.RootTurnId));
                }

                continue;
            }

            PromptContext? context = null;
            if (!string.IsNullOrWhiteSpace(record.ParentUuid) &&
                contextByEntry.TryGetValue(record.ParentUuid, out var parentContext))
            {
                context = parentContext;
            }
            else if (activeBySession.TryGetValue(record.SessionId, out var activeContext))
            {
                context = activeContext;
            }

            if (!string.IsNullOrWhiteSpace(record.Uuid) && context is not null)
            {
                contextByEntry[record.Uuid] = context;
            }

            if (record.Kind != RecordKind.AssistantUsage || record.Usage is null)
            {
                continue;
            }

            if (context is null)
            {
                unattributed.Add(new UnattributedUsage(record, missingPromptSessions.Contains(record.SessionId)));
            }
            else
            {
                usage.Add(new UsageObservation(record, context));
            }
        }

        return new AttributionResult(usage, unattributed, notifications);
    }

    private static List<CallResult> ResolveCalls(
        IReadOnlyList<UsageObservation> observations,
        DedupeResult dedupe)
    {
        var results = new List<CallResult>();
        foreach (var sessionGroup in observations.GroupBy(item => item.Record.SessionId, IdComparer))
        {
            var union = new AliasUnion();
            var withAlias = new List<UsageObservation>();
            foreach (var observation in sessionGroup)
            {
                var aliases = GetAliases(observation.Record).ToArray();
                if (aliases.Length == 0)
                {
                    results.Add(CallResult.UnattributedIdentity(observation));
                    continue;
                }

                withAlias.Add(observation);
                union.Add(aliases[0]);
                for (var index = 1; index < aliases.Length; index++)
                {
                    union.Union(aliases[0], aliases[index]);
                }
            }

            foreach (var aliasGroup in withAlias.GroupBy(item => union.Find(GetAliases(item.Record).First()), IdComparer))
            {
                results.Add(ResolveCall(aliasGroup.ToArray(), dedupe));
            }
        }

        return results;
    }

    private static CallResult ResolveCall(IReadOnlyList<UsageObservation> observations, DedupeResult dedupe)
    {
        var contexts = observations
            .Select(item => new TurnKey(item.Context.RootSessionId, item.Context.RootTurnId))
            .Distinct()
            .ToArray();
        var requestIds = observations.Select(item => item.Record.RequestId).WhereNotNull().Distinct(IdComparer).ToArray();
        var messageIds = observations.Select(item => item.Record.MessageId).WhereNotNull().Distinct(IdComparer).ToArray();
        var executionId = requestIds.FirstOrDefault() ?? messageIds.First();
        var context = observations[0].Context;

        var conflicts = contexts.Length != 1 || requestIds.Length > 1 || messageIds.Length > 1 ||
                        !AllCompatible(observations);
        if (conflicts)
        {
            return CallResult.InvalidAlias(
                context,
                executionId,
                observations.Select(item => item.Record).ToArray());
        }

        var selected = observations
            .OrderByDescending(item => item.Record.Usage!.OutputTokens ?? long.MinValue)
            .ThenByDescending(item => item.Record.Timestamp ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.Record.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Record.LineNumber)
            .First();
        var usage = selected.Record.Usage!;
        var diagnostics = new List<string>();

        if (observations.Any(item => item.Record.Uuid is not null && dedupe.DuplicateUuids.Contains(item.Record.Uuid)))
        {
            diagnostics.Add("duplicate_entry_uuid_excluded");
        }

        if (observations.Select(item => item.Record.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            diagnostics.Add("logical_call_replay_collapsed");
        }

        if (usage.ThinkingTokens is not null)
        {
            diagnostics.Add("reasoning_semantics_unknown");
        }

        if (!usage.HasCompleteShape || usage.HasNegativeValue ||
            string.IsNullOrWhiteSpace(selected.Record.Model))
        {
            diagnostics.Add("invalid_usage_shape");
            return CallResult.InvalidUsage(context, executionId, selected.Record, usage, diagnostics);
        }

        if (checked(usage.CacheWrite5m!.Value + usage.CacheWrite1h!.Value) != usage.CacheCreationInputTokens)
        {
            diagnostics.Add("cache_ttl_total_mismatch");
            return CallResult.InvalidTtl(context, executionId, selected.Record, usage, diagnostics);
        }

        return CallResult.Valid(context, executionId, selected.Record, usage, observations, diagnostics);
    }

    private static bool AllCompatible(IReadOnlyList<UsageObservation> observations)
    {
        var first = observations[0].Record;
        return observations.All(item =>
            string.Equals(item.Record.Model, first.Model, StringComparison.Ordinal) &&
            item.Record.Usage!.InputTokens == first.Usage!.InputTokens &&
            item.Record.Usage.CacheCreationInputTokens == first.Usage.CacheCreationInputTokens &&
            item.Record.Usage.CacheReadInputTokens == first.Usage.CacheReadInputTokens &&
            item.Record.Usage.CacheWrite5m == first.Usage.CacheWrite5m &&
            item.Record.Usage.CacheWrite1h == first.Usage.CacheWrite1h);
    }

    private static IEnumerable<string> GetAliases(ParsedRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.RequestId))
        {
            yield return $"request:{record.RequestId}";
        }

        if (!string.IsNullOrWhiteSpace(record.MessageId))
        {
            yield return $"message:{record.MessageId}";
        }
    }

    private static List<ClaudeUnattributedObservation> BuildUnattributed(
        IReadOnlyList<UnattributedUsage> observations)
    {
        var result = new List<ClaudeUnattributedObservation>();
        foreach (var item in observations)
        {
            var usage = item.Record.Usage!;
            var native = ToNativeUsage(usage);
            var normalized = NormalizeSingleUsage(usage, out var knownSubtotal, out var diagnostics);
            diagnostics.Add(item.MissingPromptIdentity ? "missing_prompt_identity" : "missing_turn_identity");
            var executionId = item.Record.RequestId ?? item.Record.MessageId ?? item.Record.Uuid ?? "unknown-execution";
            var quality = diagnostics.Any(IsInvalidUsageDiagnostic)
                ? MeasurementQuality.Invalid
                : MeasurementQuality.Partial;
            result.Add(new ClaudeUnattributedObservation(
                native,
                normalized,
                new ClaudeMembership(
                    executionId,
                    item.Record.SessionId,
                    ClaudeAttributionStatus.Unattributed,
                    item.MissingPromptIdentity ? "missing-human-prompt-id" : "missing-turn-identity",
                    item.Record.ParentSessionId,
                    item.Record.AgentId),
                quality,
                OrderedDistinct(diagnostics)));
        }

        return result;
    }

    private static IEnumerable<ClaudeUnattributedObservation> BuildUnattributed(
        IEnumerable<CallResult> calls)
    {
        foreach (var call in calls)
        {
            var native = call.NativeUsage!;
            var parsed = new ParsedUsage(
                native.InputTokens,
                native.CacheCreationInputTokens,
                native.CacheReadInputTokens,
                native.OutputTokens,
                native.CacheWrite5m,
                native.CacheWrite1h,
                native.ThinkingTokens);
            var usage = NormalizeSingleUsage(parsed, out _, out var diagnostics);
            diagnostics.AddRange(call.Diagnostics);
            yield return new ClaudeUnattributedObservation(
                native,
                usage,
                call.Membership,
                MeasurementQuality.Partial,
                OrderedDistinct(diagnostics));
        }
    }

    private static List<ClaudeTurnObservation> BuildTurns(
        IReadOnlyList<CallResult> calls,
        AttributionResult attribution,
        ParseState parseState,
        DedupeResult dedupe)
    {
        var sourceIssues = parseState.SourceDiagnostics.Count(IsSourceIntegrityDiagnostic);
        var turns = new List<ClaudeTurnObservation>();

        foreach (var turnGroup in calls
                     .Where(call => call.Context is not null && call.Kind != CallKind.MissingCallIdentity)
                     .GroupBy(call => new TurnKey(call.Context!.RootSessionId, call.Context.RootTurnId))
                     .OrderBy(group => group.Key.RootSessionId, IdComparer)
                     .ThenBy(group => group.Key.RootTurnId, IdComparer))
        {
            var turnCalls = turnGroup.ToArray();
            var diagnostics = turnCalls.SelectMany(call => call.Diagnostics).ToList();
            var memberships = turnCalls.Select(call => call.Membership).ToArray();
            var valid = turnCalls.Where(call => call.Kind == CallKind.Valid).ToArray();
            var invalidTtl = turnCalls.Where(call => call.Kind is CallKind.InvalidTtl or CallKind.InvalidUsage).ToArray();
            var invalidAlias = turnCalls.Where(call => call.Kind == CallKind.InvalidAlias).ToArray();
            var missingCallIdentity = turnCalls.Where(call => call.Kind == CallKind.MissingCallIdentity).ToArray();

            var hasKnownCalls = valid.Length + invalidTtl.Length > 0;
            var native = invalidAlias.Length > 0 && !hasKnownCalls
                ? null
                : AggregateNative(valid.Concat(invalidTtl).Select(call => call.NativeUsage!));

            var normalized = AggregateNormalized(valid, invalidTtl, out var knownSubtotal, out var maxObservedInput);
            int? apiCallCount = invalidAlias.Length > 0 && !hasKnownCalls
                ? null
                : valid.Length + invalidTtl.Length;
            var unknownCount = invalidAlias.Length + invalidTtl.Length + missingCallIdentity.Length + sourceIssues;

            var quality = MeasurementQuality.Observed;
            if (invalidAlias.Length > 0 || invalidTtl.Length > 0)
            {
                quality = MeasurementQuality.Invalid;
            }
            else if (sourceIssues > 0 || missingCallIdentity.Length > 0)
            {
                quality = MeasurementQuality.Partial;
            }

            var executionState = GetExecutionState(parseState.Events, turnGroup.Key);
            var notification = attribution.Notifications.Contains(turnGroup.Key);
            var pendingAgents = GetPendingAgents(parseState.Events, turnGroup.Key, turnCalls);
            if (notification)
            {
                diagnostics.Add(pendingAgents.Length > 0
                    ? "task_notification_identity_unknown"
                    : "task_notification_did_not_open_turn");
                if (pendingAgents.Length > 0)
                {
                    diagnostics.Add("child_source_pending");
                    unknownCount++;
                    quality = MeasurementQuality.Provisional;
                }
            }

            if (dedupe.CopiedSessions.Any(session =>
                    turnCalls.Any(call => string.Equals(call.Context!.OriginSessionId, session, StringComparison.Ordinal))))
            {
                diagnostics.Add("copied_entry_uuid_excluded");
            }

            if (parseState.ForkSessions.Any(session =>
                    turnCalls.Any(call => string.Equals(call.Context!.OriginSessionId, session, StringComparison.Ordinal))))
            {
                diagnostics.Add("fork_parent_unknown");
                if (quality == MeasurementQuality.Observed)
                {
                    quality = MeasurementQuality.Partial;
                }
            }

            diagnostics.AddRange(parseState.SourceDiagnostics.Where(IsSourceIntegrityDiagnostic));
            var latestTimestamp = turnCalls
                .Select(call => call.Timestamp)
                .Where(timestamp => timestamp is not null)
                .Max();

            turns.Add(new ClaudeTurnObservation(
                turnGroup.Key.RootSessionId,
                turnGroup.Key.RootTurnId,
                native,
                normalized,
                quality,
                apiCallCount,
                maxObservedInput,
                knownSubtotal,
                unknownCount,
                memberships,
                executionState,
                OrderedDistinct(diagnostics),
                latestTimestamp?.ToString("O", CultureInfo.InvariantCulture)));
        }

        return turns;
    }

    private static TokenUsage AggregateNormalized(
        IReadOnlyList<CallResult> valid,
        IReadOnlyList<CallResult> invalidTtl,
        out long? knownSubtotal,
        out long? maxObservedInput)
    {
        if (valid.Count == 0 && invalidTtl.Count == 0)
        {
            knownSubtotal = null;
            maxObservedInput = null;
            return new TokenUsage();
        }

        var all = valid.Concat(invalidTtl).ToArray();
        var uncached = SumRequired(all.Select(call => call.NativeUsage!.InputTokens));
        var cacheRead = SumRequired(all.Select(call => call.NativeUsage!.CacheReadInputTokens));
        var output = SumRequired(all.Select(call => call.NativeUsage!.OutputTokens));
        var validCacheWrite = SumRequired(valid.Select(call => call.NativeUsage!.CacheCreationInputTokens));
        var validCacheWrite5m = SumRequired(valid.Select(call => call.NativeUsage!.CacheWrite5m));
        var validCacheWrite1h = SumRequired(valid.Select(call => call.NativeUsage!.CacheWrite1h));

        if (invalidTtl.Count > 0)
        {
            knownSubtotal = checked(uncached + cacheRead + output + validCacheWrite);
            maxObservedInput = null;
            return new TokenUsage(
                InputTotal: null,
                UncachedInput: uncached,
                CacheRead: cacheRead,
                CacheWrite: null,
                CacheWrite5m: SumRequired(invalidTtl.Select(call => call.NativeUsage!.CacheWrite5m)) + validCacheWrite5m,
                CacheWrite1h: SumRequired(invalidTtl.Select(call => call.NativeUsage!.CacheWrite1h)) + validCacheWrite1h,
                Output: output,
                Reasoning: null,
                ProcessedTokens: null,
                NativeTotal: null);
        }

        var cacheWrite = validCacheWrite;
        var inputTotal = checked(uncached + cacheRead + cacheWrite);
        var processed = checked(inputTotal + output);
        knownSubtotal = processed;
        maxObservedInput = valid.Max(call => checked(
            call.NativeUsage!.InputTokens!.Value +
            call.NativeUsage.CacheReadInputTokens!.Value +
            call.NativeUsage.CacheCreationInputTokens!.Value));
        return new TokenUsage(
            InputTotal: inputTotal,
            UncachedInput: uncached,
            CacheRead: cacheRead,
            CacheWrite: cacheWrite,
            CacheWrite5m: validCacheWrite5m,
            CacheWrite1h: validCacheWrite1h,
            Output: output,
            Reasoning: null,
            ProcessedTokens: processed,
            NativeTotal: null);
    }

    private static ClaudeNativeUsage AggregateNative(IEnumerable<ClaudeNativeUsage> usages)
    {
        var values = usages.ToArray();
        return new ClaudeNativeUsage(
            SumComplete(values.Select(item => item.InputTokens)),
            SumComplete(values.Select(item => item.CacheCreationInputTokens)),
            SumComplete(values.Select(item => item.CacheReadInputTokens)),
            SumComplete(values.Select(item => item.OutputTokens)),
            SumComplete(values.Select(item => item.CacheWrite5m)),
            SumComplete(values.Select(item => item.CacheWrite1h)),
            SumComplete(values.Select(item => item.ThinkingTokens)));
    }

    private static TokenUsage NormalizeSingleUsage(
        ParsedUsage usage,
        out long? knownSubtotal,
        out List<string> diagnostics)
    {
        diagnostics = [];
        if (!usage.HasCompleteShape || usage.HasNegativeValue)
        {
            diagnostics.Add("invalid_usage_shape");
            knownSubtotal = SumNullable(
                [usage.InputTokens, usage.CacheReadInputTokens, usage.OutputTokens]);
            return new TokenUsage(
                UncachedInput: usage.InputTokens,
                CacheRead: usage.CacheReadInputTokens,
                Output: usage.OutputTokens);
        }

        var ttl = checked(usage.CacheWrite5m!.Value + usage.CacheWrite1h!.Value);
        if (ttl != usage.CacheCreationInputTokens)
        {
            diagnostics.Add("cache_ttl_total_mismatch");
            knownSubtotal = checked(
                usage.InputTokens!.Value + usage.CacheReadInputTokens!.Value + usage.OutputTokens!.Value);
            return new TokenUsage(
                UncachedInput: usage.InputTokens,
                CacheRead: usage.CacheReadInputTokens,
                CacheWrite5m: usage.CacheWrite5m,
                CacheWrite1h: usage.CacheWrite1h,
                Output: usage.OutputTokens);
        }

        var inputTotal = checked(
            usage.InputTokens!.Value + usage.CacheReadInputTokens!.Value + usage.CacheCreationInputTokens!.Value);
        var processed = checked(inputTotal + usage.OutputTokens!.Value);
        knownSubtotal = processed;
        return new TokenUsage(
            inputTotal,
            usage.InputTokens,
            usage.CacheReadInputTokens,
            usage.CacheCreationInputTokens,
            usage.CacheWrite5m,
            usage.CacheWrite1h,
            usage.OutputTokens,
            null,
            processed,
            null);
    }

    private static ClaudeNativeUsage ToNativeUsage(ParsedUsage usage) => new(
        usage.InputTokens,
        usage.CacheCreationInputTokens,
        usage.CacheReadInputTokens,
        usage.OutputTokens,
        usage.CacheWrite5m,
        usage.CacheWrite1h,
        usage.ThinkingTokens);

    private static ClaudeExecutionState GetExecutionState(
        IReadOnlyList<HookEvent> events,
        TurnKey key)
    {
        if (events.Any(item =>
                string.Equals(item.EventName, "StopFailure", StringComparison.Ordinal) &&
                string.Equals(item.SessionId, key.RootSessionId, StringComparison.Ordinal) &&
                string.Equals(item.PromptId, key.RootTurnId, StringComparison.Ordinal)))
        {
            return ClaudeExecutionState.Failed;
        }

        if (events.Any(item =>
                string.Equals(item.EventName, "Stop", StringComparison.Ordinal) &&
                string.Equals(item.SessionId, key.RootSessionId, StringComparison.Ordinal) &&
                string.Equals(item.PromptId, key.RootTurnId, StringComparison.Ordinal)))
        {
            return ClaudeExecutionState.Completed;
        }

        return ClaudeExecutionState.Unknown;
    }

    private static string[] GetPendingAgents(
        IReadOnlyList<HookEvent> events,
        TurnKey key,
        IReadOnlyList<CallResult> calls)
    {
        var observedAgents = calls
            .Select(call => call.Context!.AgentId)
            .WhereNotNull()
            .ToHashSet(IdComparer);
        return events
            .Where(item =>
                string.Equals(item.EventName, "Stop", StringComparison.Ordinal) &&
                string.Equals(item.SessionId, key.RootSessionId, StringComparison.Ordinal) &&
                string.Equals(item.PromptId, key.RootTurnId, StringComparison.Ordinal))
            .SelectMany(item => item.BackgroundTasks)
            .Where(agent => !observedAgents.Contains(agent))
            .Distinct(IdComparer)
            .ToArray();
    }

    private ClaudeReadResult UnsupportedResult(string diagnostic) => new(
        ClaudeReadStatus.Unsupported,
        Capabilities,
        [],
        [],
        [diagnostic]);

    private static string[] OrderedDistinct(IEnumerable<string> values) => values
        .Distinct(IdComparer)
        .OrderBy(value => value, IdComparer)
        .ToArray();

    private static bool IsSourceIntegrityDiagnostic(string diagnostic) =>
        diagnostic is "malformed_json_line" or "incomplete_tail" or "malformed_json_source";
    private static bool IsGlobalCallDiagnostic(string diagnostic) =>
        diagnostic is "invalid_alias_conflict" or "cache_ttl_total_mismatch" or "invalid_usage_shape";

    private static bool IsGlobalUnattributedDiagnostic(string diagnostic) =>
        diagnostic is "missing_prompt_identity" or "missing_turn_identity" or "missing_call_identity" or
            "invalid_usage_shape" or "cache_ttl_total_mismatch";

    private static bool IsInvalidUsageDiagnostic(string diagnostic) =>
        diagnostic is "invalid_usage_shape" or "cache_ttl_total_mismatch";


    private static long SumRequired(IEnumerable<long?> values)
    {
        long result = 0;
        foreach (var value in values)
        {
            result = checked(result + value!.Value);
        }

        return result;
    }

    private static long? SumNullable(IEnumerable<long?> values)
    {
        long result = 0;
        var found = false;
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            result = checked(result + value.Value);
            found = true;
        }

        return found ? result : null;
    }

    private static long? SumComplete(IEnumerable<long?> values)
    {
        long result = 0;
        var found = false;
        foreach (var value in values)
        {
            if (value is null)
            {
                return null;
            }

            result = checked(result + value.Value);
            found = true;
        }

        return found ? result : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static bool? ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return value.GetBoolean();
    }

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result))
        {
            return null;
        }

        return result;
    }

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        var value = ReadInt64(element, propertyName);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static string[] ReadStrings(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .WhereNotNull()
            .ToArray();
    }

    private sealed record CapturedSource(string Path, string Text);

    private sealed class ParseState
    {
        public List<ParsedRecord> Records { get; } = [];
        public List<HookEvent> Events { get; } = [];
        public HashSet<string> ForkSessions { get; } = new(IdComparer);
        public HashSet<string> DeclaredProviderVersions { get; } = new(IdComparer);
        public List<string> SourceDiagnostics { get; } = [];
        public int RecognizedInvalidRecords { get; set; }
        public int IgnoredSupplementalSources { get; set; }
    }

    private enum RecordKind
    {
        HumanPrompt,
        MissingPrompt,
        TaskNotification,
        NonHuman,
        AssistantUsage,
        Ignored
    }

    private sealed record ParsedRecord(
        string SourcePath,
        int LineNumber,
        DateTimeOffset? Timestamp,
        RecordKind Kind,
        string? Uuid,
        string? ParentUuid,
        string SessionId,
        string? ParentSessionId,
        string? AgentId,
        string? PromptId,
        string? RequestId,
        string? MessageId,
        string? Model,
        ParsedUsage? Usage);

    private sealed record ParsedUsage(
        long? InputTokens,
        long? CacheCreationInputTokens,
        long? CacheReadInputTokens,
        long? OutputTokens,
        long? CacheWrite5m,
        long? CacheWrite1h,
        long? ThinkingTokens)
    {
        public bool HasCompleteShape =>
            InputTokens is not null &&
            CacheCreationInputTokens is not null &&
            CacheReadInputTokens is not null &&
            OutputTokens is not null &&
            CacheWrite5m is not null &&
            CacheWrite1h is not null;

        public bool HasNegativeValue =>
            InputTokens < 0 ||
            CacheCreationInputTokens < 0 ||
            CacheReadInputTokens < 0 ||
            OutputTokens < 0 ||
            CacheWrite5m < 0 ||
            CacheWrite1h < 0 ||
            ThinkingTokens < 0;
    }

    private sealed record PromptContext(
        string RootSessionId,
        string RootTurnId,
        string OriginSessionId,
        string? ParentSessionId,
        string? AgentId);

    private readonly record struct TurnKey(string RootSessionId, string RootTurnId);

    private sealed record UsageObservation(ParsedRecord Record, PromptContext Context);
    private sealed record UnattributedUsage(ParsedRecord Record, bool MissingPromptIdentity);
    private sealed record AttributionResult(
        IReadOnlyList<UsageObservation> UsageObservations,
        IReadOnlyList<UnattributedUsage> UnattributedUsage,
        IReadOnlySet<TurnKey> Notifications);

    private sealed record DedupeResult(
        IReadOnlyList<ParsedRecord> Records,
        IReadOnlySet<string> DuplicateSessions,
        IReadOnlySet<string> CopiedSessions,
        IReadOnlySet<string> DuplicateUuids);

    private sealed record HookEvent(
        string? EventName,
        string? SessionId,
        string? PromptId,
        string? AgentId,
        string? Source,
        IReadOnlyList<string> BackgroundTasks);

    private enum CallKind
    {
        Valid,
        InvalidAlias,
        InvalidTtl,
        InvalidUsage,
        MissingCallIdentity
    }

    private sealed record CallResult(
        CallKind Kind,
        PromptContext? Context,
        ClaudeNativeUsage? NativeUsage,
        ClaudeMembership Membership,
        IReadOnlyList<string> Diagnostics,
        DateTimeOffset? Timestamp)
    {
        public static CallResult Valid(
            PromptContext context,
            string executionId,
            ParsedRecord record,
            ParsedUsage usage,
            IReadOnlyList<UsageObservation> observations,
            IReadOnlyList<string> diagnostics) => new(
                CallKind.Valid,
                context,
                ToNativeUsage(usage),
                MembershipFor(context, executionId, ClaudeAttributionStatus.Attributed, EvidenceFor(context, observations)),
                diagnostics,
                record.Timestamp);

        public static CallResult InvalidAlias(
            PromptContext context,
            string executionId,
            IReadOnlyList<ParsedRecord> records) => new(
                CallKind.InvalidAlias,
                context,
                null,
                MembershipFor(context, executionId, ClaudeAttributionStatus.Invalid, "conflicting-immutable-fields"),
                ["invalid_alias_conflict"],
                records.Select(record => record.Timestamp).Where(value => value is not null).Max());

        public static CallResult InvalidTtl(
            PromptContext context,
            string executionId,
            ParsedRecord record,
            ParsedUsage usage,
            IReadOnlyList<string> diagnostics) => new(
                CallKind.InvalidTtl,
                context,
                ToNativeUsage(usage),
                MembershipFor(context, executionId, ClaudeAttributionStatus.Invalid, "ttl-total-mismatch"),
                diagnostics,
                record.Timestamp);

        public static CallResult InvalidUsage(
            PromptContext context,
            string executionId,
            ParsedRecord record,
            ParsedUsage usage,
            IReadOnlyList<string> diagnostics) => new(
                CallKind.InvalidUsage,
                context,
                ToNativeUsage(usage),
                MembershipFor(context, executionId, ClaudeAttributionStatus.Invalid, "invalid-usage-shape"),
                diagnostics,
                record.Timestamp);

        public static CallResult UnattributedIdentity(UsageObservation observation)
        {
            var executionId = observation.Record.Uuid ?? "unknown-execution";
            return new CallResult(
                CallKind.MissingCallIdentity,
                observation.Context,
                ToNativeUsage(observation.Record.Usage!),
                MembershipFor(
                    observation.Context,
                    executionId,
                    ClaudeAttributionStatus.Unattributed,
                    "missing-call-identity"),
                ["missing_call_identity"],
                observation.Record.Timestamp);
        }

        private static ClaudeMembership MembershipFor(
            PromptContext context,
            string executionId,
            ClaudeAttributionStatus status,
            string evidence) => new(
                executionId,
                context.OriginSessionId,
                status,
                evidence,
                context.ParentSessionId,
                context.AgentId);

        private static string EvidenceFor(
            PromptContext context,
            IReadOnlyList<UsageObservation> observations)
        {
            if (!string.IsNullOrWhiteSpace(context.ParentSessionId))
            {
                return "child-user-prompt-id-and-parent-session";
            }

            if (observations.Any(item => string.IsNullOrWhiteSpace(item.Record.RequestId)))
            {
                return "request-message-alias";
            }

            return "prompt-order-and-parent-chain";
        }
    }

    private sealed class AliasUnion
    {
        private readonly Dictionary<string, string> parent = new(IdComparer);

        public void Add(string value)
        {
            parent.TryAdd(value, value);
        }

        public string Find(string value)
        {
            Add(value);
            var root = value;
            while (!string.Equals(parent[root], root, StringComparison.Ordinal))
            {
                root = parent[root];
            }

            while (!string.Equals(parent[value], value, StringComparison.Ordinal))
            {
                var next = parent[value];
                parent[value] = root;
                value = next;
            }

            return root;
        }

        public void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (!string.Equals(leftRoot, rightRoot, StringComparison.Ordinal))
            {
                parent[rightRoot] = leftRoot;
            }
        }
    }
}

internal static class EnumerableExtensions
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source) where T : class
    {
        foreach (var item in source)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }
}




