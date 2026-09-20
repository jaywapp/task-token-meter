using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Core.Identity;

public sealed record SessionLineage(
    ProviderKind Provider,
    string SessionId,
    string? RootSessionId = null,
    string? ParentSessionId = null,
    string? ForkedFromSessionId = null);

public enum RootSessionResolutionStatus
{
    Resolved,
    Unresolved,
    Invalid
}

public sealed record RootSessionResolution(
    RootSessionResolutionStatus Status,
    string? RootSessionId,
    string? Evidence,
    IReadOnlyList<string> Diagnostics)
{
    public bool IsResolved => Status == RootSessionResolutionStatus.Resolved;
}

public static class SessionLineageResolver
{
    public static RootSessionResolution Resolve(
        ProviderKind provider,
        string sessionId,
        IEnumerable<SessionLineage> lineage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(lineage);

        var providerNodes = lineage
            .Where(node => node.Provider == provider)
            .GroupBy(node => node.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        if (!providerNodes.TryGetValue(sessionId, out var startingNodes))
        {
            return new RootSessionResolution(
                RootSessionResolutionStatus.Unresolved,
                null,
                null,
                ["session_lineage_missing"]);
        }

        var normalizedStart = NormalizeNode(startingNodes);
        if (normalizedStart is null)
        {
            return Invalid("session_lineage_conflict");
        }

        if (!string.IsNullOrWhiteSpace(normalizedStart.RootSessionId))
        {
            return new RootSessionResolution(
                RootSessionResolutionStatus.Resolved,
                normalizedStart.RootSessionId,
                "explicit-root-session-id",
                []);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = normalizedStart;
        while (true)
        {
            if (!visited.Add(current.SessionId))
            {
                return Invalid("session_lineage_cycle");
            }

            if (string.IsNullOrWhiteSpace(current.ParentSessionId))
            {
                return new RootSessionResolution(
                    RootSessionResolutionStatus.Resolved,
                    current.SessionId,
                    "parent-session-lineage",
                    []);
            }

            if (!providerNodes.TryGetValue(current.ParentSessionId, out var parentNodes))
            {
                return new RootSessionResolution(
                    RootSessionResolutionStatus.Unresolved,
                    null,
                    null,
                    ["parent_session_lineage_missing"]);
            }

            var parent = NormalizeNode(parentNodes);
            if (parent is null)
            {
                return Invalid("session_lineage_conflict");
            }

            if (!string.IsNullOrWhiteSpace(parent.RootSessionId))
            {
                return new RootSessionResolution(
                    RootSessionResolutionStatus.Resolved,
                    parent.RootSessionId,
                    "parent-session-lineage",
                    []);
            }

            current = parent;
        }
    }

    private static SessionLineage? NormalizeNode(SessionLineage[] nodes)
    {
        var first = nodes[0];
        return nodes.All(node =>
                string.Equals(node.RootSessionId, first.RootSessionId, StringComparison.Ordinal) &&
                string.Equals(node.ParentSessionId, first.ParentSessionId, StringComparison.Ordinal) &&
                string.Equals(node.ForkedFromSessionId, first.ForkedFromSessionId, StringComparison.Ordinal))
            ? first
            : null;
    }

    private static RootSessionResolution Invalid(string diagnostic) =>
        new(RootSessionResolutionStatus.Invalid, null, null, [diagnostic]);
}
