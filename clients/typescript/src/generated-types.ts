// GENERATED FILE — do not edit by hand.
// Regenerate from the host's OpenAPI description: node scripts/generate.mjs <openapi.json> <out.ts>
// The snapshot lives at scripts/openapi.snapshot.json; scripts/check-generated.mjs fails
// when this file has drifted from it.

export type AxisOrientation = "east" | "north" | "west" | "south" | "up" | "down" | "other";

export interface BboxDto {
  minX: number | string;
  minY: number | string;
  maxX: number | string;
  maxY: number | string;
}

export interface BeginTransactionResponse {
  transaction: string;
}

export interface BufferRequest {
  geometry: string;
  distance: number | string;
  quadrantSegments?: null | number | string;
}

export interface CatalogueResponse {
  datasets: DatasetSummary[];
}

export interface CreateDatasetRequest {
  dataset: string;
  batch: string;
  srid: number | string;
}

export interface CreateDatasetResponse {
  dataset: string;
}

export interface CrsAxis {
  name: string;
  orientation: AxisOrientation;
  unitName: string;
}

export interface CrsDescription {
  authority: string;
  code: string;
  name: string;
  kind: CrsKind;
  dimension: number | string;
  axes: CrsAxis[];
  datum: null | string;
  ellipsoid: CrsEllipsoid | null;
}

export interface CrsEllipsoid {
  name: string;
  semiMajorAxis: number | string;
  semiMinorAxis: number | string;
  unitName: string;
}

export type CrsKind = "geographic" | "projected" | "geocentric" | "vertical" | "compound" | "other";

export interface DatasetDescription {
  id: string;
  schemaName: string;
  table: string;
  geometryColumn: string;
  srid: number | string;
  geometryType: string;
  estimatedRowCount: number | string;
  idColumns: string[];
  schema: FeatureSchema;
}

export interface DatasetSummary {
  id: string;
  schema: string;
  table: string;
  geometryColumn: string;
  srid: number | string;
  estimatedRowCount: number | string;
}

export interface DescribeRequest {
  crs: string;
}

export interface ErrorResponse {
  code: string;
  message: string;
}

export interface FeatureBatchesResponse {
  batches: string[];
}

export interface FeatureQueryRequest {
  dataset: string;
  bbox?: BboxDto | null;
  filter?: null | string;
}

export type FeatureSchema = unknown;

export interface FeatureWriteRequest {
  dataset: string;
  batch: string;
  transaction?: null | string;
}

export interface FeatureWriteResponse {
  appended: number | string;
}

export interface GeometryResponse {
  geometry: string;
}

export interface IntersectionRequest {
  left: string;
  right: string;
}

export interface ScanRequest {
  dataset: string;
}

export interface SimplifyRequest {
  geometry: string;
  tolerance: number | string;
}

export interface SleepRequest {
  milliseconds: number | string;
}

export interface SleepResponse {
  slept: number | string;
}

export interface TransactionRequest {
  transaction: string;
}

export interface TransactionResponse {
  ok: boolean;
}

export interface TransformRequest {
  geometry: string;
  source: null | string;
  target: string;
}

export interface ValidateRequest {
  geometry: string;
}

export interface ValidateResponse {
  valid: boolean;
}
