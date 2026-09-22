using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace TaskTokenMeter.Adapters.Codex;

internal enum CodexLineKind
{
    Ignored,
    SessionMeta,
    TokenUsageRecord,
    TurnContext
}

internal enum CodexRootType
{
    Other,
    SessionMeta,
    EventMessage,
    TokenUsageRecord,
    TurnContext
}

/// <summary>
/// Everything a single rollout line can contribute to a projection.
/// </summary>
/// <remarks>
/// The scanner fills every recognized field during one forward pass because JSON object members are
/// unordered: the root <c>type</c> that decides how a payload should be read may follow the payload
/// itself. Deciding afterwards keeps the previous <see cref="JsonDocument"/> behaviour, which looked
/// the properties up in any order.
/// </remarks>
internal struct CodexScannedLine
{
    public CodexLineKind Kind;
    public bool HasCompleteUsage;

    public string? ObservedAt;
    public string? SessionId;
    public string? RootTurnId;
    public string? ThreadId;
    public string? TurnId;
    public string? ResponseId;
    public string? OriginExecutionId;

    public string? MetaThreadId;
    public string? MetaParentThreadId;
    public string? MetaForkedFromThreadId;

    /// <summary>
    /// Working directory recorded on a session_meta or turn_context line. Discovery metadata only —
    /// never part of the usage calculation.
    /// </summary>
    public string? Cwd;

    public CodexNativeUsage Delta;
    public CodexNativeUsage TurnSnapshot;
    public CodexNativeUsage SessionSnapshot;
}

/// <summary>
/// Reads one Codex rollout line straight from its UTF-8 bytes.
/// </summary>
/// <remarks>
/// Each line is still validated end to end, so a trailing or nested syntax error raises
/// <see cref="JsonException"/> exactly where the previous <see cref="JsonDocument"/> parse did. What
/// the scanner drops is the document database: unsupported records never materialize their payload,
/// and supported ones are read into the projection state directly.
/// </remarks>
internal static class CodexLineScanner
{
    private static readonly CodexNativeUsage EmptyUsage = new();

    public static void Scan(ReadOnlySpan<byte> line, ref CodexScannedLine result)
    {
        result = default;
        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
        if (!reader.Read())
        {
            throw new JsonException("The rollout line does not contain a JSON value.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            ThrowForNonObjectRoot(line);
        }

        var recordType = CodexRootType.Other;
        var payloadIsTokenUsageRecord = false;
        var seenType = false;
        var seenTimestamp = false;
        var seenPayload = false;
        var payloadIsObject = false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!seenType && reader.ValueTextEquals("type"u8))
            {
                seenType = true;
                reader.Read();
                recordType = ReadRootType(ref reader);
            }
            else if (!seenTimestamp && reader.ValueTextEquals("timestamp"u8))
            {
                seenTimestamp = true;
                reader.Read();
                result.ObservedAt = ReadText(ref reader);
            }
            else if (!seenPayload && reader.ValueTextEquals("payload"u8))
            {
                seenPayload = true;
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    payloadIsObject = true;
                    payloadIsTokenUsageRecord = ScanPayload(ref reader, ref result);
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (reader.Read())
        {
            throw new JsonException("The rollout line contains more than one JSON value.");
        }

        if (!payloadIsObject)
        {
            return;
        }

        if (recordType == CodexRootType.SessionMeta)
        {
            result.Kind = CodexLineKind.SessionMeta;
            return;
        }

        // Codex CLI 0.153.4 rollouts write token_usage_record as the root type directly, with the
        // session/turn/usage fields as siblings under `payload` — not nested inside an event_msg
        // envelope. The event_msg-wrapped form below is kept for any source that still uses it.
        if (recordType == CodexRootType.TokenUsageRecord)
        {
            result.Kind = CodexLineKind.TokenUsageRecord;
            return;
        }

        // turn_context records the working directory (and workspace_roots, which matches cwd on
        // every real 0.153.4 sample) once per turn. It carries no usage and only ever sets
        // discovery metadata (SCOPE-001), so a missing or empty line here never affects a turn's
        // token totals.
        if (recordType == CodexRootType.TurnContext)
        {
            result.Kind = CodexLineKind.TurnContext;
            return;
        }

        if (recordType == CodexRootType.EventMessage && payloadIsTokenUsageRecord)
        {
            result.Kind = CodexLineKind.TokenUsageRecord;
        }
    }

    private static bool ScanPayload(ref Utf8JsonReader reader, ref CodexScannedLine result)
    {
        var isTokenUsageRecord = false;
        bool seenType = false, seenId = false, seenSessionId = false, seenRootTurnId = false;
        bool seenThreadId = false, seenTurnId = false, seenResponseId = false, seenOriginId = false;
        bool seenParentThreadId = false, seenForkedFromId = false, seenCwd = false;
        bool seenDelta = false, seenTurnUsage = false, seenSessionUsage = false;
        bool hasDelta = false, hasTurnUsage = false, hasSessionUsage = false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!seenType && reader.ValueTextEquals("type"u8))
            {
                seenType = true;
                reader.Read();
                isTokenUsageRecord = IsText(ref reader, "token_usage_record"u8);
            }
            else if (!seenSessionId && reader.ValueTextEquals("session_id"u8))
            {
                seenSessionId = true;
                reader.Read();
                result.SessionId = ReadText(ref reader);
            }
            else if (!seenRootTurnId && reader.ValueTextEquals("root_turn_id"u8))
            {
                seenRootTurnId = true;
                reader.Read();
                result.RootTurnId = ReadText(ref reader);
            }
            else if (!seenThreadId && reader.ValueTextEquals("thread_id"u8))
            {
                seenThreadId = true;
                reader.Read();
                result.ThreadId = ReadText(ref reader);
            }
            else if (!seenTurnId && reader.ValueTextEquals("turn_id"u8))
            {
                seenTurnId = true;
                reader.Read();
                result.TurnId = ReadText(ref reader);
            }
            else if (!seenResponseId && reader.ValueTextEquals("response_id"u8))
            {
                seenResponseId = true;
                reader.Read();
                result.ResponseId = ReadText(ref reader);
            }
            else if (!seenOriginId && reader.ValueTextEquals("origin_execution_id"u8))
            {
                seenOriginId = true;
                reader.Read();
                result.OriginExecutionId = ReadText(ref reader);
            }
            else if (!seenId && reader.ValueTextEquals("id"u8))
            {
                seenId = true;
                reader.Read();
                result.MetaThreadId = ReadText(ref reader);
            }
            else if (!seenParentThreadId && reader.ValueTextEquals("parent_thread_id"u8))
            {
                seenParentThreadId = true;
                reader.Read();
                result.MetaParentThreadId = ReadText(ref reader);
            }
            else if (!seenForkedFromId && reader.ValueTextEquals("forked_from_id"u8))
            {
                seenForkedFromId = true;
                reader.Read();
                result.MetaForkedFromThreadId = ReadText(ref reader);
            }
            else if (!seenCwd && reader.ValueTextEquals("cwd"u8))
            {
                seenCwd = true;
                reader.Read();
                result.Cwd = ReadText(ref reader);
            }
            else if (!seenDelta && reader.ValueTextEquals("usage"u8))
            {
                seenDelta = true;
                hasDelta = ReadUsage(ref reader, out result.Delta);
            }
            else if (!seenTurnUsage && reader.ValueTextEquals("turn_token_usage"u8))
            {
                seenTurnUsage = true;
                hasTurnUsage = ReadUsage(ref reader, out result.TurnSnapshot);
            }
            else if (!seenSessionUsage && reader.ValueTextEquals("thread_token_usage"u8))
            {
                seenSessionUsage = true;
                hasSessionUsage = ReadUsage(ref reader, out result.SessionSnapshot);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        result.HasCompleteUsage = hasDelta && hasTurnUsage && hasSessionUsage;
        return isTokenUsageRecord;
    }

    /// <summary>
    /// Reads one usage object, mirroring the all-or-nothing contract the JsonElement reader had: an
    /// absent metric stays null, but a metric that is present and is not an Int64 number rejects the
    /// whole object.
    /// </summary>
    private static bool ReadUsage(ref Utf8JsonReader reader, out CodexNativeUsage usage)
    {
        usage = EmptyUsage;
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return false;
        }

        long? input = null, cached = null, output = null, reasoning = null, total = null, cacheWrite = null;
        bool seenInput = false, seenCached = false, seenOutput = false;
        bool seenReasoning = false, seenTotal = false, seenCacheWrite = false;
        var rejected = false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!seenInput && reader.ValueTextEquals("input_tokens"u8))
            {
                seenInput = true;
                rejected |= !ReadTokenCount(ref reader, out input);
            }
            else if (!seenCached && reader.ValueTextEquals("cached_input_tokens"u8))
            {
                seenCached = true;
                rejected |= !ReadTokenCount(ref reader, out cached);
            }
            else if (!seenOutput && reader.ValueTextEquals("output_tokens"u8))
            {
                seenOutput = true;
                rejected |= !ReadTokenCount(ref reader, out output);
            }
            else if (!seenReasoning && reader.ValueTextEquals("reasoning_output_tokens"u8))
            {
                seenReasoning = true;
                rejected |= !ReadTokenCount(ref reader, out reasoning);
            }
            else if (!seenTotal && reader.ValueTextEquals("total_tokens"u8))
            {
                seenTotal = true;
                rejected |= !ReadTokenCount(ref reader, out total);
            }
            else if (!seenCacheWrite && reader.ValueTextEquals("cache_write_input_tokens"u8))
            {
                seenCacheWrite = true;
                rejected |= !ReadTokenCount(ref reader, out cacheWrite);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (rejected)
        {
            return false;
        }

        usage = new CodexNativeUsage(input, cached, output, reasoning, total, cacheWrite);
        return total is not null || input is not null || output is not null;
    }

    private static bool ReadTokenCount(ref Utf8JsonReader reader, out long? value)
    {
        value = null;
        reader.Read();
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
        {
            value = number;
            return true;
        }

        reader.Skip();
        return false;
    }

    /// <summary>
    /// Classifies a record type without materializing it.
    /// </summary>
    /// <remarks>
    /// Only two record types are acted on, and every other value — absent, blank, not a string, or
    /// simply unrecognized — makes the line irrelevant. Comparing against the UTF-8 literals keeps a
    /// rollout from allocating one throwaway string per line just to discard it.
    /// </remarks>
    private static CodexRootType ReadRootType(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return CodexRootType.Other;
        }

        if (reader.ValueTextEquals("event_msg"u8))
        {
            return CodexRootType.EventMessage;
        }

        if (reader.ValueTextEquals("session_meta"u8))
        {
            return CodexRootType.SessionMeta;
        }

        if (reader.ValueTextEquals("token_usage_record"u8))
        {
            return CodexRootType.TokenUsageRecord;
        }

        return reader.ValueTextEquals("turn_context"u8) ? CodexRootType.TurnContext : CodexRootType.Other;
    }

    private static bool IsText(ref Utf8JsonReader reader, ReadOnlySpan<byte> expected)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.ValueTextEquals(expected);
        }

        reader.Skip();
        return false;
    }

    private static string? ReadText(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        var value = reader.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Reproduces what a non-object root used to do: <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/>
    /// rejected a malformed line with <see cref="JsonException"/>, and a well formed non-object root
    /// then failed inside <c>JsonElement.TryGetProperty</c> with <see cref="InvalidOperationException"/>.
    /// Both outcomes are observable, so this rare path replays them instead of guessing.
    /// </summary>
    [DoesNotReturn]
    private static void ThrowForNonObjectRoot(ReadOnlySpan<byte> line)
    {
        using var document = JsonDocument.Parse(line.ToArray());
        document.RootElement.TryGetProperty("type", out _);
        throw new JsonException("The rollout line does not contain a JSON object.");
    }
}
