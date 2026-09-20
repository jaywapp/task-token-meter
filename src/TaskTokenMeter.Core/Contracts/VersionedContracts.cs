using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskTokenMeter.Core.Contracts;

public sealed record SessionCandidate(
    ProviderKind Provider,
    string SessionId,
    string? WorkspaceId,
    DateTimeOffset? LastObservedAt,
    string? DisplayLabel = null);

public sealed record TurnSnapshot(
    string Provider,
    string SessionId,
    string TurnId,
    TokenUsage Usage,
    MeasurementQuality Quality,
    int? ApiCallCount = null,
    long? MaxObservedInput = null,
    string? ObservedAt = null);

public sealed record MeterSnapshot(
    int SchemaVersion,
    string Provider,
    string SessionId,
    IReadOnlyList<TurnSnapshot> Turns,
    StorageRoute? StorageRoute = null);

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
