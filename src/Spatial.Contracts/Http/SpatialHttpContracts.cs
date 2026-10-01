using Spatial.Contracts.Providers;
using Spatial.Contracts.Transformations;

namespace Spatial.Contracts.Http;

// ---- geometry ----

public sealed record BufferRequest(string Geometry, double Distance, int? QuadrantSegments = 8);
public sealed record GeometryResponse(string Geometry);
public sealed record IntersectionRequest(string Left, string Right);
public sealed record ValidateRequest(string Geometry);
public sealed record ValidateResponse(bool Valid);
public sealed record SimplifyRequest(string Geometry, double Tolerance);

// ---- transforms ----

public sealed record DescribeRequest(string Crs);
public sealed record TransformRequest(string Geometry, string? Source, string Target);

// ---- catalogue / datasets ----

public sealed record CatalogueResponse(IReadOnlyList<DatasetSummary> Datasets);
public sealed record CreateDatasetRequest(string Dataset, string Batch, int Srid);
public sealed record CreateDatasetResponse(string Dataset);

// ---- features ----

public sealed record ScanRequest(string Dataset);
public sealed record BboxDto(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>
/// The body of <c>POST /api/features/query</c> (ADR-0158): a dataset and the
/// plan to read it with, in either of two spellings.
/// <para>
/// <paramref name="Plan"/> is the plan — ids, predicate tree, bbox,
/// projection, order, limit, offset, cursor. <paramref name="Bbox"/> and
/// <paramref name="Filter"/> are the published sugar for its bounding box and
/// its predicate, kept for one more release because clients still send them
/// (ADR-0074 §3); a request that sends both spellings of one member must
/// agree, and one that disagrees is <c>invalid.arguments</c> naming it.
/// </para>
/// </summary>
/// <param name="Dataset">The dataset to read.</param>
/// <param name="Bbox">A bounding-box pre-filter, or <c>null</c>.</param>
/// <param name="Filter">Attribute filter text, or <c>null</c>.</param>
/// <param name="Plan">The query plan, or <c>null</c> to read with the sugar alone.</param>
public sealed record FeatureQueryRequest(
    string Dataset, BboxDto? Bbox = null, string? Filter = null, FeatureQueryDto? Plan = null);

public sealed record FeatureBatchesResponse(IReadOnlyList<string> Batches);

/// <summary>
/// The answer to <c>POST /api/features/query</c> (ADR-0158 §3): one page of
/// the plan's read — the batches the plan selected, the continuation when the
/// plan has more, the total the plan matched, and the "one more" signal.
/// <para>
/// <see cref="Batches"/> is unchanged in name, shape and encoding, so every
/// client that reads the route today reads this answer unchanged; the other
/// three members are what the route used to drop.
/// </para>
/// </summary>
/// <param name="Batches">The selected features as canonical SFBAT Base64, in plan order.</param>
/// <param name="NextCursor">The continuation token, or <c>null</c> when the page is the last.</param>
/// <param name="TotalCount">The number of features the plan matched, or <c>null</c> when not computed.</param>
/// <param name="HasMore">Whether the plan has features beyond this page.</param>
public sealed record FeatureQueryResponse(
    IReadOnlyList<string> Batches, string? NextCursor = null, int? TotalCount = null, bool HasMore = false);
public sealed record FeatureWriteRequest(string Dataset, string Batch, string? Transaction = null);
public sealed record FeatureWriteResponse(int Appended);

// ---- transactions ----

public sealed record BeginTransactionResponse(string Transaction);
public sealed record TransactionRequest(string Transaction);
public sealed record TransactionResponse(bool Ok);

// ---- demo ----

public sealed record SleepRequest(long Milliseconds);
public sealed record SleepResponse(long Slept);

// ---- errors ----

// ---- auth ----

public sealed record LoginRequest(string Username, string Password);
public sealed record AuthTokenResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record AuthIdentityResponse(string Issuer, string Subject, string Username, IReadOnlyList<string> Roles);

// ---- errors ----

public sealed record ErrorResponse(string Code, string Message);

// ---- seed (ADR-0078) ----

// Mirrors tools/seed/manifest.mjs so the seed tool can POST its manifest
// verbatim: one downloadable source per dataset, one map per service.
public sealed record SeedSource(
    string Id,
    string Url,
    string Format,
    int Srid,
    int? SourceSrid = null,
    string? Identity = null,
    string? IdentityField = null);

public sealed record SeedLayerStyle(
    string? Color = null,
    double? Opacity = null,
    double? LineWidth = null,
    double? Radius = null,
    bool? Visible = null);

public sealed record SeedMapLayer(
    string Dataset,
    string? Name = null,
    string Geometry = "mixed",
    SeedLayerStyle? Style = null,
    MapLayerKind Kind = MapLayerKind.Feature);

public sealed record SeedMap(
    string Name,
    IReadOnlyList<MapServiceKind> Services,
    IReadOnlyList<SeedMapLayer> Layers,
    string? Description = null,
    string? Copyright = null);

public sealed record SeedRequest(
    IReadOnlyList<SeedSource> Sources,
    IReadOnlyList<SeedMap> Maps,
    string Store = "memory",
    bool Force = false,
    IReadOnlyList<string>? Only = null);

public sealed record SeedFailure(string Target, string Code, string Message);

public sealed record SeedResponse(
    string Store,
    int Ingested,
    int Reused,
    int Published,
    IReadOnlyList<SeedFailure> Failures);
