using System.Reflection;

namespace TaskTokenMeter.Cli;

/// <summary>
/// Exposes the build version that the release workflow injects with <c>-p:Version</c>.
/// </summary>
public static class VersionInfo
{
    public const string FallbackVersion = "0.0.0-dev";

    public static string Product { get; } = "task-token-meter";

    public static string Informational { get; } = ReadInformational();

    public static string File { get; } = typeof(VersionInfo).Assembly
        .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0.0";

    private static string ReadInformational()
    {
        var value = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(value))
        {
            return FallbackVersion;
        }

        var metadata = value.IndexOf('+', StringComparison.Ordinal);
        return metadata >= 0 ? value[..metadata] : value;
    }
}
