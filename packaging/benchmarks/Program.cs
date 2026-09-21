using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TaskTokenMeter.Cli.Hooks;

const int LineCount = 100_000;
const int DefaultWarmRuns = 30;
const int UsageRecordCount = 25_000;
const long TargetBytes = 21L * 1024 * 1024;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: task-token-meter-performance-probe <absolute-task-token-meter-executable>");
    return 2;
}

var executable = Path.GetFullPath(args[0]);
var warmRuns = int.TryParse(Environment.GetEnvironmentVariable("TASK_TOKEN_METER_PERFORMANCE_RUNS"), NumberStyles.None, CultureInfo.InvariantCulture, out var configuredRuns) && configuredRuns > 0
    ? configuredRuns : DefaultWarmRuns;
var root = Path.Combine(Path.GetTempPath(), "task-token-meter-performance", Guid.NewGuid().ToString("N"));
var sourceRoot = Path.Combine(root, "source");
var workspace = Path.Combine(root, "workspace");
var sourcePath = Path.Combine(sourceRoot, "performance.jsonl");
Directory.CreateDirectory(sourceRoot);
Directory.CreateDirectory(workspace);

try
{
    await GenerateFixtureAsync(sourcePath);
    var fileInfo = new FileInfo(sourcePath);
    var parsedLineCount = File.ReadLines(sourcePath).Count();
    if (fileInfo.Length != TargetBytes || parsedLineCount != LineCount)
    {
        throw new InvalidOperationException($"Fixture shape mismatch: {fileInfo.Length} bytes / {parsedLineCount} lines.");
    }

    var cold = await MeasureCliAsync(executable, sourceRoot, workspace, UsageRecordCount);
    var warm = new List<ProcessMeasurement>(warmRuns);
    for (var index = 0; index < warmRuns; index++)
    {
        warm.Add(await MeasureCliAsync(executable, sourceRoot, workspace, UsageRecordCount));
    }

    var hookSource = Path.Combine(sourceRoot, "hook.jsonl");
    await File.WriteAllTextAsync(hookSource,
        "{\"timestamp\":\"2026-02-01T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\",\"session_id\":\"hook-session\",\"thread_id\":\"hook-thread\",\"turn_id\":\"hook-turn\",\"root_turn_id\":\"hook-turn\",\"response_id\":\"hook-response\",\"usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0,\"total_tokens\":1,\"cache_write_input_tokens\":0}}}\n",
        new UTF8Encoding(false));
    var hook = await MeasureHookAcceptanceAsync(root, workspace, sourceRoot, hookSource, warmRuns);

    var elapsed = warm.Select(static item => item.ElapsedMilliseconds).Order().ToArray();
    var rss = warm.Select(static item => item.PeakWorkingSetBytes).Order().ToArray();
    var result = new
    {
        schemaVersion = 1,
        fixture = new { lines = LineCount, usageRecords = UsageRecordCount, noOpRecords = LineCount - UsageRecordCount, bytes = fileInfo.Length, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))) },
        environment = new
        {
            os = Environment.OSVersion.VersionString,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount
        },
        cli = new
        {
            cold,
            warmRuns,
            p50ElapsedMilliseconds = Percentile(elapsed, 0.50),
            p95ElapsedMilliseconds = Percentile(elapsed, 0.95),
            maxElapsedMilliseconds = elapsed[^1],
            p50PeakRssBytes = Percentile(rss, 0.50),
            p95PeakRssBytes = Percentile(rss, 0.95),
            maxPeakRssBytes = rss[^1],
            elapsedTargetMilliseconds = 1000,
            peakRssTargetBytes = 256L * 1024 * 1024,
            passed = Percentile(elapsed, 0.95) <= 1000 && rss[^1] <= 256L * 1024 * 1024
        },
        hook
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static async Task GenerateFixtureAsync(string path)
{
    var encoding = new UTF8Encoding(false);
    long usageBytes = 0;
    for (var index = 0; index < UsageRecordCount; index++)
    {
        usageBytes += encoding.GetByteCount(UsageLine(index));
    }

    var noOpCount = LineCount - UsageRecordCount;
    const string noOpPrefix = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"synthetic_noop\",\"padding\":\"";
    const string noOpSuffix = "\"}}\n";
    var noOpBaseBytes = encoding.GetByteCount(noOpPrefix + noOpSuffix);
    var paddingBytes = TargetBytes - usageBytes - ((long)noOpBaseBytes * noOpCount);
    if (paddingBytes < 0) throw new InvalidOperationException("Usage records exceed the fixture byte target.");
    var paddingPerLine = checked((int)(paddingBytes / noOpCount));
    var extraPaddingLines = checked((int)(paddingBytes % noOpCount));

    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
    await using var writer = new StreamWriter(stream, encoding, 1024 * 1024);
    for (var index = 0; index < UsageRecordCount; index++)
    {
        await writer.WriteAsync(UsageLine(index));
    }

    for (var index = 0; index < noOpCount; index++)
    {
        await writer.WriteAsync(noOpPrefix);
        await writer.WriteAsync(new string('x', paddingPerLine + (index < extraPaddingLines ? 1 : 0)));
        await writer.WriteAsync(noOpSuffix);
    }
}

static string UsageLine(int index)
{
    var cumulative = (index + 1).ToString(CultureInfo.InvariantCulture);
    const string zeroFields = ",\"cached_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0,\"cache_write_input_tokens\":0,\"total_tokens\":";
    return "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_usage_record\",\"session_id\":\"s\",\"thread_id\":\"t\",\"turn_id\":\"u\",\"root_turn_id\":\"u\",\"response_id\":\"r" +
        index.ToString("D5", CultureInfo.InvariantCulture) +
        "\",\"usage\":{\"input_tokens\":1" + zeroFields + "1},\"turn_token_usage\":{\"input_tokens\":" + cumulative +
        zeroFields + cumulative + "},\"thread_token_usage\":{\"input_tokens\":" + cumulative + zeroFields + cumulative + "}}}\n";
}static async Task<ProcessMeasurement> MeasureCliAsync(string executable, string sourceRoot, string workspace, int expectedCalls)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[]
    {
        "current", "--provider", "codex", "--session", "s", "--workspace", workspace,
        "--json", "--non-interactive"
    }) startInfo.ArgumentList.Add(argument);
    startInfo.Environment["TOKEN_METER_CODEX_SOURCES"] = sourceRoot;
    startInfo.Environment["TOKEN_METER_CLAUDE_SOURCES"] = Path.Combine(sourceRoot, "not-present");
    startInfo.Environment["NO_COLOR"] = "1";

    var stopwatch = Stopwatch.StartNew();
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("CLI process did not start.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    long peakWorkingSet = 0;
    while (!process.HasExited)
    {
        process.Refresh();
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        await Task.Delay(5);
    }
    await process.WaitForExitAsync();
    stopwatch.Stop();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"CLI exited {process.ExitCode}: {stderr.Trim()}");
    }

    using var document = JsonDocument.Parse(stdout);
    var turn = document.RootElement.GetProperty("turns")[0];
    var apiCalls = turn.GetProperty("apiCalls");
    var processedTokens = turn.GetProperty("processedTokens");
    if (apiCalls.ValueKind != JsonValueKind.Number || apiCalls.GetInt32() != expectedCalls ||
        processedTokens.ValueKind != JsonValueKind.Number || processedTokens.GetInt64() != expectedCalls)
    {
        throw new InvalidOperationException("CLI did not process all deterministic records: " + stdout);
    }
    return new ProcessMeasurement(stopwatch.Elapsed.TotalMilliseconds, peakWorkingSet);
}

static async Task<object> MeasureHookAcceptanceAsync(string root, string workspace, string sourceRoot, string sourcePath, int runCount)
{
    // The stub stands in for the hook worker process. rundll32.exe treats the worker arguments as a DLL
    // name, shows an error dialog and never exits, so every run leaked a process. The command processor
    // started without /c reads commands from stdin; the launcher closes stdin right after start, so it
    // exits immediately with code 0.
    var stubPath = Environment.GetEnvironmentVariable("ComSpec");
    if (string.IsNullOrEmpty(stubPath) || !File.Exists(stubPath)) throw new FileNotFoundException("The command processor stub was not found.", stubPath);
    var provider = HookProvider.Codex;
    var validator = new HookContextValidator(new Dictionary<HookProvider, IReadOnlyList<string>> { [provider] = [sourceRoot] });
    var launcher = new ProcessHookWorkerLauncher(stubPath);
    var diagnostics = new NullHookDiagnosticSink();
    var payload = JsonSerializer.Serialize(new
    {
        hook_event_name = "Stop",
        session_id = "hook-session",
        turn_id = "hook-turn",
        cwd = workspace,
        transcript_path = sourcePath
    });
    var measurements = new List<double>(runCount);
    for (var index = 0; index < runCount; index++)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var stopwatch = Stopwatch.StartNew();
        var exitCode = await HookEntryPoint.RunAsync(
            provider,
            new StringReader(payload),
            output,
            validator,
            launcher,
            diagnostics,
            Path.Combine(root, "diagnostics.jsonl"));
        stopwatch.Stop();
        if (exitCode != 0 || output.ToString() != "{}") throw new InvalidOperationException("Hook acceptance contract failed.");
        measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
    }

    var ordered = measurements.Order().ToArray();
    return new
    {
        runs = runCount,
        stubProcess = Path.GetFileName(stubPath),
        p50ElapsedMilliseconds = Percentile(ordered, 0.50),
        p95ElapsedMilliseconds = Percentile(ordered, 0.95),
        maxElapsedMilliseconds = ordered[^1],
        targetMilliseconds = 250,
        passed = Percentile(ordered, 0.95) <= 250
    };
}

static double Percentile<T>(IReadOnlyList<T> ordered, double percentile) where T : IConvertible
{
    var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Count));
    return Convert.ToDouble(ordered[rank - 1], CultureInfo.InvariantCulture);
}

internal sealed record ProcessMeasurement(double ElapsedMilliseconds, long PeakWorkingSetBytes);
