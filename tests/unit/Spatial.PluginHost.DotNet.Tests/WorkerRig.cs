using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Spatial.Plugin.Fixtures;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginHost.DotNet.WorkerHost;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// A live worker-process test rig: writes a plugin package (manifest +
/// assembly and its contracts) into a temp directory, spawns the worker host
/// apphost against it and speaks the wire protocol through a
/// <see cref="WorkerChannel"/>. Every <c>WorkerProcessTests</c> case drives a
/// real child process, so the process boundary, handshake, isolation and exit
/// behaviour are exercised for real.
/// </summary>
internal sealed class WorkerRig : IAsyncDisposable
{
    private readonly string _packageDirectory;
    private readonly Process _process;
    private readonly StringBuilder _stderr;
    private readonly ConcurrentQueue<ProgressSample> _progress = new();
    private readonly TaskCompletionSource<HelloDocument> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stderrDrained;

    private WorkerRig(string packageDirectory, Process process, StringBuilder stderr, Task stderrDrained)
    {
        _packageDirectory = packageDirectory;
        _process = process;
        _stderr = stderr;
        _stderrDrained = stderrDrained;
    }

    /// <summary>The wire channel to the worker process (stdout in, stdin out).</summary>
    public WorkerChannel Channel { get; private set; } = null!;

    public Task<HelloDocument> Hello => _hello.Task;

    /// <summary>Completes when the worker answers <c>closed</c> during a drain.</summary>
    public Task<int> Closed => _closed.Task;

    public IReadOnlyList<ProgressSample> Progress => _progress.ToArray();

    public Task<int> ExitCode => _process.WaitForExitAsync().ContinueWith(_ => _process.ExitCode);

    public string StandardError => _stderr.ToString();

    /// <summary>Completes when the process's stderr pipe has been fully drained.</summary>
    public Task StderrDrained => _stderrDrained;

    public static WorkerRig Start(PluginManifest manifest)
    {
        var packageRoot = Directory.CreateTempSubdirectory("spatial-package-").FullName;
        var packageDirectory = PackageWriter.Write(packageRoot, manifest);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = WorkerHostExecutable,
                Arguments = $"--package \"{packageDirectory}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();

        var stderr = new StringBuilder();
        var stderrDrained = ReadStderrAsync(process, stderr);
        var rig = new WorkerRig(packageDirectory, process, stderr, stderrDrained);
        rig.Channel = new WorkerChannel(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            rig.OnEnvelopeAsync,
            $"rig:{Path.GetFileName(packageDirectory)}");
        return rig;
    }

    /// <summary>Sends one capability invocation and waits for its terminal result.</summary>
    public async Task<WorkerOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?>? arguments = null,
        DateTimeOffset? deadline = null,
        TimeSpan? timeout = null)
    {
        var payload = WorkerPayload.Invoke(capability.ToString(), arguments ?? new Dictionary<string, object?>(), [], deadline);
        var response = await Channel.RequestAsync(WorkerProtocol.Invoke, payload, WorkerProtocol.Result, timeout);
        return WorkerPayload.ReadOutcome(response);
    }

    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        var exitTask = _process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout));
        if (completed != exitTask)
        {
            _process.Kill(entireProcessTree: true);
            throw new TimeoutException($"the worker process did not exit within {timeout}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            await Channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            _process.Dispose();
            try
            {
                Directory.Delete(Path.GetDirectoryName(_packageDirectory)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task ReadStderrAsync(Process process, StringBuilder stderr)
    {
        var reader = new StreamReader(process.StandardError.BaseStream, Encoding.UTF8, leaveOpen: false);
        while (await reader.ReadLineAsync() is { } line)
        {
            stderr.AppendLine(line);
        }
    }

    private async ValueTask OnEnvelopeAsync(WorkerEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case WorkerProtocol.Hello:
                _hello.TrySetResult(WorkerPayload.ReadHello(envelope.Payload));
                break;
            case WorkerProtocol.Progress:
                _progress.Enqueue(ReadProgress(envelope.Payload));
                break;
            case WorkerProtocol.Closed:
                _closed.TrySetResult(envelope.Payload?["inFlight"]?.GetValue<int>() ?? -1);
                break;
            default:
                break;
        }

        await ValueTask.CompletedTask;
    }

    private static ProgressSample ReadProgress(JsonNode? payload)
    {
        if (payload is not JsonObject obj)
        {
            return new ProgressSample(null, null);
        }

        return new ProgressSample(
            obj["fraction"] is JsonValue fraction ? fraction.GetValue<double>() : null,
            obj["message"] is JsonValue message ? message.GetValue<string>() : null);
    }

    private static string WorkerHostExecutable =>
        Path.Combine(
            Path.GetDirectoryName(typeof(PluginWorkerHost).Assembly.Location)!,
            OperatingSystem.IsWindows() ? "Spatial.PluginHost.DotNet.exe" : "Spatial.PluginHost.DotNet");

    public readonly record struct ProgressSample(double? Fraction, string? Message);
}
