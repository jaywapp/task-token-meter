namespace TaskTokenMeter.Core.Identity;

/// <summary>
/// Canonicalizes a directory into the workspace identity used to match a provider log's recorded
/// working directory against the CLI's requested workspace: the nearest ancestor that contains a
/// `.git` entry, or the directory itself when none is found. This is the same walk the CLI already
/// does for the default (no <c>--workspace</c>) case, so running a session from a subdirectory of a
/// project does not make its logs look like a different workspace.
///
/// This only touches the filesystem once per distinct recorded directory per Turn, never per log
/// line, so it stays off the hot parsing path.
/// </summary>
public static class WorkspaceRoot
{
    /// <summary>
    /// Finds the nearest Git root starting at <paramref name="path"/>. Recorded log directories can
    /// point at a path that no longer exists (renamed or deleted since the session ran); in that case
    /// the walk cannot verify `.git` boundaries, so the path is only normalized, not walked.
    /// </summary>
    public static string FindGitRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            return Path.TrimEndingDirectorySeparator(full);
        }

        for (var candidate = new DirectoryInfo(full); candidate is not null; candidate = candidate.Parent)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, ".git")) ||
                File.Exists(Path.Combine(candidate.FullName, ".git")))
            {
                return Path.TrimEndingDirectorySeparator(candidate.FullName);
            }
        }

        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// Same as <see cref="FindGitRoot"/>, but returns null for blank or malformed input instead of
    /// throwing. Used for provider-recorded paths, which are untrusted external data.
    /// </summary>
    public static string? TryFindGitRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return FindGitRoot(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static bool Matches(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
