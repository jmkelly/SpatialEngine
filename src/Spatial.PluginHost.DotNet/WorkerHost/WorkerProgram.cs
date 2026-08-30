using Spatial.PluginHost.DotNet.WorkerHost;

namespace Spatial.PluginHost.DotNet;

/// <summary>
/// The worker host executable entry: <c>Spatial.PluginHost.DotNet --package &lt;dir&gt;</c>
/// loads one plugin package, validates its manifest and provider surface and
/// then serves the wire protocol over stdin/stdout until the supervisor
/// drains it. Exits 0 after a clean drain; exits 1 with a diagnostic on
/// startup failure (a crash inside an invocation kills the process, so the
/// supervisor can restart with full isolation — that is the fault model).
/// </summary>
public static class WorkerProgram
{
    public static int Main(string[] args)
    {
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"spatial worker failed to start: {exception}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var packageDirectory = ParseArgument(args, "--package");
        if (packageDirectory is null)
        {
            Console.Error.WriteLine("usage: Spatial.PluginHost.DotNet --package <plugin-package-directory>");
            return 2;
        }

        using var surface = PluginPackageLoader.Load(packageDirectory);
        var host = PluginWorkerHost.Start(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            surface.Manifest,
            surface.Provider);
        await host.RunAsync();
        await host.DisposeAsync();
        return 0;
    }

    private static string? ParseArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
