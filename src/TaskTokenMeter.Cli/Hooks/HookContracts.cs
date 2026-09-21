using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Cli.Hooks;

public enum HookProvider
{
    Claude,
    Codex
}

public enum HookInstallationState
{
    Installed,
    NotInstalled,
    PartiallyInstalled
}

public sealed record HookInstallationStatus(
    HookProvider Provider,
    HookInstallationState State,
    string SettingsPath,
    int InstalledEntryCount,
    int ExpectedEntryCount,
    string? BackupPath,
    string? ExecutablePath = null,
    bool ExecutableAvailable = false);

public sealed record HookInvocationContext(
    HookProvider Provider,
    string EventName,
    string SessionId,
    string? TurnId,
    string WorkspacePath,
    IReadOnlyList<string> TranscriptPaths);

public sealed record HookWorkerRequest(
    HookInvocationContext Context,
    string DiagnosticsPath,
    TimeSpan Timeout);

public static class HookProviderNames
{
    public static HookProvider Parse(string value) => value.ToLowerInvariant() switch
    {
        "claude" or "claude-code" => HookProvider.Claude,
        "codex" => HookProvider.Codex,
        _ => throw new ArgumentException("Unsupported Hook provider.")
    };

    public static string ToCliName(this HookProvider provider) => provider switch
    {
        HookProvider.Claude => "claude",
        HookProvider.Codex => "codex",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static ProviderKind ToProviderKind(this HookProvider provider) => provider switch
    {
        HookProvider.Claude => ProviderKind.Claude,
        HookProvider.Codex => ProviderKind.Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
