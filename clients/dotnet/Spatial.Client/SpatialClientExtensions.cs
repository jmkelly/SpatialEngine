using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Jobs;

namespace Spatial.Client;

/// <summary>
/// Convenience extensions over <see cref="SpatialClient"/>: the raw-value
/// invocation shape and job polling. Kept as stateless extensions so the
/// client class stays cohesive (metrics god-class rule).
/// </summary>
public static class SpatialClientExtensions
{
    /// <summary>A convenience for the common inline shape: codec-encoded wire values as arguments.</summary>
    public static Task<InvocationResponse> InvokeAsync(
        this SpatialClient client,
        string capability,
        IReadOnlyDictionary<string, object?> arguments,
        IReadOnlySet<string>? permissions = null,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var encoded = arguments.ToDictionary(
            entry => entry.Key,
            entry => ValueCodec.Encode(entry.Value),
            StringComparer.Ordinal);
        return client.InvokeAsync(
            new InvocationRequest(
                capability,
                Arguments: encoded,
                Permissions: permissions?.ToArray(),
                Provider: provider),
            cancellationToken);
    }

    /// <summary>
    /// Polls a job until it reaches a terminal state and returns its final
    /// snapshot. The poll interval starts at 50 ms and backs off to 500 ms.
    /// </summary>
    public static async Task<JobResponse> WaitForJobAsync(
        this SpatialClient client,
        string jobId,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        while (true)
        {
            var job = await client.GetJobAsync(jobId, cancellationToken);
            if (IsTerminal(job.State))
            {
                return job;
            }

            await Task.Delay(interval, cancellationToken);
            if (interval < TimeSpan.FromMilliseconds(500))
            {
                interval += TimeSpan.FromMilliseconds(50);
            }
        }
    }

    private static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.TimedOut;
}
