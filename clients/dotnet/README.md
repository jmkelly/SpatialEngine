# Spatial.Client — .NET SDK

The .NET client SDK for the public spatial host API (plan §12, Epic H): a
thin `HttpClient` wrapper for automation and service clients, speaking the
same versioned contracts (`Spatial.PluginSdk.Http`) and the shared value
codec (`Spatial.PluginSdk.Codec`) the host and the TypeScript SDK use.

## Use

```csharp
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5199") };
var client = new SpatialClient(http);

var capabilities = await client.GetCapabilitiesAsync();
var invocation = await client.InvokeAsync("spatial.geometry.buffer@1",
    new Dictionary<string, object?> { ["geometry"] = encodedGeometry, ["distance"] = 1.5 });
if (invocation.Ok) { /* invocation.Result is the wire-encoded value */ }

// Streams: string/metadata items and canonical feature batches.
await foreach (var item in client.ReadStreamAsync(token)) { }
await foreach (var batch in client.ReadFeatureBatchesAsync(token)) { }

// Jobs: start (wait: false returns the started job), poll, cancel, observe events.
var started = await client.InvokeAsync(
    new InvocationRequest("fixture.sleep@1", Arguments: ... , Wait: false));
var finished = await client.WaitForJobAsync(started.Job!.JobId);
```

The project lives outside `src/` (the plan's repo layout); it is exercised
and quality-gated through `tests/unit/Spatial.Client.Tests` (stub-handler
unit tests) and `tests/integration/Spatial.Host.Tests`
(`SpatialClientIntegrationTests`, against the real host). It is not part of
the solution — build it via the test projects or `dotnet build
clients/dotnet/Spatial.Client`.