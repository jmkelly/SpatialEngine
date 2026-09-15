using Spatial.Client;
using Spatial.Contracts.Providers;

namespace Spatial.Cli;

/// <summary>The host readiness report read by <c>host health</c>.</summary>
public sealed record HostHealth(string Status, IReadOnlyList<string> Stores);

/// <summary>
/// The CLI's view of the public host API (ADR-0052): exactly the operations
/// the commands need, over core/SDK types. Implemented by
/// <see cref="HttpSpatialGateway"/> against the .NET client SDK and faked in
/// unit tests, so command logic never depends on HTTP.
/// </summary>
public interface ISpatialGateway : IDisposable
{
    /// <summary>Reads <c>GET /health/ready</c>.</summary>
    Task<HostHealth> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists spatial datasets in a store, optionally filtered by a LIKE pattern.</summary>
    Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string store, string? pattern, CancellationToken cancellationToken = default);

    /// <summary>Describes one dataset.</summary>
    Task<DatasetDescription> DescribeDatasetAsync(string dataset, string store, CancellationToken cancellationToken = default);

    /// <summary>Ingests a local file or URL into a dataset (requires the admin token).</summary>
    Task<IngestOutcome> IngestAsync(string source, IngestUpload upload, string? adminToken, CancellationToken cancellationToken = default);

    /// <summary>Lists every map.</summary>
    Task<IReadOnlyList<Map>> ListMapsAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds a map by name, or <see langword="null"/> when it does not exist.</summary>
    Task<Map?> FindMapAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a runtime map (requires the admin token).</summary>
    Task<Map> PutMapAsync(Map map, string? adminToken, CancellationToken cancellationToken = default);

    /// <summary>Deletes a runtime map and reports whether it existed (requires the admin token).</summary>
    Task<bool> DeleteMapAsync(string name, string? adminToken, CancellationToken cancellationToken = default);
}
