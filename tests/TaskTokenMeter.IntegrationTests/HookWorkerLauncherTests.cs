using TaskTokenMeter.Cli.Hooks;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

/// <summary>
/// The worker must not inherit the Hook process's standard streams. An inherited stdout keeps the
/// provider's pipe open until the worker exits, so the provider would wait for the aggregation, and any
/// worker output would be mixed into the Hook response.
/// </summary>
public sealed class HookWorkerLauncherTests
{
    [Fact]
    public void WorkerStartInfoRedirectsEveryStandardStream()
    {
        var request = new HookWorkerRequest(
            new HookInvocationContext(HookProvider.Codex, "Stop", "session", "turn", @"C:\workspace", [@"C:\sources\rollout.jsonl"]),
            @"C:\data\diagnostics.jsonl",
            TimeSpan.FromSeconds(30));

        var startInfo = ProcessHookWorkerLauncher.CreateStartInfo(@"C:\tools\task-token-meter.exe", request);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(["hook", "worker", "--provider", "codex"], startInfo.ArgumentList.Take(4));
    }
}
