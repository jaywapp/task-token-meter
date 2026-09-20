using System.Diagnostics;
using System.Text.Json;

namespace TaskTokenMeter.Cli.Hooks;

public interface IHookWorkerLauncher
{
    void Start(HookWorkerRequest request);
}

public sealed class ProcessHookWorkerLauncher(string executablePath) : IHookWorkerLauncher
{
    private readonly string executablePath = ValidateExecutable(executablePath);

    public void Start(HookWorkerRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add("hook");
        startInfo.ArgumentList.Add("worker");
        startInfo.ArgumentList.Add("--provider");
        startInfo.ArgumentList.Add(request.Context.Provider.ToCliName());
        startInfo.ArgumentList.Add("--event");
        startInfo.ArgumentList.Add(request.Context.EventName);
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add(request.Context.SessionId);
        if (request.Context.TurnId is not null)
        {
            startInfo.ArgumentList.Add("--turn");
            startInfo.ArgumentList.Add(request.Context.TurnId);
        }

        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(request.Context.WorkspacePath);
        foreach (var transcriptPath in request.Context.TranscriptPaths)
        {
            startInfo.ArgumentList.Add("--transcript");
            startInfo.ArgumentList.Add(transcriptPath);
        }

        startInfo.ArgumentList.Add("--diagnostics");
        startInfo.ArgumentList.Add(request.DiagnosticsPath);
        startInfo.ArgumentList.Add("--timeout-ms");
        startInfo.ArgumentList.Add(((int)request.Timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Hook worker did not start.");
        process.StandardInput.Close();
        process.Dispose();
    }

    private static string ValidateExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Hook executable path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Hook executable was not found.");
        }

        return fullPath;
    }
}

public sealed class HookContextValidator(
    IReadOnlyDictionary<HookProvider, IReadOnlyList<string>> allowedTranscriptRoots)
{
    private const int MaximumIdentifierLength = 512;
    private readonly Dictionary<HookProvider, string[]> allowedRoots = allowedTranscriptRoots
        .ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Select(ResolvePhysicalPath).Distinct(PathComparer).ToArray());

    public HookInvocationContext Validate(HookProvider provider, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Hook payload must be an object.");
        }

        var eventName = RequiredString(payload, "hook_event_name");
        if (!SupportedEvents(provider).Contains(eventName, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Hook event is not supported.");
        }

        var sessionId = ValidateIdentifier(RequiredString(payload, "session_id"));
        var turnId = OptionalString(payload, "turn_id");
        if (provider == HookProvider.Codex)
        {
            turnId = ValidateIdentifier(turnId ?? throw new InvalidDataException("Codex Hook turn_id is required."));
        }

        var workspace = CanonicalExistingDirectory(RequiredString(payload, "cwd"));
        var transcriptPaths = new List<string>
        {
            ValidateTranscript(provider, RequiredString(payload, "transcript_path"))
        };
        if (string.Equals(eventName, "SubagentStop", StringComparison.Ordinal) &&
            OptionalString(payload, "agent_transcript_path") is { } agentTranscript)
        {
            transcriptPaths.Add(ValidateTranscript(provider, agentTranscript));
        }

        return new HookInvocationContext(
            provider,
            eventName,
            sessionId,
            turnId,
            workspace,
            transcriptPaths.Distinct(PathComparer).ToArray());
    }

    private string ValidateTranscript(HookProvider provider, string value)
    {
        if (!Path.IsPathFullyQualified(value) || !string.Equals(Path.GetExtension(value), ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Hook transcript path is invalid.");
        }

        var fullPath = Path.GetFullPath(value);
        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException("Hook transcript path is outside the configured source roots.");
        }

        var physicalPath = ResolvePhysicalPath(fullPath);
        if (!allowedRoots.TryGetValue(provider, out var roots) ||
            !roots.Any(root => IsWithin(physicalPath, root)))
        {
            throw new InvalidDataException("Hook transcript path is outside the configured source roots.");
        }

        return physicalPath;
    }

    private static string RequiredString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"Hook field {name} is required.");

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString() : null;

    private static string ValidateIdentifier(string value)
    {
        if (value.Length > MaximumIdentifierLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException("Hook identifier is invalid.");
        }

        return value;
    }

    private static string CanonicalExistingDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Hook workspace path must be absolute.");
        }

        var fullPath = CanonicalDirectory(path);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException("Hook workspace was not found.");
    }

    private static string CanonicalDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("Hook path root is invalid.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate) ? new FileInfo(candidate) : null;
            current = entry?.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relative) &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> SupportedEvents(HookProvider provider) => provider switch
    {
        HookProvider.Claude => ["Stop", "SubagentStop", "StopFailure"],
        HookProvider.Codex => ["Stop", "SubagentStop", "Interrupt"],
        _ => []
    };

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public static class HookEntryPoint
{
    private const int MaximumPayloadCharacters = 64 * 1024;

    public static async Task<int> RunAsync(
        HookProvider provider,
        TextReader input,
        TextWriter output,
        HookContextValidator validator,
        IHookWorkerLauncher launcher,
        IHookDiagnosticSink diagnostics,
        string diagnosticsPath,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        string? eventName = null;
        var neutralWritten = false;
        try
        {
            var payload = await ReadLimitedAsync(input, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("hook_event_name", out var eventProperty) &&
                eventProperty.ValueKind == JsonValueKind.String)
            {
                eventName = eventProperty.GetString();
            }

            WriteNeutral(provider, eventName, output);
            neutralWritten = true;
            var context = validator.Validate(provider, document.RootElement);
            launcher.Start(new HookWorkerRequest(context, diagnosticsPath, TimeSpan.FromSeconds(10)));
        }
        catch (OperationCanceledException)
        {
            if (!neutralWritten) WriteNeutral(provider, eventName, output);
            diagnostics.Record("hook_cancelled", provider, eventName, started.Elapsed);
        }
        catch (JsonException)
        {
            if (!neutralWritten) WriteNeutral(provider, eventName, output);
            diagnostics.Record("hook_payload_invalid", provider, eventName, started.Elapsed);
        }
        catch (Exception)
        {
            if (!neutralWritten) WriteNeutral(provider, eventName, output);
            diagnostics.Record("hook_enqueue_failed", provider, eventName, started.Elapsed);
        }

        return 0;
    }

    private static async Task<string> ReadLimitedAsync(TextReader input, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (builder.Length + read > MaximumPayloadCharacters)
            {
                throw new InvalidDataException("Hook payload is too large.");
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static void WriteNeutral(HookProvider provider, string? eventName, TextWriter output)
    {
        if (provider == HookProvider.Codex && !string.Equals(eventName, "Interrupt", StringComparison.Ordinal))
        {
            output.Write("{}");
            output.Flush();
        }
    }
}
