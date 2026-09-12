export { SpatialClient } from "./client.ts";
export type { IngestResult, RasterImage } from "./client.ts";
export type {
  AxisOrientation, BboxDto, BeginTransactionResponse, BufferRequest, CatalogueResponse,
  CreateDatasetRequest, CreateDatasetResponse, CrsAxis, CrsDescription, CrsEllipsoid, CrsKind,
  DatasetDescription, DatasetSummary, DescribeRequest, ErrorResponse, FeatureBatchesResponse,
  FeatureQueryRequest, FeatureWriteRequest, FeatureWriteResponse, GeometryResponse,
  ImagerySourceDto, IntersectionRequest, JsonElement, Publication, PublicationKind,
  PublicationLayer, RasterBlend, RasterFormat, RenderCapabilitiesResponse, RenderImageryDto,
  RenderLayerDto, RenderRequest, ScanRequest, SimplifyRequest, SleepRequest, SleepResponse,
  TileBatchRequest, TileBatchResponse, TileCapabilitiesResponse, TileDto, TileLevelDto,
  TileRenderRequest, TileResultDto, TileSchemeDto,
  TransactionRequest, TransactionResponse, TransformRequest, ValidateRequest, ValidateResponse,
  ViewportDto,
} from "./generated-types.ts";
export * from "./feature-batch.ts";
export { SpatialApiError } from "./errors.ts";
