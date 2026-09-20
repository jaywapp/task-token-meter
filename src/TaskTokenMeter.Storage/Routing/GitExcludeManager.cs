using TaskTokenMeter.Storage.Migration;
using System.Diagnostics;

namespace TaskTokenMeter.Storage.Routing;

public sealed record GitExcludePlan(
    bool IsGitWorkspace,
    string? ExcludePath,
    bool RequiresChange,
    bool HasTrackedStorageFiles);

public interface IGitExcludeManager
{
    Task<GitExcludePlan> PreviewAsync(string workspaceRoot, CancellationToken cancellationToken = default);
    Task<GitExcludePlan> EnsureExcludedAsync(string workspaceRoot, CancellationToken cancellationToken = default);
}

public interface IGitCommandRunner
{
    Task<IReadOnlyList<string>> ListTrackedStorageFilesAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default);
}

public sealed class GitCommandRunner : IGitCommandRunner
{
    public async Task<IReadOnlyList<string>> ListTrackedStorageFilesAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspaceRoot
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(".token-meter");
        startInfo.ArgumentList.Add(".token-meter/**");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new IOException("Unable to inspect tracked workspace storage files.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException("Unable to inspect tracked workspace storage files.");
        }

        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public sealed class GitExcludeManager : IGitExcludeManager
{
    public const string StorageExcludeEntry = "/.token-meter/";
    private readonly IStorageFileSystem fileSystem;
    private readonly IGitCommandRunner commandRunner;

    public GitExcludeManager(
        IStorageFileSystem? fileSystem = null,
        IGitCommandRunner? commandRunner = null)
    {
        this.fileSystem = fileSystem ?? new PhysicalStorageFileSystem();
        this.commandRunner = commandRunner ?? new GitCommandRunner();
    }

    public async Task<GitExcludePlan> PreviewAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var canonicalWorkspace = fileSystem.GetFullPath(workspaceRoot);
        var gitDirectory = ResolveGitDirectory(canonicalWorkspace);
        if (gitDirectory is null)
        {
            return new GitExcludePlan(false, null, false, false);
        }

        var tracked = await commandRunner.ListTrackedStorageFilesAsync(canonicalWorkspace, cancellationToken)
            .ConfigureAwait(false);
        var excludePath = Path.Combine(gitDirectory, "info", "exclude");
        var contents = fileSystem.FileExists(excludePath) ? fileSystem.ReadAllText(excludePath) : string.Empty;
        var hasEntry = contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static line => string.Equals(line.Trim(), StorageExcludeEntry, StringComparison.Ordinal));
        return new GitExcludePlan(true, excludePath, !hasEntry, tracked.Count > 0);
    }

    public async Task<GitExcludePlan> EnsureExcludedAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var plan = await PreviewAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        if (!plan.IsGitWorkspace || !plan.RequiresChange)
        {
            if (plan.HasTrackedStorageFiles)
            {
                throw new StorageMigrationConflictException("Workspace storage files are already tracked by Git.");
            }

            return plan;
        }

        if (plan.HasTrackedStorageFiles)
        {
            throw new StorageMigrationConflictException("Workspace storage files are already tracked by Git.");
        }

        var excludePath = plan.ExcludePath
            ?? throw new InvalidOperationException("A Git workspace must have an exclude path.");
        var existing = fileSystem.FileExists(excludePath) ? fileSystem.ReadAllText(excludePath) : string.Empty;
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var updated = existing;
        if (updated.Length > 0 && !updated.EndsWith('\n') && !updated.EndsWith('\r'))
        {
            updated += newline;
        }

        updated += StorageExcludeEntry + newline;
        var directory = Path.GetDirectoryName(excludePath)
            ?? throw new InvalidOperationException("The Git exclude path has no parent directory.");
        fileSystem.CreateDirectory(directory);
        fileSystem.WriteAllText(excludePath, updated);
        return plan;
    }

    private string? ResolveGitDirectory(string workspaceRoot)
    {
        var marker = Path.Combine(workspaceRoot, ".git");
        if (fileSystem.DirectoryExists(marker))
        {
            return fileSystem.GetFullPath(marker);
        }

        if (!fileSystem.FileExists(marker))
        {
            return null;
        }

        var firstLine = fileSystem.ReadAllText(marker)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        const string prefix = "gitdir:";
        if (firstLine is null || !firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The workspace .git file is invalid.");
        }

        var path = firstLine[prefix.Length..].Trim();
        return fileSystem.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));
    }
}
