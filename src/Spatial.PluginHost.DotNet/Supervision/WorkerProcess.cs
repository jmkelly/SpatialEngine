using System.Diagnostics;
using System.Text;
using Spatial.PluginHost.DotNet.Protocol;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// One supervised worker *process*: spawns the worker host apphost against a
/// package directory, wires its stdin/stdout into a <see cref="WorkerChannel"/>
/// and captures stderr as diagnostics. The supervisor owns the channel handler
/// (handshake, progress, drain answers); the process's exit, crash or EOF
/// surfaces through <see cref="Channel"/>.Disconnected and
/// <see cref="WaitForExitAsync"/>.
/// </summary>
public sealed class WorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrDrained;

    private WorkerProcess(Process process, WorkerChannel channel, StringBuilder stderr, Task stderrDrained)
    {
        _process = process;
        Channel = channel;
        _stderr = stderr;
        _stderrDrained = stderrDrained;
    }

    /// <summary>The wire channel to the worker (its stdout in, its stdin out).</summary>
    public WorkerChannel Channel { get; }

    public int ProcessId => _process.Id;

    public string StandardError => _stderr.ToString();

    /// <summary>Completes when the process has exited; the value is its exit code.</summary>
    public Task<int> WaitForExitAsync() => _process.WaitForExitAsync().ContinueWith(_ => _process.ExitCode);

    /// <summary>Completes when the process's stderr has been fully drained.</summary>
    public Task StderrDrained => _stderrDrained;

    /// <summary>
    /// Spawns the worker host executable with <c>--package &lt;dir&gt;</c> and
    /// starts the channel. Throws <see cref="InvalidOperationException"/> when
    /// the executable cannot be launched (missing apphost — diagnostics).
    /// </summary>
    public static WorkerProcess Start(
        string workerExecutable,
        string packageDirectory,
        string name,
        Func<WorkerEnvelope, ValueTask> onMessage)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerExecutable,
                Arguments = $"--package \"{packageDirectory}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"cannot launch the worker host '{workerExecutable}': {exception.Message} "
                + "(the executable must be built and deployed next to the supervisor)", exception);
        }

        var stderr = new StringBuilder();
        var stderrDrained = ReadStderrAsync(process, stderr);
        var channel = new WorkerChannel(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            onMessage,
            name);
        return new WorkerProcess(process, channel, stderr, stderrDrained);
    }

    /// <summary>Stops the worker: close the channel, then wait for a graceful exit before killing.</summary>
    public async ValueTask StopAsync(TimeSpan grace)
    {
        await Channel.DisposeAsync().AsTask().WaitAsync(grace);
        try
        {
            await WaitForExitAsync().WaitAsync(grace);
        }
        catch (TimeoutException)
        {
            _process.Kill(entireProcessTree: true);
            try
            {
                await WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }
        }

        await StderrDrained.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.FromSeconds(2));

    private static async Task ReadStderrAsync(Process process, StringBuilder stderr)
    {
        var reader = new StreamReader(process.StandardError.BaseStream, Encoding.UTF8, leaveOpen: false);
        while (await reader.ReadLineAsync() is { } line)
        {
            stderr.AppendLine(line);
        }
    }
}
