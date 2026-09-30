using Spatial.Core.Features;
using Spatial.Core.Features.Ingest;

namespace Spatial.Contracts.Providers;

/// <summary>
/// How an ingested dataset keys its features (ADR-0041). Every ingested
/// dataset carries an identity column, so every ingested dataset is editable
/// and lookup-able; the keyless mode ADR-0041 listed as <c>None</c> was
/// removed by ADR-0149 rather than left as a request that builds a dataset
/// which cannot name its features (ADR-0140).
/// </summary>
public enum IngestIdentity
{
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
/// landed, and the identity column it is keyed by (ADR-0149 — an ingest
/// always keys its dataset, so this is always reported).
/// </newText>
public sealed record IngestOutcome(
    string Dataset,
    long Features,
    int Srid,
    string? IdentityField = null,
    Map? Map = null,
    DecodeReport? Report = null)
{
    public override string ToString() =>
        Map is null
            ? $"{Dataset}: {Features} feature(s)"
            : $"{Dataset}: {Features} feature(s), published as {Map.Name}";
}

/// <summary>
/// Optional bulk create-and-load face (ADR-0041), the ingest sibling of
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

/// <summary>
/// The streaming sibling of <see cref="IDatasetIngest"/> (ADR-0041 §4): the
/// same atomic create-and-load, but pages arrive as an
/// <see cref="IAsyncEnumerable{T}"/> and the schema is declared up front so the
/// table can be created before the first page is read.
/// <para>
/// It is a separate face, not an overload, for the ADR-0033 reason every other
/// optional capability is: a store implements the one it can. The buffered
/// face is still the right one for an upload already in memory, and a provider
/// that cannot stream pages simply does not implement this.
/// </para>
/// <para>
/// The schema must match every page's, exactly as for the buffered face, and
/// the pages must be non-empty. A store that begins a transaction, reads pages
/// and fails part-way leaves no dataset behind, so an exception out of
/// <paramref name="pages"/> is a rollback rather than a half-populated table.
/// </para>
/// </summary>
public interface IDatasetIngestStream
{
    /// <summary>
    /// Creates <see cref="IngestRequest.Dataset"/> from
    /// <paramref name="schema"/> and loads every page in one transaction. A
    /// failure — including a cancellation while the pages are still arriving —
    /// leaves no dataset behind. Cancellation is honoured before the
    /// transaction commits.
    /// </summary>
    Task<IngestOutcome> IngestStreamAsync(
        IngestRequest request,
        FeatureSchema schema,
        IAsyncEnumerable<FeatureBatch> pages,
        CancellationToken cancellationToken = default);
}
