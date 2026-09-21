using TaskTokenMeter.Cli;
using TaskTokenMeter.Cli.Hooks;
using Xunit;

namespace TaskTokenMeter.UnitTests;

public sealed class VersionAndExecutableTests
{
    private const string InstallRoot = @"C:\Users\person\AppData\Local\Programs\TaskTokenMeter";
    private static readonly string VersionedExecutable = Path.Combine(InstallRoot, "versions", "0.1.0-preview.1", "task-token-meter.exe");
    private static readonly string Shim = Path.Combine(InstallRoot, "bin", "task-token-meter.cmd");

    [Fact]
    public void InformationalVersionIsAvailableAndMatchesFileVersionCore()
    {
        Assert.False(string.IsNullOrWhiteSpace(VersionInfo.Informational));
        Assert.DoesNotContain("+", VersionInfo.Informational, StringComparison.Ordinal);

        var core = VersionInfo.Informational.Split('-')[0];
        Assert.StartsWith(core + ".", VersionInfo.File, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePrefersInstallerShimOverVersionedExecutable()
    {
        var resolved = StableExecutableResolver.Resolve(
            VersionedExecutable,
            path => string.Equals(path, Shim, StringComparison.OrdinalIgnoreCase),
            static _ => null);

        Assert.Equal(Shim, resolved);
    }

    [Fact]
    public void ResolveFallsBackToProcessPathWhenShimIsMissing()
    {
        var resolved = StableExecutableResolver.Resolve(VersionedExecutable, static _ => false, static _ => null);

        Assert.Equal(VersionedExecutable, resolved);
    }

    [Fact]
    public void ResolveKeepsProcessPathForNonVersionedLayouts()
    {
        var repositoryBuild = Path.Combine(@"D:\repo\src\TaskTokenMeter.Cli\bin\Release\net10.0", "task-token-meter.exe");

        var resolved = StableExecutableResolver.Resolve(repositoryBuild, static _ => true, static _ => null);

        Assert.Equal(repositoryBuild, resolved);
    }

    [Fact]
    public void ResolveHonoursAbsoluteOverrideVariable()
    {
        var overridePath = Path.Combine(@"D:\tools", "task-token-meter.cmd");

        var resolved = StableExecutableResolver.Resolve(
            VersionedExecutable,
            path => string.Equals(path, overridePath, StringComparison.OrdinalIgnoreCase) || string.Equals(path, Shim, StringComparison.OrdinalIgnoreCase),
            name => string.Equals(name, StableExecutableResolver.OverrideVariable, StringComparison.Ordinal) ? overridePath : null);

        Assert.Equal(overridePath, resolved);
    }

    [Fact]
    public void ResolvePrefersTheWinGetLinkForPortableInstalls()
    {
        var winGetRoot = @"C:\Users\person\AppData\Local\Microsoft\WinGet";
        var packaged = Path.Combine(winGetRoot, "Packages", "Jaywapp.TaskTokenMeter_Microsoft.Winget.Source_8wekyb3d8bbwe", "task-token-meter.exe");
        var link = Path.Combine(winGetRoot, "Links", "task-token-meter.exe");

        var resolved = StableExecutableResolver.Resolve(
            packaged,
            path => string.Equals(path, link, StringComparison.OrdinalIgnoreCase),
            static _ => null);

        Assert.Equal(link, resolved);
    }

    [Fact]
    public void ResolveKeepsThePackagedPathWhenTheWinGetLinkIsMissing()
    {
        var packaged = Path.Combine(@"C:\Users\person\AppData\Local\Microsoft\WinGet\Packages\Jaywapp.TaskTokenMeter_x", "task-token-meter.exe");

        var resolved = StableExecutableResolver.Resolve(packaged, static _ => false, static _ => null);

        Assert.Equal(packaged, resolved);
    }

    [Theory]
    [InlineData("relative\\task-token-meter.cmd")]
    [InlineData("")]
    public void ResolveIgnoresUnusableOverrideVariable(string overrideValue)
    {
        var resolved = StableExecutableResolver.Resolve(
            VersionedExecutable,
            path => string.Equals(path, Shim, StringComparison.OrdinalIgnoreCase),
            name => string.Equals(name, StableExecutableResolver.OverrideVariable, StringComparison.Ordinal) ? overrideValue : null);

        Assert.Equal(Shim, resolved);
    }
}
