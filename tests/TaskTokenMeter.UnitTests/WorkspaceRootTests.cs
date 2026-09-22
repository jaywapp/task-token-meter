using TaskTokenMeter.Core.Identity;
using Xunit;

namespace TaskTokenMeter.UnitTests;

/// <summary>
/// SCOPE-001: canonicalizing a provider log's recorded cwd and the CLI's requested --workspace the
/// same way is what lets a session run from a subdirectory of a project without looking like a
/// different workspace, while still telling genuinely different projects apart.
/// </summary>
public sealed class WorkspaceRootTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ttm-workspace-root-").FullName;

    [Fact]
    public void FindGitRootReturnsTheDirectoryItselfWhenItHasAGitFolder()
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));

        Assert.Equal(project, WorkspaceRoot.FindGitRoot(project));
    }

    [Fact]
    public void FindGitRootRecognizesASubmoduleGitFile()
    {
        // A submodule's .git is a file (`gitdir: ...`), not a directory. The Claude/Codex source tree
        // this CLI ships from is itself a submodule, so this is the common case, not an edge case.
        var project = Path.Combine(root, "submodule");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".git"), "gitdir: ../.git/modules/submodule\n");

        Assert.Equal(project, WorkspaceRoot.FindGitRoot(project));
    }

    [Fact]
    public void FindGitRootWalksUpFromASubdirectoryToTheProjectRoot()
    {
        var project = Path.Combine(root, "project");
        var nested = Path.Combine(project, "src", "deep", "nested");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Directory.CreateDirectory(nested);

        Assert.Equal(project, WorkspaceRoot.FindGitRoot(nested));
    }

    [Fact]
    public void FindGitRootReturnsTheNormalizedPathWhenNoAncestorHasGit()
    {
        var loose = Path.Combine(root, "no-git-here");
        Directory.CreateDirectory(loose);

        // root (the temp directory itself) has no .git either, and neither do any of its parents in
        // a CI/dev sandbox, so the walk exhausts every ancestor and falls back to the input path.
        Assert.Equal(Path.TrimEndingDirectorySeparator(loose), WorkspaceRoot.FindGitRoot(loose));
    }

    [Fact]
    public void FindGitRootNormalizesOnlyWhenThePathNoLongerExists()
    {
        // A provider log can record a directory that was later renamed or deleted. The walk cannot
        // verify .git boundaries against a path that is not there, so it only normalizes.
        var gone = Path.Combine(root, "deleted", "project");

        var result = WorkspaceRoot.FindGitRoot(gone);

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gone)), result);
    }

    [Fact]
    public void TryFindGitRootReturnsNullForBlankInput()
    {
        Assert.Null(WorkspaceRoot.TryFindGitRoot(null));
        Assert.Null(WorkspaceRoot.TryFindGitRoot(""));
        Assert.Null(WorkspaceRoot.TryFindGitRoot("   "));
    }

    [Fact]
    public void MatchesIsCaseInsensitiveAndRejectsUnknownOnEitherSide()
    {
        var project = Path.Combine(root, "Project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var upper = WorkspaceRoot.FindGitRoot(project.ToUpperInvariant());
        var lower = WorkspaceRoot.FindGitRoot(project.ToLowerInvariant());

        Assert.True(WorkspaceRoot.Matches(upper, lower));
        Assert.False(WorkspaceRoot.Matches(null, lower));
        Assert.False(WorkspaceRoot.Matches(upper, null));
        Assert.False(WorkspaceRoot.Matches(null, null));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
