using TaskTokenMeter.Cli.Selection;
using TaskTokenMeter.Core.Contracts;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class TerminalSelectionTests
{
    [Theory]
    [InlineData(InteractionMode.Json, false, false, false)]
    [InlineData(InteractionMode.NonInteractive, false, false, false)]
    [InlineData(InteractionMode.Interactive, true, false, false)]
    [InlineData(InteractionMode.Interactive, false, true, false)]
    [InlineData(InteractionMode.Interactive, false, false, true)]
    public void SelectRequiresProviderAndSessionWithoutPromptInNonInteractiveEnvironments(
        InteractionMode mode,
        bool inputRedirected,
        bool outputRedirected,
        bool continuousIntegration)
    {
        var console = new TerminalConsole
        {
            IsInputRedirected = inputRedirected,
            IsOutputRedirected = outputRedirected
        };
        var selector = new SessionSelector(new StaticDiscovery(Candidate("only")));
        var request = new SessionSelectionRequest(null, null, mode, continuousIntegration);

        var result = selector.Select(request, console);

        Assert.Equal(SessionSelectionStatus.SelectorRequired, result.Status);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, console.ReadCount);
        Assert.Empty(console.Output);
    }

    [Fact]
    public void SelectUsesValidatedHookContextWithoutPromptOrConsoleOutput()
    {
        var candidate = Candidate("hook-session");
        var console = new TerminalConsole();
        var result = new SessionSelector(new StaticDiscovery(candidate)).Select(
            new SessionSelectionRequest(ProviderKind.Codex, candidate.SessionId, InteractionMode.Hook),
            console);

        Assert.Equal(SessionSelectionStatus.Selected, result.Status);
        Assert.Equal(candidate, result.Candidate);
        Assert.Equal(0, console.ReadCount);
        Assert.Empty(console.Output);
    }

    [Fact]
    public void SelectCancelsWithoutRequestingAnyWrite()
    {
        var console = new TerminalConsole(SessionSelectionInput.Cancelled);
        var selector = new SessionSelector(new StaticDiscovery(Candidate("one"), Candidate("two")));

        var result = selector.Select(new SessionSelectionRequest(null, null, InteractionMode.Interactive), console);

        Assert.Equal(SessionSelectionStatus.Cancelled, result.Status);
        Assert.Equal(130, result.ExitCode);
    }

    private static SessionCandidate Candidate(string id) => new(ProviderKind.Codex, id, null, null);

    private sealed class StaticDiscovery(params SessionCandidate[] candidates) : ISessionDiscovery
    {
        public IReadOnlyList<SessionCandidate> Discover(string? provider, string? sessionId, string? workspace = null) => candidates;
    }

    private sealed class TerminalConsole(params SessionSelectionInput[] inputs) : ISessionSelectionConsole
    {
        private readonly Queue<SessionSelectionInput> inputs = new(inputs);

        public bool IsInputRedirected { get; init; }
        public bool IsOutputRedirected { get; init; }
        public int ReadCount { get; private set; }
        public List<string> Output { get; } = [];

        public void Write(string value) => Output.Add(value);

        public SessionSelectionInput ReadInput()
        {
            ReadCount++;
            return inputs.Count == 0 ? SessionSelectionInput.EndOfFile : inputs.Dequeue();
        }
    }
}
