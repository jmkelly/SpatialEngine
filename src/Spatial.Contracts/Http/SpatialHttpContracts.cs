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
public sealed record FeatureQueryRequest(string Dataset, BboxDto? Bbox = null, string? Filter = null);
public sealed record FeatureBatchesResponse(IReadOnlyList<string> Batches);
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

// ---- seed (ADR-0070) ----

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
