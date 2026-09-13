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

export interface ImagerySourceDto {
  name: string;
}

export interface IntersectionRequest {
  left: string;
  right: string;
}

export type JsonElement = unknown;

export interface Map {
  name: string;
  store: string;
  layers: MapLayer[];
  services: MapService[];
  description?: null | string;
  copyright?: null | string;
}

export interface MapLayer {
  dataset: string;
  layerId: number | string;
  name?: null | string;
  style?: null | string;
  kind?: MapLayerKind;
  store?: null | string;
}

export type MapLayerKind = "feature" | "image";

export interface MapRenderRequestDto {
  viewport: ViewportDto;
  imagery?: null | RenderImageryDto[];
  format?: RasterFormat;
  quality?: number | string;
  background?: null | string;
  transparent?: boolean;
  scale?: number | string;
}

export type MapService = "feature" | "map" | "tiles" | "wms" | "wfs" | "image";

export type RasterBlend = "over" | "multiply" | "screen" | "darken" | "lighten";

export type RasterFormat = "png" | "jpeg" | "webp" | "tiff";

export interface RenderCapabilitiesResponse {
  formats: string[];
  pixelFormats: string[];
  blendModes: string[];
  maxPixels: number | string;
  imagerySources: ImagerySourceDto[];
}

export interface RenderImageryDto {
  source: string;
  blend?: RasterBlend;
  opacity?: number | string;
}

export interface RenderLayerDto {
  dataset: string;
  store?: null | string;
  filter?: null | string;
}

export interface RenderRequest {
  viewport: ViewportDto;
  style: JsonElement;
  layers: RenderLayerDto[];
  imagery?: null | RenderImageryDto[];
  format?: RasterFormat;
  quality?: number | string;
  background?: null | string;
  transparent?: boolean;
  scale?: number | string;
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

export interface TileBatchRequest {
  request: TileRenderRequest;
  tiles: TileDto[];
}

export interface TileBatchResponse {
  tiles: TileResultDto[];
}

export interface TileCapabilitiesResponse {
  defaultScheme: string;
  maxTilesPerBatch: number | string;
  schemes: TileSchemeDto[];
}

export interface TileDto {
  z: number | string;
  x: number | string;
  y: number | string;
}

export interface TileLevelDto {
  zoom: number | string;
  resolution: number | string;
  scaleDenominator: number | string;
}

export interface TileRenderRequest {
  style: JsonElement;
  layers: RenderLayerDto[];
  imagery?: null | RenderImageryDto[];
  scheme?: null | string;
  format?: RasterFormat;
  quality?: number | string;
  background?: null | string;
  transparent?: boolean;
  scale?: number | string;
}

export interface TileResultDto {
  z: number | string;
  x: number | string;
  y: number | string;
  cached: boolean;
  contentType: string;
  width: number | string;
  height: number | string;
  content: string;
}

export interface TileSchemeDto {
  id: string;
  crs: string;
  tileSize: number | string;
  minZoom: number | string;
  maxZoom: number | string;
  levels: TileLevelDto[];
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

export interface ViewportDto {
  minX: number | string;
  minY: number | string;
  maxX: number | string;
  maxY: number | string;
  width: number | string;
  height: number | string;
  crs: string;
}
