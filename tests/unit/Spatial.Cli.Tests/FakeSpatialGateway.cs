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

    public IReadOnlyList<Publication> Publications { get; set; } = [];

    public IReadOnlyDictionary<string, Publication> PublicationsByName { get; set; } =
        new Dictionary<string, Publication>(StringComparer.Ordinal);

    public Exception? Failure { get; set; }

    public List<(string Source, IngestUpload Upload, string? Token)> IngestCalls { get; } = [];

    public List<(Publication Publication, string? Token)> PutCalls { get; } = [];

    public List<string> DeletedPublications { get; } = [];

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

    public Task<IReadOnlyList<Publication>> ListPublicationsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        return Task.FromResult(Publications);
    }

    public Task<Publication?> FindPublicationAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        return Task.FromResult(PublicationsByName.GetValueOrDefault(name));
    }

    public Task<Publication> PutPublicationAsync(Publication publication, string? adminToken, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        PutCalls.Add((publication, adminToken));
        return Task.FromResult(publication);
    }

    public Task<bool> DeletePublicationAsync(string name, string? adminToken, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        DeletedPublications.Add(name);
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
