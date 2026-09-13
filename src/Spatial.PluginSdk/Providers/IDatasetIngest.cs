using Spatial.Core.Features;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// How an ingested dataset keys its features (ADR-0041).
/// </summary>
public enum IngestIdentity
{
    /// <summary>
    /// No identity column: the dataset is load-only/query-only and cannot be
    /// edited (ADR-0037) or read by identity (ADR-0038).
    /// </summary>
    None,

    /// <summary>
    /// The store assigns an integer identity for every loaded feature, so the
    /// dataset is editable and lookup-able without the source supplying a key.
    /// </summary>
    Auto,

    /// <summary>
    /// <see cref="IngestRequest.IdentityField"/> is the identity: an integer
    /// field present in the source schema.
    /// </summary>
    Source,
}

/// <summary>
/// One ingest: create a dataset described by the first page's schema and load
/// every page into it. <see cref="Srid"/> is the CRS of the geometry column;
/// the pages must be non-empty and share one schema, with geometry fields
/// carried as core values (ADR-0020) — the decoding of a foreign file into
/// these pages happens at the adapter edge (ADR-0041).
/// </summary>
public sealed record IngestRequest(
    string Dataset,
    int Srid,
    IngestIdentity Identity = IngestIdentity.Auto,
    string? IdentityField = null);

/// <summary>
/// The result of a successful ingest: the created dataset, how many features
/// landed, and the identity column when one exists.
/// </summary>
public sealed record IngestOutcome(
    string Dataset,
    long Features,
    int Srid,
    string? IdentityField = null,
    Map? Map = null)
{
    public override string ToString() =>
        Map is null
            ? $"{Dataset}: {Features} feature(s)"
            : $"{Dataset}: {Features} feature(s), published as {Map.Name}";
}

/// <summary>
/// Optional bulk create-and-load capability (ADR-0041), the ingest sibling of
/// <see cref="IDataCatalogue.CreateAsync"/>. A store that can create a table
/// and load it implements it so the whole operation is one transaction: the
/// dataset exists only if every page lands, which the non-atomic
/// <c>CreateAsync</c> + <see cref="IFeatureStore.WriteAsync"/> pair cannot
/// guarantee. Additive like <see cref="IFeatureEditStore"/>: a provider that
/// cannot bulk-load simply does not implement it, and the host reports the
/// store as ingest-incapable.
/// </summary>
public interface IDatasetIngest
{
    /// <summary>
    /// Creates <see cref="IngestRequest.Dataset"/> from the pages' schema and
    /// loads every feature in one transaction. A failure leaves no dataset
    /// behind. Cancellation is honoured before the transaction commits.
    /// </summary>
    Task<IngestOutcome> IngestAsync(
        IngestRequest request, IReadOnlyList<FeatureBatch> pages, CancellationToken cancellationToken = default);
}
