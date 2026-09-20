using TaskTokenMeter.Core.Contracts;
using Xunit;

namespace TaskTokenMeter.UnitTests;

public sealed class ContractJsonTests
{
    [Fact]
    public void RoundTripPreservesSchemaVersionAndNullableTokenFields()
    {
        var snapshot = new MeterSnapshot(
            1,
            "codex",
            "session-1",
            [new TurnSnapshot("codex", "session-1", "turn-1", new TokenUsage(InputTotal: 100), MeasurementQuality.Partial)],
            new StorageRoute(Guid.NewGuid(), StorageMode.Workspace, "C:\\data", 3, "store-1"));

        var roundTrip = ContractJson.Deserialize<MeterSnapshot>(ContractJson.Serialize(snapshot));

        Assert.NotNull(roundTrip);
        Assert.Equal(1, roundTrip!.SchemaVersion);
        Assert.Null(roundTrip.Turns[0].Usage.Output);
        Assert.Null(roundTrip.Turns[0].Usage.ProcessedTokens);
        Assert.Equal(snapshot.StorageRoute, roundTrip.StorageRoute);
    }
}
