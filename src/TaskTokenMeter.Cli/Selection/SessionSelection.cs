using System.Globalization;
using System.Text;
using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Cli.Selection;

public interface ISessionSelectionConsole
{
    bool IsInputRedirected { get; }
    bool IsOutputRedirected { get; }
    void Write(string value);
    SessionSelectionInput ReadInput();
}

public enum SessionSelectionInputKind
{
    Value,
    Cancelled,
    EndOfFile
}

public sealed record SessionSelectionInput(SessionSelectionInputKind Kind, string? Value = null)
{
    public static SessionSelectionInput Cancelled { get; } = new(SessionSelectionInputKind.Cancelled);
    public static SessionSelectionInput EndOfFile { get; } = new(SessionSelectionInputKind.EndOfFile);
}

public enum SessionSelectionStatus
{
    Selected,
    Cancelled,
    SelectorRequired,
    NoCandidates,
    CandidateUnavailable
}

public sealed record SessionSelectionRequest(
    ProviderKind? Provider,
    string? SessionId,
    InteractionMode InteractionMode,
    bool IsContinuousIntegration = false);

public sealed record SessionSelectionResult(SessionSelectionStatus Status, SessionCandidate? Candidate = null)
{
    public int ExitCode => Status switch
    {
        SessionSelectionStatus.Selected => 0,
        SessionSelectionStatus.Cancelled => 130,
        SessionSelectionStatus.SelectorRequired => 4,
        SessionSelectionStatus.NoCandidates or SessionSelectionStatus.CandidateUnavailable => 3,
        _ => 1
    };

    public static SessionSelectionResult Selected(SessionCandidate candidate) => new(SessionSelectionStatus.Selected, candidate);
}

public sealed class SessionSelector(ISessionDiscovery discovery)
{
    public SessionSelectionResult Select(SessionSelectionRequest request, ISessionSelectionConsole console)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(console);

        var nonInteractive = IsNonInteractive(request, console);
        if (nonInteractive && (request.Provider is null || string.IsNullOrWhiteSpace(request.SessionId)))
        {
            return new SessionSelectionResult(SessionSelectionStatus.SelectorRequired);
        }

        var candidates = Discover(request.Provider, request.SessionId);
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            var explicitCandidate = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.SessionId, request.SessionId, StringComparison.Ordinal) &&
                (request.Provider is null || candidate.Provider == request.Provider));

            return explicitCandidate is null
                ? new SessionSelectionResult(SessionSelectionStatus.CandidateUnavailable)
                : Revalidate(explicitCandidate);
        }

        if (candidates.Count == 0)
        {
            return new SessionSelectionResult(SessionSelectionStatus.NoCandidates);
        }

        if (candidates.Count == 1)
        {
            return Revalidate(candidates[0]);
        }

        RenderCandidates(candidates, console);
        while (true)
        {
            console.Write("Select a session number (q to cancel): ");
            var input = console.ReadInput();
            if (input.Kind is SessionSelectionInputKind.Cancelled or SessionSelectionInputKind.EndOfFile ||
                string.Equals(input.Value, "q", StringComparison.OrdinalIgnoreCase))
            {
                return new SessionSelectionResult(SessionSelectionStatus.Cancelled);
            }

            if (!int.TryParse(input.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var selectedNumber) ||
                selectedNumber < 1 || selectedNumber > candidates.Count)
            {
                console.Write("Enter a listed session number or q to cancel." + Environment.NewLine);
                continue;
            }

            return Revalidate(candidates[selectedNumber - 1]);
        }
    }

    private IReadOnlyList<SessionCandidate> Discover(ProviderKind? provider, string? sessionId)
    {
        return discovery.Discover(provider?.ToString().ToLowerInvariant(), sessionId);
    }

    private SessionSelectionResult Revalidate(SessionCandidate candidate)
    {
        var current = Discover(candidate.Provider, candidate.SessionId)
            .FirstOrDefault(item => item.Provider == candidate.Provider &&
                string.Equals(item.SessionId, candidate.SessionId, StringComparison.Ordinal));

        return current is null
            ? new SessionSelectionResult(SessionSelectionStatus.CandidateUnavailable)
            : SessionSelectionResult.Selected(current);
    }

    private static bool IsNonInteractive(SessionSelectionRequest request, ISessionSelectionConsole console)
    {
        return request.InteractionMode is InteractionMode.NonInteractive or InteractionMode.Json or InteractionMode.Hook ||
            request.IsContinuousIntegration || console.IsInputRedirected || console.IsOutputRedirected;
    }

    private static void RenderCandidates(IReadOnlyList<SessionCandidate> candidates, ISessionSelectionConsole console)
    {
        console.Write("Available sessions:" + Environment.NewLine);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var label = string.IsNullOrWhiteSpace(candidate.DisplayLabel)
                ? string.Empty
                : $" ({RemoveControlCharacters(candidate.DisplayLabel)})";
            console.Write($"{index + 1}. {candidate.Provider.ToString().ToLowerInvariant()} {RemoveControlCharacters(candidate.SessionId)}{label}" + Environment.NewLine);
        }
    }

    internal static string RemoveControlCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

