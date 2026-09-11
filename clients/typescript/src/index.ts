export { SpatialClient } from "./client.ts";
export type {
  AxisOrientation, BboxDto, BeginTransactionResponse, BufferRequest, CatalogueResponse,
  CreateDatasetRequest, CreateDatasetResponse, CrsAxis, CrsDescription, CrsEllipsoid, CrsKind,
  DatasetDescription, DatasetSummary, DescribeRequest, ErrorResponse, FeatureBatchesResponse,
  FeatureQueryRequest, FeatureWriteRequest, FeatureWriteResponse, GeometryResponse,
  IntersectionRequest, ScanRequest, SimplifyRequest, SleepRequest, SleepResponse,
  TransactionRequest, TransactionResponse, TransformRequest, ValidateRequest, ValidateResponse,
} from "./generated-types.ts";
export * from "./feature-batch.ts";
export { SpatialApiError } from "./errors.ts";
