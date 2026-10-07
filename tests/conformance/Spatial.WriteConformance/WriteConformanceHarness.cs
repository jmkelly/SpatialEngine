using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.WriteConformance;

/// <summary>
/// The faces one writable store exposes to the write conformance suite
/// (ADR-0037, ADR-0041, ADR-0065). Each face is its own additive contract, so
/// a store that cannot bulk-load or hold blobs simply supplies the faces it
/// has; the suite exercises the ones that are present.
///
/// <para>
/// <see cref="SchemaName"/> is the store's own schema part (PostGIS
/// <c>public</c>, memory <c>memory</c>, SQL Server <c>dbo</c>), and the suite
/// builds every dataset name under it, so two suites sharing one store never
/// collide. <see cref="AttachmentsWithCap"/> is the same attachment face over a
/// store with a smaller per-attachment cap, which is how the over-quota rule is
/// measured without a ten-megabyte payload.
/// </para>
/// </summary>
public sealed record WriteConformanceHarness(
    string SchemaName,
    IDataCatalogue Catalogue,
    IFeatureStore Store,
    IFeatureEditStore Editor,
    IFeatureAttachmentStore Attachments,
    IDatasetIngest Ingest,
    Func<long, IFeatureAttachmentStore> AttachmentsWithCap);
