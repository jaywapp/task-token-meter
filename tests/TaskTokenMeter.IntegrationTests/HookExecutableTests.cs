using System.Text.Json;
using TaskTokenMeter.Cli.Hooks;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

/// <summary>
/// Hook entries must survive an installer update. The installer keeps releases under
/// <c>versions\&lt;version&gt;</c> and exposes <c>bin\task-token-meter.cmd</c>, so entries record the shim
/// and <c>hook status</c> reports a stale executable instead of failing open without a signal.
/// </summary>
public sealed class HookExecutableTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ttm-hook-exe-").FullName;

    [Theory]
    [InlineData(HookProvider.Claude, "2.1.278")]
    [InlineData(HookProvider.Codex, "0.153.4")]
    public void InstalledEntriesReferenceTheShimAndStatusReportsStaleExecutable(HookProvider provider, string providerVersion)
    {
        var installRoot = Path.Combine(root, "Programs", "TaskTokenMeter");
        var versioned = Path.Combine(installRoot, "versions", "0.1.0-preview.1", "task-token-meter.exe");
        var shim = Path.Combine(installRoot, "bin", StableExecutableResolver.ShimFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(versioned)!);
        Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        File.WriteAllText(versioned, "executable");
        File.WriteAllText(shim, "@echo off\r\n");
        var settings = Path.Combine(root, "settings-" + provider.ToCliName() + ".json");
        var installer = CreateInstaller(provider);

        var resolved = StableExecutableResolver.Resolve(versioned, File.Exists, static _ => null);
        var installed = installer.Install(settings, resolved, providerVersion);

        Assert.Equal(shim, resolved);
        Assert.Equal(HookInstallationState.Installed, installed.State);
        Assert.Equal(shim, installed.ExecutablePath);
        Assert.True(installed.ExecutableAvailable);
        Assert.All(ManagedCommands(settings), command => Assert.Contains(shim, command, StringComparison.Ordinal));
        Assert.All(ManagedCommands(settings), command => Assert.DoesNotContain("versions", command, StringComparison.Ordinal));

        // An update removes the previous version directory; the shim keeps the entries valid.
        Directory.Delete(Path.GetDirectoryName(versioned)!, recursive: true);
        Assert.True(installer.Status(settings).ExecutableAvailable);

        // Removing the whole install must surface as a stale executable rather than silent inactivity.
        File.Delete(shim);
        var stale = installer.Status(settings);
        Assert.Equal(HookInstallationState.Installed, stale.State);
        Assert.Equal(shim, stale.ExecutablePath);
        Assert.False(stale.ExecutableAvailable);
    }

    [Fact]
    public void StatusWithoutManagedEntriesReportsNoExecutable()
    {
        var settings = Path.Combine(root, "empty.json");
        File.WriteAllText(settings, "{}\n");

        var status = CreateInstaller(HookProvider.Claude).Status(settings);

        Assert.Equal(HookInstallationState.NotInstalled, status.State);
        Assert.Null(status.ExecutablePath);
        Assert.False(status.ExecutableAvailable);
    }

    private static ProviderHookInstaller CreateInstaller(HookProvider provider) => provider == HookProvider.Claude
        ? new ClaudeHookInstaller()
        : new CodexHookInstaller();

    private static string[] ManagedCommands(string settingsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return document.RootElement.GetProperty("hooks")
            .EnumerateObject()
            .SelectMany(static property => property.Value.EnumerateArray())
            .SelectMany(static group => group.GetProperty("hooks").EnumerateArray())
            .Select(static handler => handler.GetProperty("command").GetString()!)
            .Where(static command => command.Contains(ProviderHookInstaller.ManagedMarker, StringComparison.Ordinal))
            .ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
