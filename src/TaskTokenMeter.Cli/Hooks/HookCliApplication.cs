using System.Globalization;
using System.Text;
using System.Text.Json;
using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Cli.Hooks;

public class HookCommandServices
{
    private readonly string globalDataRoot;

    public HookCommandServices(string? globalDataRoot = null)
    {
        this.globalDataRoot = Path.GetFullPath(globalDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskTokenMeter"));
    }

    public virtual ProviderHookInstaller Installer(HookProvider provider) => provider switch
    {
        HookProvider.Claude => new ClaudeHookInstaller(),
        HookProvider.Codex => new CodexHookInstaller(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public virtual HookContextValidator Validator() => new(DefaultAllowedRoots());

    public virtual IHookWorkerLauncher Launcher()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        return new ProcessHookWorkerLauncher(executable);
    }

    public virtual IHookDiagnosticSink Diagnostics(string path) => new FileHookDiagnosticSink(path);
    public virtual IHookAggregationRuntime AggregationRuntime() => new AdapterHookAggregationRuntime(globalDataRoot);
    public virtual string DiagnosticsPath => Path.Combine(globalDataRoot, "diagnostics", "hooks.jsonl");

    public virtual string SettingsPath(HookProvider provider)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return provider == HookProvider.Claude
            ? Path.Combine(profile, ".claude", "settings.json")
            : Path.Combine(profile, ".codex", "hooks.json");
    }

    private static Dictionary<HookProvider, IReadOnlyList<string>> DefaultAllowedRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Dictionary<HookProvider, IReadOnlyList<string>>
        {
            [HookProvider.Claude] = SourceRoots("TOKEN_METER_CLAUDE_SOURCES", Path.Combine(profile, ".claude", "projects")),
            [HookProvider.Codex] = SourceRoots("TOKEN_METER_CODEX_SOURCES", Path.Combine(profile, ".codex", "sessions"))
        };
    }

    private static string[] SourceRoots(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(configured)
            ? [fallback]
            : configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public static class HookCliApplication
{
    private const int Success = 0;
    private const int GeneralError = 1;
    private const int InvalidArguments = 2;

    public static async Task<int> RunAsync(
        string[] args,
        ICliConsole console,
        TextReader input,
        TextWriter output,
        HookCommandServices? services = null,
        CancellationToken cancellationToken = default)
    {
        services ??= new HookCommandServices();
        try
        {
            var command = HookCommand.Parse(args, services);
            if (command.Action == HookAction.Run)
            {
                return await HookEntryPoint.RunAsync(
                    command.Provider, input, output, services.Validator(), services.Launcher(),
                    services.Diagnostics(services.DiagnosticsPath), services.DiagnosticsPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (command.Action == HookAction.Worker)
            {
                var request = new HookWorkerRequest(
                    new HookInvocationContext(
                        command.Provider, command.EventName!, command.SessionId!, command.TurnId,
                        command.Workspace!, command.TranscriptPaths),
                    command.DiagnosticsPath!, TimeSpan.FromMilliseconds(command.TimeoutMilliseconds));
                return await HookWorker.RunAsync(
                    request, services.AggregationRuntime, services.Diagnostics(command.DiagnosticsPath!), cancellationToken)
                    .ConfigureAwait(false);
            }

            var installer = services.Installer(command.Provider);
            var status = command.Action switch
            {
                HookAction.Install => installer.Install(command.SettingsPath!, command.ExecutablePath!, command.ProviderVersion!),
                HookAction.Uninstall => installer.Uninstall(command.SettingsPath!),
                HookAction.Status => installer.Status(command.SettingsPath!),
                _ => throw new InvalidOperationException("Unsupported Hook action.")
            };
            RenderStatus(status, command.Json, console);
            return Success;
        }
        catch (ArgumentException)
        {
            console.WriteError("token-meter: invalid_hook_arguments" + Environment.NewLine);
            return InvalidArguments;
        }
        catch (Exception)
        {
            console.WriteError("token-meter: hook_command_failed" + Environment.NewLine);
            return GeneralError;
        }
    }

    private static void RenderStatus(HookInstallationStatus status, bool json, ICliConsole console)
    {
        if (json)
        {
            console.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                provider = status.Provider,
                state = status.State,
                settingsPath = status.SettingsPath,
                installedEntryCount = status.InstalledEntryCount,
                expectedEntryCount = status.ExpectedEntryCount,
                backupPath = status.BackupPath
            }, ContractJson.Options) + Environment.NewLine);
            return;
        }

        console.Write("Provider: " + status.Provider.ToCliName() + Environment.NewLine);
        console.Write("State: " + status.State.ToString().ToLowerInvariant() + Environment.NewLine);
        console.Write("Entries: " + status.InstalledEntryCount.ToString(CultureInfo.InvariantCulture) +
            "/" + status.ExpectedEntryCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
        console.Write("Settings: " + status.SettingsPath + Environment.NewLine);
    }
}

internal enum HookAction { Install, Uninstall, Status, Run, Worker }

internal sealed record HookCommand(
    HookAction Action,
    HookProvider Provider,
    string? ProviderVersion,
    string? SettingsPath,
    string? ExecutablePath,
    bool Json,
    string? EventName,
    string? SessionId,
    string? TurnId,
    string? Workspace,
    IReadOnlyList<string> TranscriptPaths,
    string? DiagnosticsPath,
    int TimeoutMilliseconds)
{
    public static HookCommand Parse(string[] args, HookCommandServices services)
    {
        if (args.Length == 0) throw new ArgumentException("Hook action is required.");
        var action = args[0] switch
        {
            "install" => HookAction.Install,
            "uninstall" => HookAction.Uninstall,
            "status" => HookAction.Status,
            "run" => HookAction.Run,
            "worker" => HookAction.Worker,
            _ => throw new ArgumentException("Unknown Hook action.")
        };
        HookProvider? provider = null;
        string? providerVersion = null;
        string? settingsPath = null;
        string? executablePath = null;
        string? eventName = null;
        string? sessionId = null;
        string? turnId = null;
        string? workspace = null;
        string? diagnosticsPath = null;
        var transcripts = new List<string>();
        var timeoutMilliseconds = 10_000;
        var json = false;
        var managed = false;
        var index = 1;
        while (index < args.Length)
        {
            var option = args[index++];
            switch (option)
            {
                case "--provider": provider = HookProviderNames.Parse(Value(args, ref index, option)); break;
                case "--provider-version": providerVersion = Value(args, ref index, option); break;
                case "--settings": settingsPath = Value(args, ref index, option); break;
                case "--executable": executablePath = Value(args, ref index, option); break;
                case "--json": json = true; break;
                case "--managed-by": managed = Value(args, ref index, option) == ProviderHookInstaller.ManagedMarker; break;
                case "--event": eventName = Value(args, ref index, option); break;
                case "--session": sessionId = Value(args, ref index, option); break;
                case "--turn": turnId = Value(args, ref index, option); break;
                case "--workspace": workspace = Value(args, ref index, option); break;
                case "--transcript": transcripts.Add(Value(args, ref index, option)); break;
                case "--diagnostics": diagnosticsPath = Value(args, ref index, option); break;
                case "--timeout-ms": timeoutMilliseconds = ParseTimeout(Value(args, ref index, option)); break;
                default: throw new ArgumentException("Unknown Hook option.");
            }
        }

        var selectedProvider = provider ?? throw new ArgumentException("Hook provider is required.");
        settingsPath ??= services.SettingsPath(selectedProvider);
        if (action == HookAction.Install)
        {
            if (providerVersion is null) throw new ArgumentException("Provider version is required.");
            executablePath ??= Environment.ProcessPath ?? throw new ArgumentException("Hook executable path is required.");
        }

        if (action == HookAction.Run && !managed) throw new ArgumentException("Managed Hook marker is required.");
        if (action == HookAction.Worker)
        {
            eventName = Required(eventName, "event");
            sessionId = Required(sessionId, "session");
            workspace = CanonicalExistingDirectory(Required(workspace, "workspace"));
            diagnosticsPath = CanonicalAbsolutePath(Required(diagnosticsPath, "diagnostics"));
            if (transcripts.Count == 0) throw new ArgumentException("At least one transcript is required.");
            transcripts = transcripts.Select(CanonicalExistingFile).ToList();
        }

        return new HookCommand(
            action, selectedProvider, providerVersion, settingsPath, executablePath, json,
            eventName, sessionId, turnId, workspace, transcripts, diagnosticsPath, timeoutMilliseconds);
    }

    private static string Value(string[] args, ref int index, string option) =>
        index < args.Length ? args[index++] : throw new ArgumentException("Value required for " + option);

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Hook " + name + " is required.");

    private static int ParseTimeout(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout) && timeout is >= 50 and <= 60_000
            ? timeout : throw new ArgumentException("Hook timeout is invalid.");

    private static string CanonicalExistingDirectory(string value)
    {
        var path = CanonicalAbsolutePath(value);
        return Directory.Exists(path) ? path : throw new ArgumentException("Hook workspace does not exist.");
    }

    private static string CanonicalExistingFile(string value)
    {
        var path = CanonicalAbsolutePath(value);
        return File.Exists(path) ? path : throw new ArgumentException("Hook transcript does not exist.");
    }

    private static string CanonicalAbsolutePath(string value) =>
        Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : throw new ArgumentException("Hook path must be absolute.");
}

public sealed class CliConsoleTextWriter(ICliConsole console) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(string? value)
    {
        if (value is not null) console.Write(value);
    }
}
