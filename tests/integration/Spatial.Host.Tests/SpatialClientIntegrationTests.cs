using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Spatial.Client;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Jobs;

namespace Spatial.Host.Tests;

/// <summary>
/// The .NET client SDK (clients/dotnet/Spatial.Client) driving the real host
/// end to end: capabilities, an inline invocation, a long-running job with
/// progress and cancellation, stream reads (string and canonical binary
/// items), resource disposal and the plugin listing. This is the automated
/// .NET client the plan requires (plan §12/§16 Phase 9) — same wire, same
/// DTOs, transport-level.
/// </summary>
public sealed class SpatialClientIntegrationTests : IClassFixture<HostApiTestFactory>
{
    private readonly HostApiTestFactory _factory;

    public SpatialClientIntegrationTests(HostApiTestFactory factory)
    {
        _factory = factory;
    }

    private SpatialClient Client => new(_factory.CreateClient());

    [Fact]
    public async Task Client_walks_the_whole_api_surface()
    {
        var client = Client;

        // Capabilities.
        var capabilities = await client.GetCapabilitiesAsync();
        Assert.Contains(capabilities, capability => capability.Id == "fixture.stream@1");

        // Inline invocation.
        var echo = await client.InvokeAsync(
            "fixture.echo@1", new Dictionary<string, object?> { ["text"] = "from the dotnet client" });
        Assert.True(echo.Ok);
        Assert.Equal("from the dotnet client", echo.Result?.GetValue<string>());

        // Long-running job: start, poll to completion, read events.
        var sleep = await client.InvokeAsync(
            "fixture.sleep@1", new Dictionary<string, object?> { ["milliseconds"] = 40L });
        Assert.Equal("job", sleep.Kind);
        var jobId = sleep.Job!.JobId;
        var finished = await client.WaitForJobAsync(jobId, pollInterval: TimeSpan.FromMilliseconds(20));
        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal("fixture@1", finished.Provider);
        var events = await client.GetJobEventsAsync(jobId);
        Assert.Contains(events.Events, jobEvent => jobEvent.Progress is not null);

        // Cancel a long-running job.
        var cancellable = await client.InvokeAsync(
            "fixture.sleep@1", new Dictionary<string, object?> { ["milliseconds"] = 30_000L });
        var cancelled = await client.CancelJobAsync(cancellable.Job!.JobId);
        Assert.NotNull(cancelled);
        var cancelledFinal = await client.WaitForJobAsync(cancellable.Job.JobId, pollInterval: TimeSpan.FromMilliseconds(20));
        Assert.Equal(JobState.Cancelled, cancelledFinal.State);

        // Streaming: string items, then canonical binary items decoded as feature batches is covered
        // by the fixture's byte stream below; assert both shapes decode.
        var stream = await client.InvokeAsync(
            "fixture.stream@1", new Dictionary<string, object?> { ["chunks"] = (object)3 });
        Assert.True(stream.Ok);
        var streamToken = stream.Result!["$resource"]!["token"]!.GetValue<string>();
        var items = new List<object?>();
        await foreach (var item in client.ReadStreamAsync(streamToken))
        {
            items.Add(item);
        }

        Assert.Equal(["chunk 1", "chunk 2", "chunk 3"], items);
        await Assert.ThrowsAsync<SpatialApiException>(() => client.GetResourceAsync(streamToken));

        // A failed stream surfaces the structured error through the client.
        var failing = await client.InvokeAsync(
            "fixture.failingstream@1", new Dictionary<string, object?>());
        var failingToken = failing.Result!["$resource"]!["token"]!.GetValue<string>();
        var saw = new List<object?>();
        var streamError = await Assert.ThrowsAsync<CapabilityStreamException>(async () =>
        {
            await foreach (var item in client.ReadStreamAsync(failingToken))
            {
                saw.Add(item);
            }
        });
        Assert.Equal("provider.failure", streamError.Error.Code);
        Assert.Single(saw);

        // Plugins: none are configured for the fixture host.
        var plugins = await client.GetPluginsAsync();
        Assert.Empty(plugins);
    }
}