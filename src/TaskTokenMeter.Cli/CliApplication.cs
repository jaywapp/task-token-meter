using System.Globalization;
using System.Text.Json;
using TaskTokenMeter.Cli.Hooks;
using TaskTokenMeter.Cli.Selection;
using TaskTokenMeter.Core.Attribution;
using TaskTokenMeter.Core.Contracts;
using TaskTokenMeter.Core.Projection;
using TaskTokenMeter.Storage.Migration;

namespace TaskTokenMeter.Cli;

public interface ICliConsole : ISessionSelectionConsole
{
    void WriteError(string value);
    bool IsColorEnabled { get; }
}

public interface ICliRuntime : ISessionDiscovery
{
    Task<IReadOnlyList<TurnProjection>> ReadTurnsAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken);
    Task<IReadOnlyList<TurnProjection>> SyncAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken);
    Task<IReadOnlyList<TurnProjection>> RebuildAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken);
    Task<StorageStatus> GetStorageStatusAsync(string workspace, CancellationToken cancellationToken);
    Task<StorageMigrationResult> MigrateStorageAsync(string workspace, StorageMode destination, bool dryRun, CancellationToken cancellationToken);
}

public sealed class EmptyCliRuntime : ICliRuntime
{
    public IReadOnlyList<SessionCandidate> Discover(string? provider, string? sessionId) => [];
    public Task<IReadOnlyList<TurnProjection>> ReadTurnsAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TurnProjection>>([]);
    public Task<IReadOnlyList<TurnProjection>> SyncAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TurnProjection>>([]);
    public Task<IReadOnlyList<TurnProjection>> RebuildAsync(SessionCandidate session, string workspace, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TurnProjection>>([]);
    public Task<StorageStatus> GetStorageStatusAsync(string workspace, CancellationToken cancellationToken) => throw new InvalidOperationException("Storage has not been configured.");
    public Task<StorageMigrationResult> MigrateStorageAsync(string workspace, StorageMode destination, bool dryRun, CancellationToken cancellationToken) => throw new InvalidOperationException("Storage has not been configured.");
}

public sealed class SystemCliConsole : ICliConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    public bool IsColorEnabled => !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    public void Write(string value) => Console.Out.Write(value);
    public void WriteError(string value) => Console.Error.Write(value);
    public SessionSelectionInput ReadInput()
    {
        try
        {
            var value = Console.ReadLine();
            return value is null ? SessionSelectionInput.EndOfFile : new SessionSelectionInput(SessionSelectionInputKind.Value, value);
        }
        catch (OperationCanceledException) { return SessionSelectionInput.Cancelled; }
    }
}

public static class CliApplication
{
    private const int Success = 0;
    private const int GeneralError = 1;
    private const int InvalidArguments = 2;
    private const int NoData = 3;
    private const int UnsupportedSchema = 5;
    private const int StrictMeasurement = 6;
    private const int StorageError = 7;

    public static async Task<int> RunAsync(string[] args, ICliRuntime runtime, ICliConsole console, CancellationToken cancellationToken = default)
    {
        if (args.Length > 0 && string.Equals(args[0], "hook", StringComparison.Ordinal))
        {
            return await HookCliApplication.RunAsync(
                args[1..],
                console,
                Console.In,
                new CliConsoleTextWriter(console),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var command = CliCommand.Parse(args);
            if (command.Kind == CliCommandKind.Help) { RenderHelp(console); return Success; }
            if (command.Kind is CliCommandKind.StorageStatus or CliCommandKind.StorageMigrate)
                return await RunStorageAsync(command, runtime, console, cancellationToken).ConfigureAwait(false);

            var selection = new SessionSelector(runtime).Select(new SessionSelectionRequest(command.Provider, command.SessionId, command.InteractionMode, command.IsContinuousIntegration), console);
            if (selection.Status != SessionSelectionStatus.Selected) return RenderSelectionFailure(command, selection, console);
            var turns = command.Kind switch
            {
                CliCommandKind.Sync => await runtime.SyncAsync(selection.Candidate!, command.Workspace, cancellationToken).ConfigureAwait(false),
                CliCommandKind.Rebuild => await runtime.RebuildAsync(selection.Candidate!, command.Workspace, cancellationToken).ConfigureAwait(false),
                _ => await runtime.ReadTurnsAsync(selection.Candidate!, command.Workspace, cancellationToken).ConfigureAwait(false)
            };
            if (turns.Count == 0) return RenderError(command, "no_data", NoData, console);
            var selected = SelectTurns(command.Kind, turns);
            RenderTurns(command, selected, console);
            return command.Strict && selected.Any(static item => item.MeasurementQuality != MeasurementQuality.Observed) ? StrictMeasurement : Success;
        }
        catch (OperationCanceledException) { return 130; }
        catch (UnsupportedSourceException)
        {
            return RenderError(CliCommand.TryParse(args), "unsupported_schema", UnsupportedSchema, console);
        }
        catch (ArgumentException exception)
        {
            console.WriteError("token-meter: " + SafeCode(exception) + Environment.NewLine);
            return InvalidArguments;
        }
        catch (Exception exception)
        {
            console.WriteError("token-meter: " + SafeCode(exception) + Environment.NewLine);
            return GeneralError;
        }
    }

    private static async Task<int> RunStorageAsync(CliCommand command, ICliRuntime runtime, ICliConsole console, CancellationToken cancellationToken)
    {
        try
        {
            if (command.Kind == CliCommandKind.StorageStatus)
            {
                var status = await runtime.GetStorageStatusAsync(command.Workspace, cancellationToken).ConfigureAwait(false);
                RenderStorage(command, new { schemaVersion = 1, mode = status.ActiveRoute.Mode, databasePath = status.DatabasePath, records = status.ActiveRecords }, console);
                return Success;
            }
            if (command.DestinationMode is null) return RenderError(command, "destination_required", GeneralError, console);
            var result = await runtime.MigrateStorageAsync(command.Workspace, command.DestinationMode.Value, command.DryRun, cancellationToken).ConfigureAwait(false);
            RenderStorage(command, new { schemaVersion = 1, outcome = result.Outcome, mode = result.ActiveRoute.Mode, phase = result.Phase }, console);
            return Success;
        }
        catch (Exception exception)
        {
            console.WriteError("token-meter: " + SafeCode(exception) + Environment.NewLine);
            return StorageError;
        }
    }

    private static int RenderSelectionFailure(CliCommand command, SessionSelectionResult result, ICliConsole console)
    {
        var code = result.Status switch
        {
            SessionSelectionStatus.SelectorRequired => "selector_required",
            SessionSelectionStatus.NoCandidates or SessionSelectionStatus.CandidateUnavailable => "no_data",
            SessionSelectionStatus.Cancelled => "cancelled",
            _ => "selection_failed"
        };
        return RenderError(command, code, result.ExitCode, console);
    }

    private static int RenderError(CliCommand command, string code, int exitCode, ICliConsole console)
    {
        if (command.Json) console.Write(JsonSerializer.Serialize(new { schemaVersion = 1, error = new { code } }, ContractJson.Options) + Environment.NewLine);
        else if (exitCode != 130) console.WriteError("token-meter: " + code + Environment.NewLine);
        return exitCode;
    }

    private static TurnProjection[] SelectTurns(CliCommandKind kind, IReadOnlyList<TurnProjection> turns)
    {
        var ordered = turns.OrderBy(static item => item.ObservedAt ?? DateTimeOffset.MinValue).ToArray();
        if (kind is CliCommandKind.Turns or CliCommandKind.Rebuild or CliCommandKind.Sync)
        {
            return ordered;
        }

        if (kind == CliCommandKind.Current)
        {
            return [ordered[^1]];
        }

        var terminal = ordered.Where(static item => item.ExecutionState is ExecutionState.Completed or ExecutionState.Interrupted or ExecutionState.Failed).ToArray();
        return terminal.Length > 0
            ? [terminal[^1]]
            : [ordered[^1] with
            {
                MeasurementQuality = MeasurementQuality.Provisional,
                Diagnostics = ordered[^1].Diagnostics.Append("last_provisional").ToArray()
            }];
    }

    private static void RenderTurns(CliCommand command, IReadOnlyList<TurnProjection> turns, ICliConsole console)
    {
        if (command.Json)
        {
            console.Write(JsonSerializer.Serialize(new { schemaVersion = 1, turns = turns.Select(ToSafeTurn) }, ContractJson.Options) + Environment.NewLine);
            return;
        }
        foreach (var turn in turns) console.Write(RenderTurn(turn));
    }

    private static object ToSafeTurn(TurnProjection turn) => new
    {
        provider = turn.TurnKey.Provider,
        sessionId = turn.TurnKey.RootSessionId,
        turnId = turn.TurnKey.RootTurnId,
        execution = turn.ExecutionState,
        measurement = turn.MeasurementQuality,
        scope = turn.RootScope,
        observedAt = turn.ObservedAt,
        freshInput = turn.Usage.UncachedInput,
        inputTotal = turn.Usage.InputTotal,
        cacheRead = turn.Usage.CacheRead,
        cacheWrite = turn.Usage.CacheWrite,
        output = turn.Usage.Output,
        reasoning = turn.Usage.Reasoning,
        processedTokens = turn.Usage.ProcessedTokens,
        nativeTotal = turn.Usage.NativeTotal,
        apiCalls = turn.ApiCallCount,
        unknownObservationCount = turn.UnknownObservationCount,
        sourceAvailability = turn.Diagnostics.Contains("stored_fallback", StringComparer.Ordinal) ? "missing" : "available",
        diagnostics = turn.Diagnostics
    };

    private static string RenderTurn(TurnProjection turn)
    {
        var usage = turn.Usage;
        var lines = new List<string>
        {
            "Turn          " + turn.TurnKey.RootTurnId,
            "Execution     " + turn.ExecutionState.ToString().ToLowerInvariant(),
            "Measurement   " + turn.MeasurementQuality.ToString().ToLowerInvariant() + " / scope: " + turn.RootScope.ToString().ToLowerInvariant(),
            "Observed at   " + (turn.ObservedAt?.ToString("O") ?? "N/A"),
            Format("Fresh input", usage.UncachedInput), Format("Input (total)", usage.InputTotal), Format("Cache read", usage.CacheRead),
            Format("Cache write", usage.CacheWrite), Format("Output", usage.Output), Format("Reasoning", usage.Reasoning),
            Format("Processed", usage.ProcessedTokens), Format("API calls", turn.ApiCallCount)
        };
        if (turn.UnknownObservationCount > 0) lines.Add("Warning       incomplete observations: " + turn.UnknownObservationCount);
        if (turn.Diagnostics.Count > 0) lines.Add("Warning       " + string.Join(", ", turn.Diagnostics));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Format(string label, long? value) => label.PadRight(14) + (value?.ToString("N0", CultureInfo.InvariantCulture) ?? "N/A");
    private static string Format(string label, int? value) => label.PadRight(14) + (value?.ToString(CultureInfo.InvariantCulture) ?? "N/A");
    private static void RenderStorage(CliCommand command, object value, ICliConsole console)
    {
        if (command.Json) console.Write(JsonSerializer.Serialize(value, ContractJson.Options) + Environment.NewLine);
        else console.Write(string.Join(Environment.NewLine, value.GetType().GetProperties().Select(property => property.Name + ": " + property.GetValue(value))) + Environment.NewLine);
    }
    private static string SafeCode(Exception exception) => exception is IOException ? "storage_error" : "command_failed";
    private static void RenderHelp(ICliConsole console) => console.Write("Usage: token-meter <current|last|turns|sync|rebuild|storage|hook> [options]" + Environment.NewLine + "Options: --provider claude|codex --session ID --workspace PATH --json --strict --non-interactive" + Environment.NewLine + "Hook: hook <install|status|uninstall> --provider NAME [options]" + Environment.NewLine + "Privacy: prompts, messages, tool input, environment variables, and credentials are never displayed or stored." + Environment.NewLine);
}

public enum CliCommandKind { Help, Current, Last, Turns, Sync, Rebuild, StorageStatus, StorageMigrate }

public sealed record CliCommand(CliCommandKind Kind, ProviderKind? Provider, string? SessionId, string Workspace, bool Json, bool Strict, bool DryRun, StorageMode? DestinationMode, InteractionMode InteractionMode, bool IsContinuousIntegration)
{
    public static CliCommand TryParse(string[] args)
    {
        try { return Parse(args); }
        catch (ArgumentException) { return Default(CliCommandKind.Current); }
    }

    public static CliCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") return Default(CliCommandKind.Help);
        var index = 0;
        var kind = args[index++] switch { "current" => CliCommandKind.Current, "last" => CliCommandKind.Last, "turns" => CliCommandKind.Turns, "sync" => CliCommandKind.Sync, "rebuild" => CliCommandKind.Rebuild, "storage" => ParseStorage(args, ref index), _ => throw new ArgumentException("Unknown command.") };
        ProviderKind? provider = null; string? session = null; string? workspace = null; var json = false; var strict = false; var dryRun = false; StorageMode? destination = null; var nonInteractive = false;
        while (index < args.Length)
        {
            var option = args[index++];
            switch (option)
            {
                case "--provider": provider = ParseProvider(Value(args, ref index, option)); break;
                case "--session": session = Value(args, ref index, option); break;
                case "--workspace": workspace = Value(args, ref index, option); break;
                case "--json": json = true; break;
                case "--strict": strict = true; break;
                case "--dry-run": dryRun = true; break;
                case "--non-interactive": nonInteractive = true; break;
                case "--to": destination = ParseMode(Value(args, ref index, option)); break;
                default: throw new ArgumentException("Unknown option: " + option);
            }
        }
        return new(kind, provider, session, FindWorkspace(workspace), json, strict, dryRun, destination, json ? InteractionMode.Json : nonInteractive ? InteractionMode.NonInteractive : InteractionMode.Interactive, string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    }
    private static CliCommandKind ParseStorage(string[] args, ref int index) { if (index >= args.Length) throw new ArgumentException("Storage action required."); return args[index++] switch { "status" => CliCommandKind.StorageStatus, "migrate" => CliCommandKind.StorageMigrate, _ => throw new ArgumentException("Unknown storage action.") }; }
    private static CliCommand Default(CliCommandKind kind) => new(kind, null, null, FindWorkspace(null), false, false, false, null, InteractionMode.Interactive, false);
    private static string Value(string[] args, ref int index, string option) => index < args.Length ? args[index++] : throw new ArgumentException("Value required for " + option);
    private static ProviderKind ParseProvider(string value) => value.ToLowerInvariant() switch { "claude" or "claude-code" => ProviderKind.Claude, "codex" => ProviderKind.Codex, _ => throw new ArgumentException("Unsupported provider.") };
    private static StorageMode ParseMode(string value) => value.ToLowerInvariant() switch { "global" => StorageMode.Global, "workspace" => StorageMode.Workspace, _ => throw new ArgumentException("Unsupported storage mode.") };
    private static string FindWorkspace(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(value);
        }

        var current = new DirectoryInfo(Path.GetFullPath(Environment.CurrentDirectory));
        for (var candidate = current; candidate is not null; candidate = candidate.Parent)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, ".git")) ||
                File.Exists(Path.Combine(candidate.FullName, ".git")))
            {
                return candidate.FullName;
            }
        }

        return current.FullName;
    }
}
