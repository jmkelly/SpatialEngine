using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Jobs;

namespace Spatial.Host.Tests;

/// <summary>
/// The job surface (ADR-0008): start a long-running job, poll it, read its
/// append-only events (JSON snapshot and server-sent stream), and cancel it.
/// </summary>
public sealed class HostJobTests : IClassFixture<HostApiTestFactory>
{
    private readonly HostApiTestFactory _factory;

    public HostJobTests(HostApiTestFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Job_runs_to_completion_with_progress_and_events()
    {
        var jobId = await StartSleepAsync(milliseconds: 60);

        var job = await PollUntilTerminalAsync(jobId);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("fixture.sleep@1", job.Capability);
        Assert.Equal("fixture@1", job.Provider);
        Assert.Equal("FirstHealthy", job.Step);
        Assert.NotNull(job.CompletedAt);

        var events = await GetJobEventsAsync(jobId);
        var kinds = events.Events.Select(jobEvent => jobEvent.Kind.ToString()).ToArray();
        Assert.Contains("Created", kinds);
        Assert.Contains("Started", kinds);
        Assert.Contains("Completed", kinds);
        Assert.Contains(events.Events, jobEvent => jobEvent.Progress is > 0 and <= 1);
    }

    [Fact]
    public async Task Cancel_stops_a_running_job()
    {
        var jobId = await StartSleepAsync(milliseconds: 15_000);

        var response = await Client.PostAsync($"/api/jobs/{jobId}/cancel", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await response.Content.ReadFromJsonAsync<JobResponse>(HostApiJson.Options);
        Assert.Equal("fixture.sleep@1", cancelled?.Capability);

        var job = await PollUntilTerminalAsync(jobId);
        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task Unknown_job_is_404()
    {
        var response = await Client.GetAsync("/api/jobs/00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Job_events_stream_as_server_sent_events_until_done()
    {
        var jobId = await StartSleepAsync(milliseconds: 80);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}/events");
        request.Headers.Accept.Add(new("text/event-stream"));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: created", text);
        Assert.Contains("event: started", text);
        Assert.Contains("event: progress", text);
        Assert.Contains("event: completed", text);
        Assert.Contains("event: done", text);
    }

    private async Task<string> StartSleepAsync(long milliseconds)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.sleep@1", Arguments: new Dictionary<string, JsonNode?>
            {
                ["milliseconds"] = JsonValue.Create(milliseconds),
            }),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.Equal("job", body?.Kind);
        Assert.NotNull(body?.Job);
        return body.Job.JobId;
    }

    private async Task<JobResponse> PollUntilTerminalAsync(string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await GetJobAsync(jobId);
            if (job.State is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.TimedOut)
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException($"job {jobId} did not reach a terminal state in time");
    }

    private async Task<JobResponse> GetJobAsync(string jobId)
    {
        var response = await Client.GetAsync($"/api/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobResponse>(HostApiJson.Options))!;
    }

    private async Task<JobEventsResponse> GetJobEventsAsync(string jobId)
    {
        var response = await Client.GetAsync($"/api/jobs/{jobId}/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobEventsResponse>(HostApiJson.Options))!;
    }
}