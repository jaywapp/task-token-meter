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
    /// Returns the installer shim when <paramref name="processPath"/> runs from a versioned install layout.
    /// </summary>
    public static string? FindShim(string processPath, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(fileExists);

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
