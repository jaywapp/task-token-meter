using TaskTokenMeter.Cli;
using TaskTokenMeter.Cli.Selection;
using TaskTokenMeter.Core.Contracts;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

/// <summary>
/// SCOPE-001: automatic session discovery must not mix candidates from a different, known workspace
/// into the list, but must not hide a candidate whose workspace could not be determined either. These
/// tests exercise the real <see cref="AdapterCliRuntime"/> filtering path (not a fake), against
/// synthetic two-workspace fixtures, matching the completion criteria recorded in
/// docs/validation/review.md: 0/1/N interactive candidates, the CI/JSON selector, and an explicit
/// session whose recorded workspace does not match the requested one.
/// </summary>
public sealed class WorkspaceDiscoveryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ttm-workspace-discovery-").FullName;
    private readonly string sourceDir;
    private readonly string workspaceA;
    private readonly string workspaceB;

    public WorkspaceDiscoveryTests()
    {
        sourceDir = Path.Combine(root, "claude-projects");
        Directory.CreateDirectory(sourceDir);
        // Non-existent, deterministic paths: WorkspaceRoot.FindGitRoot only normalizes a path that
        // does not exist on disk (it cannot verify .git boundaries), so both a --workspace argument
        // and a recorded cwd for the same synthetic path always canonicalize identically without the
        // test depending on real Git repositories.
        workspaceA = Path.Combine(root, "workspace-a");
        workspaceB = Path.Combine(root, "workspace-b");
    }

    [Fact]
    public void DiscoverExcludesAKnownDifferentWorkspaceButKeepsAnUnknownOne()
    {
        WriteSession("session-a", workspaceA);
        WriteSession("session-b", workspaceB);
        WriteSession("session-unknown", cwd: null);
        var runtime = CreateRuntime();

        var forA = runtime.Discover("claude", null, workspaceA);
        var forB = runtime.Discover("claude", null, workspaceB);
        var forThirdWorkspace = runtime.Discover("claude", null, Path.Combine(root, "workspace-c"));

        Assert.Equal(["session-a", "session-unknown"], forA.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(["session-b", "session-unknown"], forB.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(["session-unknown"], forThirdWorkspace.Select(c => c.SessionId));
    }

    [Fact]
    public void DiscoverWithoutAWorkspaceArgumentReturnsEveryCandidateUnfiltered()
    {
        WriteSession("session-a", workspaceA);
        WriteSession("session-b", workspaceB);
        var runtime = CreateRuntime();

        var candidates = runtime.Discover("claude", null);

        Assert.Equal(["session-a", "session-b"], candidates.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task InteractiveZeroOneAndManyCandidatesMatchTheWorkspaceFilter()
    {
        WriteSession("only-in-b", workspaceB);
        var runtime = CreateRuntime();

        // Zero: workspace A has no session at all.
        var zero = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, null, InteractionMode.Interactive, Workspace: workspaceA),
            new ScriptedConsole());
        Assert.Equal(SessionSelectionStatus.NoCandidates, zero.Status);
        Assert.Equal(3, zero.ExitCode);

        // One: workspace B has exactly one session, so it is chosen without prompting.
        var oneConsole = new ScriptedConsole();
        var one = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, null, InteractionMode.Interactive, Workspace: workspaceB),
            oneConsole);
        Assert.Equal(SessionSelectionStatus.Selected, one.Status);
        Assert.Equal("only-in-b", one.Candidate!.SessionId);
        Assert.Equal(0, oneConsole.ReadCount);

        // Many: a second session joins workspace B, so the console is asked to pick a number and the
        // chosen index selects the corresponding listed candidate (not just any candidate).
        WriteSession("also-in-b", workspaceB);
        var many = new ScriptedConsole("2");
        var manyResult = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, null, InteractionMode.Interactive, Workspace: workspaceB),
            many);
        Assert.Equal(SessionSelectionStatus.Selected, manyResult.Status);
        Assert.Equal(1, many.ReadCount);
        var listedSecondLine = Assert.Single(many.Output, line => line.StartsWith("2. ", StringComparison.Ordinal));
        Assert.Contains(manyResult.Candidate!.SessionId, listedSecondLine, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void JsonAndCiSelectorsRequireAnExplicitSessionEvenWhenWorkspaceNarrowsToOneCandidate()
    {
        WriteSession("only-in-a", workspaceA);
        var runtime = CreateRuntime();

        var json = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, null, InteractionMode.Json, Workspace: workspaceA),
            new ScriptedConsole());
        var ci = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, null, InteractionMode.Interactive, IsContinuousIntegration: true, Workspace: workspaceA),
            new ScriptedConsole());

        Assert.Equal(SessionSelectionStatus.SelectorRequired, json.Status);
        Assert.Equal(4, json.ExitCode);
        Assert.Equal(SessionSelectionStatus.SelectorRequired, ci.Status);
        Assert.Equal(4, ci.ExitCode);
    }

    [Fact]
    public void ExplicitSessionWithAKnownWorkspaceMismatchIsNotAutoAccepted()
    {
        WriteSession("session-b", workspaceB);
        var runtime = CreateRuntime();

        // SCOPE-001 item 3: an explicit --session whose recorded workspace is known and different
        // from the requested --workspace must not be silently accepted as if nothing were wrong.
        var mismatched = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, "session-b", InteractionMode.NonInteractive, Workspace: workspaceA),
            new ScriptedConsole());
        Assert.Equal(SessionSelectionStatus.CandidateUnavailable, mismatched.Status);
        Assert.Equal(3, mismatched.ExitCode);

        // The same explicit session with its own workspace still resolves normally: the mismatch
        // check is not a blanket block on explicit selection.
        var matched = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, "session-b", InteractionMode.NonInteractive, Workspace: workspaceB),
            new ScriptedConsole());
        Assert.Equal(SessionSelectionStatus.Selected, matched.Status);
        Assert.Equal("session-b", matched.Candidate!.SessionId);
    }

    [Fact]
    public void ExplicitSessionWithoutAWorkspaceArgumentIsUnaffectedByOtherWorkspaces()
    {
        WriteSession("session-a", workspaceA);
        WriteSession("session-b", workspaceB);
        var runtime = CreateRuntime();

        var result = new SessionSelector(runtime).Select(
            new SessionSelectionRequest(ProviderKind.Claude, "session-a", InteractionMode.NonInteractive),
            new ScriptedConsole());

        Assert.Equal(SessionSelectionStatus.Selected, result.Status);
        Assert.Equal("session-a", result.Candidate!.SessionId);
    }

    private AdapterCliRuntime CreateRuntime() => new(
        Path.Combine(root, "data"),
        sourceRoots: new Dictionary<ProviderKind, IReadOnlyList<string>> { [ProviderKind.Claude] = [sourceDir] });

    private void WriteSession(string sessionId, string? cwd)
    {
        var cwdField = cwd is null ? "" : ",\"cwd\":\"" + cwd.Replace("\\", "\\\\") + "\"";
        var lines = new[]
        {
            "{\"type\":\"user\",\"uuid\":\"" + sessionId + "-user\",\"sessionId\":\"" + sessionId + "\"," +
                "\"promptId\":\"" + sessionId + "-prompt\",\"timestamp\":\"2026-02-01T00:00:00Z\"," +
                "\"syntheticEntryKind\":\"human-prompt\",\"message\":{\"role\":\"user\"}" + cwdField + "}",
            "{\"type\":\"assistant\",\"uuid\":\"" + sessionId + "-call\",\"sessionId\":\"" + sessionId + "\"," +
                "\"parentUuid\":\"" + sessionId + "-user\",\"requestId\":\"" + sessionId + "-request\"," +
                "\"timestamp\":\"2026-02-01T00:00:01Z\",\"message\":{\"id\":\"" + sessionId + "-message\"," +
                "\"role\":\"assistant\",\"model\":\"synthetic-claude-model-a\",\"usage\":{" +
                "\"input_tokens\":4,\"cache_creation_input_tokens\":10,\"cache_read_input_tokens\":6," +
                "\"output_tokens\":7,\"cache_creation\":{\"ephemeral_5m_input_tokens\":10,\"ephemeral_1h_input_tokens\":0}}}" +
                cwdField + "}"
        };
        File.WriteAllLines(Path.Combine(sourceDir, sessionId + ".jsonl"), lines);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ScriptedConsole(params string[] inputs) : ISessionSelectionConsole
    {
        private int index;
        public bool IsInputRedirected => false;
        public bool IsOutputRedirected => false;
        public int ReadCount { get; private set; }
        public List<string> Output { get; } = [];
        public void Write(string value) => Output.Add(value);

        public SessionSelectionInput ReadInput()
        {
            ReadCount++;
            return index < inputs.Length
                ? new SessionSelectionInput(SessionSelectionInputKind.Value, inputs[index++])
                : SessionSelectionInput.EndOfFile;
        }
    }
}
