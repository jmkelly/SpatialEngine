# Spatial.Client — .NET SDK

The .NET client SDK for the typed spatial host API (ADR-0033): a thin
`HttpClient` wrapper for automation and service clients, speaking the same
typed routes (`Spatial.PluginSdk.Http`) the host and the TypeScript SDK use.

## Use

```csharp
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5199") };
var client = new SpatialClient(http);

var buffered = await client.BufferAsync(GeometryFactory.CreatePoint(0, 0), 1.5);
var valid = await client.ValidateAsync(buffered);

var datasets = await client.ListCatalogueAsync();
var batches = await client.ScanAsync("demo.points");
var count = batches.Sum(batch => batch.Features.Count);

var description = await client.DescribeAsync("EPSG:4326");
var moved = await client.TransformAsync(buffered, source: null, target: "EPSG:32632");
```

Failures throw `SpatialClientException` with the HTTP status and the
structured error code (`invalid.arguments`, `not.found`,
`store.unavailable`).

The project lives outside `src/` (the plan's repo layout); it is exercised
and quality-gated through `tests/unit/Spatial.Client.Tests` (stub-handler
unit tests) and `tests/integration/Spatial.Host.Tests` (against the real
host). It is not part of the solution — build it via the test projects or
`dotnet build clients/dotnet/Spatial.Client`.
