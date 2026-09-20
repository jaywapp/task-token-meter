using TaskTokenMeter.Cli.Selection;
using TaskTokenMeter.Core.Contracts;
using Xunit;

namespace TaskTokenMeter.UnitTests;

public sealed class SessionSelectorTests
{
    [Fact]
    public void SelectReturnsNoCandidatesWhenInteractiveDiscoveryIsEmpty()
    {
        var result = CreateSelector([]).Select(InteractiveRequest(), new FakeConsole());

        Assert.Equal(SessionSelectionStatus.NoCandidates, result.Status);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void SelectAutomaticallySelectsAndRevalidatesASingleCandidate()
    {
        var candidate = Candidate("one");
        var discovery = new FakeDiscovery([candidate], [candidate]);

        var result = new SessionSelector(discovery).Select(InteractiveRequest(), new FakeConsole());

        Assert.Equal(SessionSelectionStatus.Selected, result.Status);
        Assert.Equal(candidate, result.Candidate);
        Assert.Equal(2, discovery.CallCount);
    }

    [Fact]
    public void SelectPromptsUntilAListedNumberIsEntered()
    {
        var first = Candidate("one", "first\u001b[31m label");
        var second = Candidate("two", "second\r\nlabel");
        var console = new FakeConsole("", "0", "bad", "2");
        var discovery = new FakeDiscovery([first, second], [second]);

        var result = new SessionSelector(discovery).Select(InteractiveRequest(), console);

        Assert.Equal(SessionSelectionStatus.Selected, result.Status);
        Assert.Equal(second, result.Candidate);
        Assert.Contains("first[31m label", console.Output);
        Assert.DoesNotContain((char)27, console.Output);
        Assert.DoesNotContain("second\r\nlabel", console.Output);
        Assert.Equal(4, console.ReadCount);
    }

    [Theory]
    [MemberData(nameof(CancellationInputs))]
    public void SelectCancelsForQuitControlCAndEndOfFile(SessionSelectionInput input)
    {
        var first = Candidate("one");
        var second = Candidate("two");
        var console = new FakeConsole(input);

        var result = CreateSelector([first, second]).Select(InteractiveRequest(), console);

        Assert.Equal(SessionSelectionStatus.Cancelled, result.Status);
        Assert.Equal(130, result.ExitCode);
    }

    [Fact]
    public void SelectReturnsCandidateUnavailableWhenCandidateDisappearsBeforeConfirmation()
    {
        var candidate = Candidate("one");
        var discovery = new FakeDiscovery([candidate], []);

        var result = new SessionSelector(discovery).Select(InteractiveRequest(), new FakeConsole());

        Assert.Equal(SessionSelectionStatus.CandidateUnavailable, result.Status);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void SelectPrefersAnExplicitSessionIdOverOtherCandidates()
    {
        var expected = Candidate("wanted");
        var discovery = new FakeDiscovery([Candidate("other"), expected], [expected]);
        var request = new SessionSelectionRequest(null, "wanted", InteractionMode.Interactive);

        var result = new SessionSelector(discovery).Select(request, new FakeConsole());

        Assert.Equal(expected, result.Candidate);
        Assert.Equal(SessionSelectionStatus.Selected, result.Status);
    }

    public static TheoryData<SessionSelectionInput> CancellationInputs =>
    [
        new SessionSelectionInput(SessionSelectionInputKind.Value, "q"),
        SessionSelectionInput.Cancelled,
        SessionSelectionInput.EndOfFile
    ];

    private static SessionSelectionRequest InteractiveRequest() => new(null, null, InteractionMode.Interactive);

    private static SessionSelector CreateSelector(IReadOnlyList<SessionCandidate> candidates) =>
        new(new FakeDiscovery(candidates));

    private static SessionCandidate Candidate(string id, string? label = null) =>
        new(ProviderKind.Codex, id, "workspace", null, label);

    private sealed class FakeDiscovery(params IReadOnlyList<SessionCandidate>[] results) : ISessionDiscovery
    {
        private int resultIndex;

        public int CallCount { get; private set; }

        public IReadOnlyList<SessionCandidate> Discover(string? provider, string? sessionId)
        {
            CallCount++;
            var index = Math.Min(resultIndex++, results.Length - 1);
            return results[index];
        }
    }

    private sealed class FakeConsole : ISessionSelectionConsole
    {
        private readonly Queue<SessionSelectionInput> inputs;

        public FakeConsole() : this(Array.Empty<SessionSelectionInput>()) { }

        public FakeConsole(params string[] inputs) : this(inputs.Select(value => new SessionSelectionInput(SessionSelectionInputKind.Value, value)).ToArray()) { }

        public FakeConsole(params SessionSelectionInput[] inputs) => this.inputs = new Queue<SessionSelectionInput>(inputs);

        public bool IsInputRedirected { get; init; }
        public bool IsOutputRedirected { get; init; }
        public int ReadCount { get; private set; }
        public string Output { get; private set; } = string.Empty;

        public void Write(string value) => Output += value;

        public SessionSelectionInput ReadInput()
        {
            ReadCount++;
            return inputs.Count == 0 ? SessionSelectionInput.EndOfFile : inputs.Dequeue();
        }
    }
}






