using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskTokenMeter.Cli.Hooks;

public interface IHookDiagnosticSink
{
    void Record(string code, HookProvider provider, string? eventName, TimeSpan duration);
}

public sealed class NullHookDiagnosticSink : IHookDiagnosticSink
{
    public void Record(string code, HookProvider provider, string? eventName, TimeSpan duration)
    {
    }
}

public sealed class FileHookDiagnosticSink(string path) : IHookDiagnosticSink
{
    private const long MaximumBytes = 10 * 1024 * 1024;
    private readonly string path = Path.GetFullPath(path);

    public void Record(string code, HookProvider provider, string? eventName, TimeSpan duration)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
            {
                File.Move(path, path + ".previous", overwrite: true);
            }

            var entry = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                code = SanitizeCode(code),
                provider = provider.ToCliName(),
                eventName = SanitizeCode(eventName ?? "unknown"),
                durationMs = Math.Max(0, (long)duration.TotalMilliseconds),
                occurredAt = DateTimeOffset.UtcNow
            });
            File.AppendAllText(path, entry + "\n", new UTF8Encoding(false));
        }
        catch
        {
            // Hook diagnostics must never affect the provider operation.
        }
    }

    public static string OpaqueReference(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static string SanitizeCode(string value)
    {
        var filtered = new string(value.Where(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(64).ToArray());
        return filtered.Length == 0 ? "unknown" : filtered;
    }
}
