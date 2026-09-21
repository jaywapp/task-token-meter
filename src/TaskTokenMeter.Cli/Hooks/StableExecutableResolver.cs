namespace TaskTokenMeter.Cli.Hooks;

/// <summary>
/// Resolves the executable path that Hook entries should reference.
/// The installer keeps every release under <c>&lt;root&gt;\versions\&lt;version&gt;</c> and exposes a
/// version independent shim at <c>&lt;root&gt;\bin\task-token-meter.cmd</c>. Recording the shim keeps
/// provider settings valid after an update removes the previous version directory.
/// </summary>
public static class StableExecutableResolver
{
    public const string OverrideVariable = "TOKEN_METER_HOOK_EXECUTABLE";
    public const string ShimFileName = "task-token-meter.cmd";
    public const string VersionsDirectoryName = "versions";
    public const string BinDirectoryName = "bin";
    private const string WinGetDirectoryName = "WinGet";
    private const string WinGetPackagesDirectoryName = "Packages";
    private const string WinGetLinksDirectoryName = "Links";
    private const string WinGetLinkFileName = "task-token-meter.exe";

    public static string Resolve(
        string processPath,
        Func<string, bool> fileExists,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        var overridden = readEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden) &&
            Path.IsPathFullyQualified(overridden) &&
            fileExists(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return FindShim(processPath, fileExists) ?? Path.GetFullPath(processPath);
    }

    /// <summary>
    /// Returns a version independent path for <paramref name="processPath"/>: the installer shim for a
    /// versioned install, or the WinGet package link for a WinGet portable install.
    /// </summary>
    public static string? FindShim(string processPath, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(fileExists);

        return FindInstallerShim(processPath, fileExists) ?? FindWinGetLink(processPath, fileExists);
    }

    /// <summary>
    /// WinGet installs portable packages into WinGet\Packages\&lt;id&gt;_&lt;hash&gt; and exposes a stable
    /// alias in WinGet\Links, which survives an upgrade that replaces the package directory.
    /// </summary>
    private static string? FindWinGetLink(string processPath, Func<string, bool> fileExists)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(processPath)) ?? string.Empty);
        for (var candidate = directory; candidate?.Parent is not null; candidate = candidate.Parent)
        {
            if (!string.Equals(candidate.Parent.Name, WinGetPackagesDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Parent.Parent is null ||
                !string.Equals(candidate.Parent.Parent.Name, WinGetDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var link = Path.Combine(candidate.Parent.Parent.FullName, WinGetLinksDirectoryName, WinGetLinkFileName);
            return fileExists(link) ? link : null;
        }

        return null;
    }

    private static string? FindInstallerShim(string processPath, Func<string, bool> fileExists)
    {
        var versionDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
        var versionsDirectory = Path.GetDirectoryName(versionDirectory);
        if (versionsDirectory is null ||
            !string.Equals(Path.GetFileName(versionsDirectory), VersionsDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var installRoot = Path.GetDirectoryName(versionsDirectory);
        if (string.IsNullOrEmpty(installRoot))
        {
            return null;
        }

        var shim = Path.Combine(installRoot, BinDirectoryName, ShimFileName);
        return fileExists(shim) ? shim : null;
    }
}
