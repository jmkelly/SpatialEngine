using Spatial.Cli;
using Spatial.Client;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>
/// A scriptable <see cref="ISpatialGateway"/> so command behaviour is tested
/// without a host: records calls and returns configured values (ADR-0052).
/// </summary>
public sealed class FakeSpatialGateway : ISpatialGateway
{
    public HostHealth Health { get; set; } = new("ready", ["demo", "memory", "postgis"]);

    public IReadOnlyList<DatasetSummary> Datasets { get; set; } = [];

    public DatasetDescription? Description { get; set; }

    public IReadOnlyList<Map> Maps { get; set; } = [];

    public IReadOnlyDictionary<string, Map> MapsByName { get; set; } =
        new Dictionary<string, Map>(StringComparer.Ordinal);

    public Exception? Failure { get; set; }

    public List<(string Source, IngestUpload Upload, string? Token)> IngestCalls { get; } = [];

    public List<(Map Map, string? Token)> PutCalls { get; } = [];

    public List<string> DeletedMaps { get; } = [];

    public Task<HostHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        return Task.FromResult(Health);
    }

    public Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string store, string? pattern, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        return Task.FromResult(Datasets);
    }

    public Task<DatasetDescription> DescribeDatasetAsync(string dataset, string store, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        return Task.FromResult(Description ?? throw new InvalidOperationException("No description configured."));
    }

    public Task<IngestOutcome> IngestAsync(string source, IngestUpload upload, string? adminToken, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        IngestCalls.Add((source, upload, adminToken));
        return Task.FromResult(new IngestOutcome(upload.Dataset, 3, upload.Srid, "id"));
    }

    public Task<IReadOnlyList<Map>> ListMapsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        return Task.FromResult(Maps);
    }

    public Task<Map?> FindMapAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        return Task.FromResult(MapsByName.GetValueOrDefault(name));
    }

    public Task<Map> PutMapAsync(Map map, string? adminToken, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        PutCalls.Add((map, adminToken));
        return Task.FromResult(map);
    }

    public Task<bool> DeleteMapAsync(string name, string? adminToken, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        DeletedMaps.Add(name);
        return Task.FromResult(true);
    }

    public void Dispose()
    {
    }

    private void ThrowIfConfigured()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
