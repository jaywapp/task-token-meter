using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class CliSmokeTests
{
    [Fact]
    public void TestAssemblyLoads()
    {
        Assert.NotNull(typeof(TaskTokenMeter.Cli.Program).Assembly);
    }
}
