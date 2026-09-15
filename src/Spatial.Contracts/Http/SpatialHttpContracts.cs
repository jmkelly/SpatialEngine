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

public sealed record ErrorResponse(string Code, string Message);
