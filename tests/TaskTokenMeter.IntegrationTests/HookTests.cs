using System.Diagnostics;
using System.Text.Json;
using TaskTokenMeter.Cli;
using TaskTokenMeter.Cli.Hooks;
using TaskTokenMeter.Cli.Selection;
using Xunit;

namespace TaskTokenMeter.IntegrationTests;

public sealed class HookTests
{
    [Theory]
    [InlineData(HookProvider.Claude, "2.1.278", 3)]
    [InlineData(HookProvider.Codex, "0.153.4", 3)]
    public void InstallStatusAndUninstallPreserveUnrelatedSettingsAndAreIdempotent(
        HookProvider provider,
        string version,
        int expectedEntries)
    {
        using var fixture = HookFixture.Create();
        var settings = fixture.Settings(provider);
        var original = """
            {
              "unknownField": { "preserve": true },
              "hooks": {
                "Stop": [
                  {
                    "matcher": "unrelated",
                    "hooks": [
                      { "type": "command", "command": "unrelated-tool --check", "custom": 7 }
                    ]
                  }
                ]
              }
            }
            """;
        File.WriteAllText(settings, original);
        var installer = Installer(provider);

        var installed = installer.Install(settings, fixture.ExecutablePath, version);
        var once = File.ReadAllText(settings);
        var repeated = installer.Install(settings, fixture.ExecutablePath, version);

        Assert.Equal(HookInstallationState.Installed, installed.State);
        Assert.Equal(expectedEntries, installed.InstalledEntryCount);
        Assert.Equal(installed, repeated);
        Assert.Equal(once, File.ReadAllText(settings));
        Assert.True(File.Exists(settings + ".task-token-meter.bak"));
        using (var document = JsonDocument.Parse(once))
        {
            Assert.True(document.RootElement.GetProperty("unknownField").GetProperty("preserve").GetBoolean());
            Assert.Equal("unrelated-tool --check", document.RootElement.GetProperty("hooks")
                .GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
            var managedCommands = document.RootElement.GetProperty("hooks")
                .EnumerateObject().SelectMany(static property => property.Value.EnumerateArray())
                .SelectMany(static group => group.GetProperty("hooks").EnumerateArray())
                .Select(static handler => handler.GetProperty("command").GetString()!)
                .Where(command => command.Contains(ProviderHookInstaller.ManagedMarker, StringComparison.Ordinal))
                .ToArray();
            Assert.All(managedCommands, command => Assert.Contains(fixture.ExecutablePath, command, StringComparison.Ordinal));
            Assert.Equal(expectedEntries, CountManaged(document.RootElement));
        }

        Assert.Equal(HookInstallationState.Installed, installer.Status(settings).State);
        Assert.Equal(HookInstallationState.NotInstalled, installer.Uninstall(settings).State);
        var uninstalled = File.ReadAllText(settings);
        Assert.Equal(uninstalled, File.ReadAllText(settings));
        Assert.Equal(HookInstallationState.NotInstalled, installer.Uninstall(settings).State);
        Assert.DoesNotContain(ProviderHookInstaller.ManagedMarker, uninstalled, StringComparison.Ordinal);
        using var finalDocument = JsonDocument.Parse(uninstalled);
        Assert.True(finalDocument.RootElement.GetProperty("unknownField").GetProperty("preserve").GetBoolean());
        Assert.Equal("unrelated-tool --check", finalDocument.RootElement.GetProperty("hooks")
            .GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Fact]
    public void MalformedAndUnsupportedSettingsLeaveOriginalBytesUntouched()
    {
        using var fixture = HookFixture.Create();
        var settings = fixture.Settings(HookProvider.Claude);
        const string malformed = "{ malformed provider settings";
        File.WriteAllText(settings, malformed);
        var installer = new ClaudeHookInstaller();

        Assert.Throws<InvalidDataException>(() => installer.Install(settings, fixture.ExecutablePath, "2.1.278"));
        Assert.Equal(malformed, File.ReadAllText(settings));
        Assert.False(File.Exists(settings + ".task-token-meter.bak"));

        File.WriteAllText(settings, "{}\n");
        Assert.Throws<NotSupportedException>(() => installer.Install(settings, fixture.ExecutablePath, "2.1.279"));
        Assert.Equal("{}\n", File.ReadAllText(settings));
    }

    [Fact]
    public void FailedAtomicReplaceRestoresOriginalFromBackup()
    {
        using var fixture = HookFixture.Create();
        var settings = fixture.Settings(HookProvider.Codex);
        const string original = "{\"existing\":true}";
        var files = new FailingReplaceFileSystem(settings, original);
        var installer = new CodexHookInstaller(files);

        Assert.Throws<UnauthorizedAccessException>(() => installer.Install(settings, fixture.ExecutablePath, "0.153.4"));
        Assert.Equal(original, files.ReadAllText(settings));
        Assert.Equal(original, files.ReadAllText(settings + ".task-token-meter.bak"));
    }

    [Fact]
    public void ReadOnlySettingsFailurePreservesOriginalOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = HookFixture.Create();
        var settings = fixture.Settings(HookProvider.Claude);
        const string original = "{\"existing\":true}";
        File.WriteAllText(settings, original);
        File.SetAttributes(settings, File.GetAttributes(settings) | FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => new ClaudeHookInstaller().Install(settings, fixture.ExecutablePath, "2.1.278"));
            Assert.Equal(original, File.ReadAllText(settings));
        }
        finally
        {
            File.SetAttributes(settings, FileAttributes.Normal);
            var backup = settings + ".task-token-meter.bak";
            if (File.Exists(backup)) File.SetAttributes(backup, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(HookProvider.Claude, "Stop", "")]
    [InlineData(HookProvider.Claude, "SubagentStop", "")]
    [InlineData(HookProvider.Claude, "StopFailure", "")]
    [InlineData(HookProvider.Codex, "Stop", "{}")]
    [InlineData(HookProvider.Codex, "SubagentStop", "{}")]
    [InlineData(HookProvider.Codex, "Interrupt", "")]
    public async Task ValidHookEventsReturnProviderNeutralOutputAndPassOnlyValidatedContext(
        HookProvider provider,
        string eventName,
        string expectedOutput)
    {
        using var fixture = HookFixture.Create();
        var transcript = fixture.Transcript(provider, "세션 로그.jsonl");
        var child = fixture.Transcript(provider, "child path.jsonl");
        var launcher = new CapturingLauncher();
        var diagnostics = new CapturingDiagnostics();
        var output = new StringWriter();
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = eventName,
            session_id = "session-safe",
            turn_id = provider == HookProvider.Codex ? "turn-safe" : null,
            cwd = fixture.WorkspacePath,
            transcript_path = transcript,
            agent_transcript_path = eventName == "SubagentStop" ? child : null,
            prompt = "raw prompt must be ignored",
            tool_input = new { token = "secret-tool-argument" }
        });

        var result = await HookEntryPoint.RunAsync(
            provider,
            new StringReader(payload),
            output,
            fixture.Validator(),
            launcher,
            diagnostics,
            fixture.DiagnosticsPath);

        Assert.Equal(0, result);
        Assert.Equal(expectedOutput, output.ToString());
        Assert.NotNull(launcher.Request);
        Assert.Equal("session-safe", launcher.Request!.Context.SessionId);
        Assert.Equal(fixture.WorkspacePath, launcher.Request.Context.WorkspacePath);
        Assert.Contains(transcript, launcher.Request.Context.TranscriptPaths);
        Assert.DoesNotContain("raw prompt", JsonSerializer.Serialize(launcher.Request), StringComparison.Ordinal);
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public void TranscriptPathThroughJunctionCannotEscapeConfiguredSourceRoot()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("ComSpec") is not { } commandProcessor ||
            !File.Exists(commandProcessor))
        {
            return;
        }

        using var fixture = HookFixture.Create();
        var outside = Path.Combine(fixture.Root, "outside source");
        Directory.CreateDirectory(outside);
        var escapedTranscript = Path.Combine(outside, "escaped.jsonl");
        File.WriteAllText(escapedTranscript, "{}\n");

        var junction = Path.Combine(fixture.SourceRoot(HookProvider.Codex), "escape");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Junction creation process did not start.");
        process.WaitForExit();
        var processOutput = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, processOutput);

        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                hook_event_name = "Stop",
                session_id = "session",
                turn_id = "turn",
                cwd = fixture.WorkspacePath,
                transcript_path = Path.Combine(junction, "escaped.jsonl")
            });

            Assert.Throws<InvalidDataException>(() => fixture.Validator().Validate(HookProvider.Codex, payload));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task ParseStartMissingExecutableAndCancellationFailuresAreAlwaysNeutral()
    {
        using var fixture = HookFixture.Create();
        var transcript = fixture.Transcript(HookProvider.Codex, "rollout.jsonl");
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            session_id = "session",
            turn_id = "turn",
            cwd = fixture.WorkspacePath,
            transcript_path = transcript,
            last_assistant_message = "must never be persisted"
        });
        var diagnostics = new CapturingDiagnostics();
        var output = new StringWriter();

        Assert.Equal(0, await HookEntryPoint.RunAsync(
            HookProvider.Codex, new StringReader(payload), output, fixture.Validator(),
            new ThrowingLauncher(new FileNotFoundException("missing executable")), diagnostics, fixture.DiagnosticsPath));
        Assert.Equal("{}", output.ToString());
        Assert.Contains(diagnostics.Entries, static entry => entry.Code == "hook_enqueue_failed");

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await HookEntryPoint.RunAsync(
            HookProvider.Codex, new StringReader("not-json"), output, fixture.Validator(),
            new CapturingLauncher(), diagnostics, fixture.DiagnosticsPath));
        Assert.Equal("{}", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await HookEntryPoint.RunAsync(
            HookProvider.Codex, new CancellingReader(), output, fixture.Validator(),
            new CapturingLauncher(), diagnostics, fixture.DiagnosticsPath));
        Assert.Equal("{}", output.ToString());
        Assert.DoesNotContain("must never be persisted", JsonSerializer.Serialize(diagnostics.Entries), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await HookEntryPoint.RunAsync(
            HookProvider.Codex, new StringReader(payload), output, fixture.Validator(),
            new ThrowingLauncher(new IOException("synthetic start failure")),
            new FileHookDiagnosticSink(fixture.DiagnosticsPath), fixture.DiagnosticsPath));
        var persistedDiagnostics = File.ReadAllText(fixture.DiagnosticsPath);
        Assert.Contains("hook_enqueue_failed", persistedDiagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("must never be persisted", persistedDiagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("session", persistedDiagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(transcript, persistedDiagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerTimeoutAndRouteConflictAreAbsorbed()
    {
        using var fixture = HookFixture.Create();
        var request = fixture.WorkerRequest(TimeSpan.FromMilliseconds(60));
        var diagnostics = new CapturingDiagnostics();
        var watch = Stopwatch.StartNew();

        Assert.Equal(0, await HookWorker.RunAsync(request, () => new DelayedRuntime(), diagnostics));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
        Assert.Contains(diagnostics.Entries, static entry => entry.Code == "hook_worker_timeout");

        var attempts = 0;
        Assert.Equal(0, await HookWorker.RunAsync(
            fixture.WorkerRequest(TimeSpan.FromSeconds(1)),
            () => new DelegateRuntime(_ =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException(new InvalidOperationException("route generation changed"))
                    : Task.CompletedTask;
            }),
            diagnostics));
        Assert.Equal(2, attempts);

        Assert.Equal(0, await HookWorker.RunAsync(
            fixture.WorkerRequest(TimeSpan.FromSeconds(1)),
            () => new DelegateRuntime(_ => Task.FromException(new IOException("synthetic aggregation error"))),
            diagnostics));
        Assert.Contains(diagnostics.Entries, static entry => entry.Code == "hook_worker_failed");
    }

    [Fact]
    public async Task CliUsesOnlyExplicitTemporarySettingsForInstallStatusAndUninstall()
    {
        using var fixture = HookFixture.Create();
        var settings = fixture.Settings(HookProvider.Codex);
        var console = new TestConsole();
        var services = new HookCommandServices(fixture.GlobalDataRoot);

        Assert.Equal(0, await HookCliApplication.RunAsync(
            ["install", "--provider", "codex", "--provider-version", "0.153.4", "--settings", settings, "--executable", fixture.ExecutablePath, "--json"],
            console, TextReader.Null, TextWriter.Null, services));
        Assert.True(File.Exists(settings));
        using (var installed = JsonDocument.Parse(console.StandardOutput))
        {
            Assert.Equal("installed", installed.RootElement.GetProperty("state").GetString());
        }

        console.Clear();
        Assert.Equal(0, await HookCliApplication.RunAsync(
            ["status", "--provider", "codex", "--settings", settings, "--json"],
            console, TextReader.Null, TextWriter.Null, services));
        Assert.Contains("installed", console.StandardOutput, StringComparison.OrdinalIgnoreCase);

        console.Clear();
        Assert.Equal(0, await HookCliApplication.RunAsync(
            ["uninstall", "--provider", "codex", "--settings", settings, "--json"],
            console, TextReader.Null, TextWriter.Null, services));
        Assert.Contains("notInstalled", console.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ActualChildProcessStartAcceptsSpaceAndKoreanPathsWithinTargetWhenAvailable()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ComSpec") is not { } commandProcessor || !File.Exists(commandProcessor)) return;
        using var fixture = HookFixture.Create();
        var launcher = new ProcessHookWorkerLauncher(commandProcessor);
        var watch = Stopwatch.StartNew();

        launcher.Start(fixture.WorkerRequest(TimeSpan.FromMilliseconds(100)));

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), watch.Elapsed.ToString());
    }

    private static ProviderHookInstaller Installer(HookProvider provider) => provider switch
    {
        HookProvider.Claude => new ClaudeHookInstaller(),
        HookProvider.Codex => new CodexHookInstaller(),
        _ => throw new InvalidOperationException()
    };

    private static int CountManaged(JsonElement root) => root.GetProperty("hooks")
        .EnumerateObject()
        .SelectMany(static property => property.Value.EnumerateArray())
        .SelectMany(static group => group.GetProperty("hooks").EnumerateArray())
        .Count(handler => handler.TryGetProperty("command", out var command) &&
            command.GetString()!.Contains(ProviderHookInstaller.ManagedMarker, StringComparison.Ordinal));

    private sealed class HookFixture : IDisposable
    {
        private HookFixture(string root)
        {
            Root = root;
            WorkspacePath = Path.Combine(root, "작업 공간");
            GlobalDataRoot = Path.Combine(root, "global data");
            ExecutablePath = Path.Combine(root, "bin path", "task token meter.exe");
            DiagnosticsPath = Path.Combine(root, "diagnostics", "hooks.jsonl");
            Directory.CreateDirectory(WorkspacePath);
            Directory.CreateDirectory(Path.GetDirectoryName(ExecutablePath)!);
            File.WriteAllText(ExecutablePath, "synthetic executable fixture");
            foreach (var provider in Enum.GetValues<HookProvider>()) Directory.CreateDirectory(SourceRoot(provider));
        }

        public string Root { get; }
        public string WorkspacePath { get; }
        public string GlobalDataRoot { get; }
        public string ExecutablePath { get; }
        public string DiagnosticsPath { get; }

        public static HookFixture Create()
        {
            var parent = Path.Combine(Path.GetTempPath(), "task-token-meter-hook-tests");
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"), "fixture with space 한글");
            Directory.CreateDirectory(root);
            return new HookFixture(root);
        }

        public string Settings(HookProvider provider)
        {
            var path = Path.Combine(Root, provider.ToCliName(), provider == HookProvider.Claude ? "settings.json" : "hooks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public string Transcript(HookProvider provider, string name)
        {
            var path = Path.Combine(SourceRoot(provider), name);
            File.WriteAllText(path, "{}\n");
            return path;
        }

        public HookContextValidator Validator() => new(new Dictionary<HookProvider, IReadOnlyList<string>>
        {
            [HookProvider.Claude] = [SourceRoot(HookProvider.Claude)],
            [HookProvider.Codex] = [SourceRoot(HookProvider.Codex)]
        });

        public HookWorkerRequest WorkerRequest(TimeSpan timeout)
        {
            var transcript = Transcript(HookProvider.Codex, "child args 한글.jsonl");
            return new HookWorkerRequest(
                new HookInvocationContext(HookProvider.Codex, "Stop", "session", "turn", WorkspacePath, [transcript]),
                DiagnosticsPath,
                timeout);
        }

        public string SourceRoot(HookProvider provider) => Path.Combine(Root, provider.ToCliName(), "source root");

        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "task-token-meter-hook-tests"));
            if (fullRoot.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private sealed class CapturingLauncher : IHookWorkerLauncher
    {
        public HookWorkerRequest? Request { get; private set; }
        public void Start(HookWorkerRequest request) => Request = request;
    }

    private sealed class ThrowingLauncher(Exception exception) : IHookWorkerLauncher
    {
        public void Start(HookWorkerRequest request) => throw exception;
    }

    private sealed class CapturingDiagnostics : IHookDiagnosticSink
    {
        public List<(string Code, HookProvider Provider, string? Event)> Entries { get; } = [];
        public void Record(string code, HookProvider provider, string? eventName, TimeSpan duration) =>
            Entries.Add((code, provider, eventName));
    }

    private sealed class CancellingReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new OperationCanceledException());
    }

    private sealed class DelayedRuntime : IHookAggregationRuntime
    {
        public Task AggregateAsync(HookInvocationContext context, CancellationToken cancellationToken) =>
            Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private sealed class DelegateRuntime(Func<CancellationToken, Task> action) : IHookAggregationRuntime
    {
        public Task AggregateAsync(HookInvocationContext context, CancellationToken cancellationToken) => action(cancellationToken);
    }

    private sealed class TestConsole : ICliConsole
    {
        public bool IsInputRedirected => true;
        public bool IsOutputRedirected => true;
        public bool IsColorEnabled => false;
        public string StandardOutput { get; private set; } = string.Empty;
        public string StandardError { get; private set; } = string.Empty;
        public void Write(string value) => StandardOutput += value;
        public void WriteError(string value) => StandardError += value;
        public SessionSelectionInput ReadInput() => SessionSelectionInput.EndOfFile;
        public void Clear() { StandardOutput = string.Empty; StandardError = string.Empty; }
    }

    private sealed class FailingReplaceFileSystem(string settingsPath, string original) : IHookSettingsFileSystem
    {
        private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            [settingsPath] = original
        };

        public bool FileExists(string path) => files.ContainsKey(path);
        public void CreateDirectory(string path) { }
        public string ReadAllText(string path) => files[path];
        public void WriteAllText(string path, string contents) => files[path] = contents;
        public void CopyFile(string source, string destination, bool overwrite) => files[destination] = files[source];
        public void ReplaceFile(string source, string destination)
        {
            files.Remove(destination);
            throw new UnauthorizedAccessException("Synthetic permission failure.");
        }
        public void DeleteFile(string path) => files.Remove(path);
    }
}
