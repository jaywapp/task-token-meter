namespace TaskTokenMeter.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var console = new SystemCliConsole();
        return await CliApplication.RunAsync(args, new AdapterCliRuntime(), console).ConfigureAwait(false);
    }
}