using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskTokenMeter.Cli.Hooks;

public interface IHookSettingsFileSystem
{
    bool FileExists(string path);
    void CreateDirectory(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void CopyFile(string source, string destination, bool overwrite);
    void ReplaceFile(string source, string destination);
    void DeleteFile(string path);
}

public sealed class PhysicalHookSettingsFileSystem : IHookSettingsFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents, new UTF8Encoding(false));
    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public void ReplaceFile(string source, string destination) => File.Move(source, destination, overwrite: true);
    public void DeleteFile(string path) => File.Delete(path);
}

public abstract class ProviderHookInstaller
{
    public const string ManagedMarker = "task-token-meter-v1";
    private readonly IHookSettingsFileSystem fileSystem;

    protected ProviderHookInstaller(IHookSettingsFileSystem? fileSystem = null)
    {
        this.fileSystem = fileSystem ?? new PhysicalHookSettingsFileSystem();
    }

    public abstract HookProvider Provider { get; }
    public abstract string SupportedProviderVersion { get; }
    protected abstract IReadOnlyList<string> Events { get; }

    public HookInstallationStatus Install(string settingsPath, string executablePath, string providerVersion)
    {
        var canonicalSettings = ValidateSettingsPath(settingsPath);
        var canonicalExecutable = ValidateExecutablePath(executablePath);
        if (!string.Equals(providerVersion, SupportedProviderVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Provider version {providerVersion} is not supported for Hook installation.");
        }

        var source = fileSystem.FileExists(canonicalSettings)
            ? fileSystem.ReadAllText(canonicalSettings)
            : "{}\n";
        var root = ParseRoot(source);
        var changed = Merge(root, BuildCommand(canonicalExecutable));
        if (changed)
        {
            AtomicWrite(canonicalSettings, Serialize(root, source));
        }

        return Inspect(canonicalSettings, root);
    }

    public HookInstallationStatus Uninstall(string settingsPath)
    {
        var canonicalSettings = ValidateSettingsPath(settingsPath);
        if (!fileSystem.FileExists(canonicalSettings))
        {
            return new HookInstallationStatus(Provider, HookInstallationState.NotInstalled, canonicalSettings, 0, Events.Count, null);
        }

        var source = fileSystem.ReadAllText(canonicalSettings);
        var root = ParseRoot(source);
        if (RemoveManagedEntries(root))
        {
            AtomicWrite(canonicalSettings, Serialize(root, source));
        }

        return Inspect(canonicalSettings, root);
    }

    public HookInstallationStatus Status(string settingsPath)
    {
        var canonicalSettings = ValidateSettingsPath(settingsPath);
        if (!fileSystem.FileExists(canonicalSettings))
        {
            return new HookInstallationStatus(Provider, HookInstallationState.NotInstalled, canonicalSettings, 0, Events.Count, null);
        }

        return Inspect(canonicalSettings, ParseRoot(fileSystem.ReadAllText(canonicalSettings)));
    }

    protected abstract JsonObject CreateHandler(string command);

    private bool Merge(JsonObject root, string command)
    {
        var hooks = GetOrCreateHooks(root);
        var changed = false;
        foreach (var eventName in Events)
        {
            var groups = GetOrCreateGroups(hooks, eventName);
            if (CountManaged(groups) > 0)
            {
                continue;
            }

            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(CreateHandler(command))
            });
            changed = true;
        }

        return changed;
    }

    private bool RemoveManagedEntries(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        var changed = false;
        foreach (var eventName in Events)
        {
            if (hooks[eventName] is not JsonArray groups)
            {
                continue;
            }

            for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                if (groups[groupIndex] is not JsonObject group || group["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                for (var handlerIndex = handlers.Count - 1; handlerIndex >= 0; handlerIndex--)
                {
                    if (IsManagedHandler(handlers[handlerIndex]))
                    {
                        handlers.RemoveAt(handlerIndex);
                        changed = true;
                    }
                }

                if (handlers.Count == 0 && group.Count == 1)
                {
                    groups.RemoveAt(groupIndex);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        return changed;
    }

    private HookInstallationStatus Inspect(string settingsPath, JsonObject root)
    {
        var count = 0;
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var eventName in Events)
            {
                if (hooks[eventName] is JsonArray groups)
                {
                    count += CountManaged(groups);
                }
            }
        }

        var state = count switch
        {
            0 => HookInstallationState.NotInstalled,
            var installed when installed == Events.Count => HookInstallationState.Installed,
            _ => HookInstallationState.PartiallyInstalled
        };
        var backup = BackupPath(settingsPath);
        return new HookInstallationStatus(
            Provider,
            state,
            settingsPath,
            count,
            Events.Count,
            fileSystem.FileExists(backup) ? backup : null);
    }

    private static JsonObject GetOrCreateHooks(JsonObject root)
    {
        if (root["hooks"] is null)
        {
            var created = new JsonObject();
            root["hooks"] = created;
            return created;
        }

        return root["hooks"] as JsonObject
            ?? throw new InvalidDataException("The settings hooks field must be an object.");
    }

    private static JsonArray GetOrCreateGroups(JsonObject hooks, string eventName)
    {
        if (hooks[eventName] is null)
        {
            var created = new JsonArray();
            hooks[eventName] = created;
            return created;
        }

        return hooks[eventName] as JsonArray
            ?? throw new InvalidDataException($"The Hook event {eventName} must be an array.");
    }

    private int CountManaged(JsonArray groups) => groups
        .OfType<JsonObject>()
        .SelectMany(static group => group["hooks"] is JsonArray handlers ? handlers : [])
        .Count(IsManagedHandler);

    private bool IsManagedHandler(JsonNode? node)
    {
        if (node is not JsonObject handler || handler["type"]?.GetValue<string>() != "command")
        {
            return false;
        }

        var command = handler["command"]?.GetValue<string>();
        return command is not null &&
            command.Contains("--managed-by " + ManagedMarker, StringComparison.Ordinal) &&
            command.Contains("--provider " + Provider.ToCliName(), StringComparison.Ordinal);
    }

    private string BuildCommand(string executablePath) =>
        QuoteExecutable(executablePath) + " hook run --provider " + Provider.ToCliName() +
        " --managed-by " + ManagedMarker;

    private static string QuoteExecutable(string path)
    {
        if (path.IndexOfAny(['\"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Hook executable path contains unsupported characters.");
        }

        return "\"" + path + "\"";
    }

    private static JsonObject ParseRoot(string source)
    {
        try
        {
            return JsonNode.Parse(
                source,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject ?? throw new InvalidDataException("Provider settings must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Provider settings JSON is malformed.", exception);
        }
    }

    private static string Serialize(JsonObject root, string original)
    {
        var lineEnding = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\n", lineEnding, StringComparison.Ordinal);
        return text + lineEnding;
    }

    private void AtomicWrite(string settingsPath, string contents)
    {
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("Provider settings path has no parent directory.");
        fileSystem.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(settingsPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = BackupPath(settingsPath);
        try
        {
            fileSystem.WriteAllText(temporary, contents);
            if (fileSystem.FileExists(settingsPath))
            {
                fileSystem.CopyFile(settingsPath, backup, overwrite: true);
            }

            fileSystem.ReplaceFile(temporary, settingsPath);
        }
        catch
        {
            if (!fileSystem.FileExists(settingsPath) && fileSystem.FileExists(backup))
            {
                fileSystem.CopyFile(backup, settingsPath, overwrite: true);
            }

            throw;
        }
        finally
        {
            if (fileSystem.FileExists(temporary))
            {
                fileSystem.DeleteFile(temporary);
            }
        }
    }

    private static string ValidateSettingsPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Provider settings path must be an absolute JSON path.");
        }

        return Path.GetFullPath(path);
    }

    private static string ValidateExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Hook executable path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException("Hook executable was not found.");
    }

    private static string BackupPath(string settingsPath) => settingsPath + ".task-token-meter.bak";
}

public sealed class ClaudeHookInstaller(IHookSettingsFileSystem? fileSystem = null) : ProviderHookInstaller(fileSystem)
{
    private static readonly string[] SupportedEvents = ["Stop", "SubagentStop", "StopFailure"];
    public override HookProvider Provider => HookProvider.Claude;
    public override string SupportedProviderVersion => "2.1.278";
    protected override IReadOnlyList<string> Events => SupportedEvents;
    protected override JsonObject CreateHandler(string command) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["async"] = true,
        ["timeout"] = 10
    };
}

public sealed class CodexHookInstaller(IHookSettingsFileSystem? fileSystem = null) : ProviderHookInstaller(fileSystem)
{
    private static readonly string[] SupportedEvents = ["Stop", "SubagentStop", "Interrupt"];
    public override HookProvider Provider => HookProvider.Codex;
    public override string SupportedProviderVersion => "0.153.4";
    protected override IReadOnlyList<string> Events => SupportedEvents;
    protected override JsonObject CreateHandler(string command) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["async"] = true,
        ["timeout"] = 3
    };
}
