import { SpatialApiError } from "./errors.ts";
import type {
  BeginTransactionResponse,
  CatalogueResponse,
  CreateDatasetResponse,
  CrsDescription,
  DatasetDescription,
  FeatureBatchesResponse,
  FeatureWriteResponse,
  GeometryResponse,
  Publication,
  RasterFormat,
  RenderCapabilitiesResponse,
  RenderRequest,
  SleepResponse,
  TileBatchRequest,
  TileBatchResponse,
  TileCapabilitiesResponse,
  TileRenderRequest,
  TransactionResponse,
  ValidateResponse,
} from "./generated-types.ts";
import { decodeFeatureBatch, type FeatureBatch } from "./feature-batch.ts";

/** An encoded raster result from the render route, with the host's metadata headers. */
export interface RasterImage {
  bytes: Uint8Array;
  mediaType: string;
  width: number;
  height: number;
  format: RasterFormat;
}

/** The result of a neutral ingest (ADR-0041); the publication is present when `publish` was requested. */
export interface IngestResult {
  dataset: string;
  features: number;
  srid: number;
  identityField?: null | string;
  publication?: null | Publication;
}

/**
 * The TypeScript SDK client for the typed spatial host API (ADR-0033): a
 * fetch-based client — browser and Node compatible — with one method per
 * route. Geometries cross as Base64 canonical SGEOM bytes; feature batches
 * as Base64 canonical SFBAT bytes decoded by decodeFeatureBatch.
 */
export class SpatialClient {
  private readonly baseUrl: string;
  private readonly fetchFn: typeof fetch;

  constructor(
    baseUrl: string,
    fetchFn: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response> = (input, init) => fetch(input, init),
  ) {
    this.baseUrl = baseUrl.replace(/\/+$/, "");
    // Bind the caller's fetch: the global window.fetch throws "Illegal
    // invocation" when called as an unbound method reference (browsers),
    // so the default is a wrapper and injected fetches are trusted as-is.
    this.fetchFn = fetchFn.bind(globalThis);
  }

  // ---- geometry ----

  /** Buffers SGEOM bytes by a distance (optional quadrant segments, default 8). */
  async buffer(geometry: Uint8Array, distance: number, quadrantSegments?: number, signal?: AbortSignal): Promise<Uint8Array> {
    const response = await this.post<GeometryResponse>("/api/geometry/buffer", {
      geometry: toBase64(geometry),
      distance,
      quadrantSegments: quadrantSegments ?? 8,
    }, signal);
    return fromBase64(response.geometry);
  }

  /** Intersects two SGEOM geometries. */
  async intersection(left: Uint8Array, right: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> {
    const response = await this.post<GeometryResponse>("/api/geometry/intersection", {
      left: toBase64(left),
      right: toBase64(right),
    }, signal);
    return fromBase64(response.geometry);
  }

  /** Reports OGC validity (invalid geometry is a successful false). */
  async validate(geometry: Uint8Array, signal?: AbortSignal): Promise<boolean> {
    const response = await this.post<ValidateResponse>("/api/geometry/validate", {
      geometry: toBase64(geometry),
    }, signal);
    return response.valid;
  }

  /** Simplifies SGEOM bytes with Douglas-Peucker tolerance. */
  async simplify(geometry: Uint8Array, tolerance: number, signal?: AbortSignal): Promise<Uint8Array> {
    const response = await this.post<GeometryResponse>("/api/geometry/simplify", {
      geometry: toBase64(geometry),
      tolerance,
    }, signal);
    return fromBase64(response.geometry);
  }

  // ---- transforms ----

  /** Describes one CRS identity (e.g. EPSG:4326). */
  async describeCrs(crs: string, signal?: AbortSignal): Promise<CrsDescription> {
    return this.post<CrsDescription>("/api/crs/describe", { crs }, signal);
  }

  /** Transforms SGEOM bytes to the target CRS (source defaults to the geometry's own CRS). */
  async transform(geometry: Uint8Array, target: string, source?: string, signal?: AbortSignal): Promise<Uint8Array> {
    const response = await this.post<GeometryResponse>("/api/coordinates/transform", {
      geometry: toBase64(geometry),
      source: source ?? null,
      target,
    }, signal);
    return fromBase64(response.geometry);
  }

  // ---- catalogue / datasets ----

  /** Lists dataset summaries (demo store by default). */
  async catalogue(store = "demo", pattern?: string): Promise<CatalogueResponse> {
    const query = pattern === undefined ? "" : `&pattern=${encodeURIComponent(pattern)}`;
    return this.get<CatalogueResponse>(`/api/catalogue?store=${encodeURIComponent(store)}${query}`);
  }

  /** Describes one dataset. */
  async describeDataset(dataset: string, store = "demo"): Promise<DatasetDescription> {
    return this.get<DatasetDescription>(`/api/datasets/${encodeURIComponent(dataset)}?store=${encodeURIComponent(store)}`);
  }

  /** Creates a dataset from a defining SFBAT batch (postgis store by default). */
  async createDataset(dataset: string, batch: Uint8Array, srid: number, store = "postgis"): Promise<CreateDatasetResponse> {
    return this.post<CreateDatasetResponse>(`/api/datasets?store=${encodeURIComponent(store)}`, {
      dataset,
      batch: toBase64(batch),
      srid,
    });
  }

  // ---- features ----

  /** Scans every feature of a dataset as decoded batches. */
  async scan(dataset: string, store = "demo", signal?: AbortSignal): Promise<FeatureBatch[]> {
    const response = await this.post<FeatureBatchesResponse>(`/api/features/scan?store=${encodeURIComponent(store)}`, { dataset }, signal);
    return response.batches.map((bytes) => decodeFeatureBatch(fromBase64(bytes)));
  }

  /** Queries a dataset by bbox and/or attribute filter as decoded batches. */
  async query(
    dataset: string,
    options?: { bbox?: { minX: number; minY: number; maxX: number; maxY: number }; filter?: string },
    store = "demo",
    signal?: AbortSignal,
  ): Promise<FeatureBatch[]> {
    const response = await this.post<FeatureBatchesResponse>(`/api/features/query?store=${encodeURIComponent(store)}`, {
      dataset,
      bbox: options?.bbox ?? null,
      filter: options?.filter ?? null,
    }, signal);
    return response.batches.map((bytes) => decodeFeatureBatch(fromBase64(bytes)));
  }

  /** Appends an SFBAT batch (postgis store by default). */
  async write(dataset: string, batch: Uint8Array, transaction?: string, store = "postgis"): Promise<number> {
    const response = await this.post<FeatureWriteResponse>(`/api/features/write?store=${encodeURIComponent(store)}`, {
      dataset,
      batch: toBase64(batch),
      transaction: transaction ?? null,
    });
    return Number(response.appended);
  }

  // ---- transactions (postgis store) ----

  async beginTransaction(store = "postgis"): Promise<string> {
    const response = await this.post<BeginTransactionResponse>(`/api/transactions/begin?store=${encodeURIComponent(store)}`, {});
    return response.transaction;
  }

  async commitTransaction(transaction: string, store = "postgis"): Promise<boolean> {
    const response = await this.post<TransactionResponse>(`/api/transactions/commit?store=${encodeURIComponent(store)}`, { transaction });
    return response.ok;
  }

  async rollbackTransaction(transaction: string, store = "postgis"): Promise<boolean> {
    const response = await this.post<TransactionResponse>(`/api/transactions/rollback?store=${encodeURIComponent(store)}`, { transaction });
    return response.ok;
  }

  // ---- health ----

  /** The host liveness probe. */
  async getHealthLive(): Promise<{ status?: string }> {
    return this.get<{ status?: string }>("/health/live");
  }

  /** The host readiness probe (includes the configured stores). */
  async getHealthReady(): Promise<{ status?: string; stores?: string[] }> {
    return this.get<{ status?: string; stores?: string[] }>("/health/ready");
  }

  // ---- demo ----
  /** Sleeps on the host (cancellable via AbortSignal), reporting nothing but the duration. */
  async sleep(milliseconds: number, signal?: AbortSignal): Promise<number> {
    const response = await this.post<SleepResponse>("/api/demo/sleep", { milliseconds }, signal);
    return Number(response.slept);
  }

  // ---- rendering (ADR-0044) ----

  /** Describes the configured raster formats, pixel cap and imagery sources. */
  async renderCapabilities(signal?: AbortSignal): Promise<RenderCapabilitiesResponse> {
    return this.get<RenderCapabilitiesResponse>("/api/render/capabilities", signal);
  }

  /** Renders a styled vector/imagery request to encoded image bytes. */
  async render(request: RenderRequest, signal?: AbortSignal): Promise<RasterImage> {
    return this.postForImage("/api/render", request, signal);
  }

  /** Renders one cache-aware tile; the request's format selects the path suffix. */
  async renderTile(
    z: number,
    x: number,
    y: number,
    request: TileRenderRequest,
    signal?: AbortSignal,
  ): Promise<RasterImage> {
    return this.postForImage(`/api/render/tiles/${z}/${x}/${y}.${request.format ?? "png"}`, request, signal);
  }

  /** Renders an ordered tile batch with server-side bounded parallelism. */
  async renderTiles(request: TileBatchRequest, signal?: AbortSignal): Promise<TileBatchResponse> {
    return this.post<TileBatchResponse>("/api/render/tiles/batch", request, signal);
  }

  /** Describes the registered tiling schemes, their levels of detail and the batch cap. */
  async tileCapabilities(signal?: AbortSignal): Promise<TileCapabilitiesResponse> {
    return this.get<TileCapabilitiesResponse>("/api/render/tiles/capabilities", signal);
  }

  // ---- publications & ingest (ADR-0041) ----

  /** Lists every publication (declared first, then runtime by name). */
  async listPublications(signal?: AbortSignal): Promise<Publication[]> {
    return this.get<Publication[]>("/api/publications", signal);
  }

  /** Gets one publication by name. */
  async getPublication(name: string, signal?: AbortSignal): Promise<Publication> {
    return this.get<Publication>(`/api/publications/${encodeURIComponent(name)}`, signal);
  }

  /** Creates or replaces a runtime publication (requires the admin token). */
  async putPublication(publication: Publication, adminToken?: string): Promise<Publication> {
    return this.send<Publication>("PUT", `/api/publications/${encodeURIComponent(publication.name)}`, {
      body: JSON.stringify(publication),
      headers: { "content-type": "application/json", ...authorization(adminToken) },
    });
  }

  /** Deletes a runtime publication and reports whether it existed (requires the admin token). */
  async deletePublication(name: string, adminToken?: string): Promise<boolean> {
    return this.send<boolean>("DELETE", `/api/publications/${encodeURIComponent(name)}`, {
      headers: authorization(adminToken),
    });
  }

  /**
   * Uploads a GeoJSON/NDJSON/CSV blob and loads it atomically into a dataset,
   * optionally registering a publication in the same call (requires the admin
   * token). The browser/tool path uses multipart; the body is opaque bytes.
   */
  async ingest(
    content: Blob,
    fileName: string,
    options: { dataset: string; srid: number; format?: string; store?: string; identity?: string; identityField?: string; publish?: string; sourceSrid?: number },
    adminToken?: string,
    signal?: AbortSignal,
  ): Promise<IngestResult> {
    const query = new URLSearchParams({
      dataset: options.dataset,
      srid: String(options.srid),
      format: options.format ?? "geojson",
      store: options.store ?? "memory",
    });
    if (options.identity) query.set("identity", options.identity);
    if (options.identityField) query.set("identityField", options.identityField);
    if (options.publish) query.set("publish", options.publish);
    if (options.sourceSrid !== undefined) query.set("sourceSrid", String(options.sourceSrid));
    const form = new FormData();
    form.append("file", content, fileName);
    return this.send<IngestResult>("POST", `/api/ingest?${query.toString()}`, {
      body: form,
      headers: authorization(adminToken),
      signal,
    });
  }

  // ---- transport ----

  private async postForImage(path: string, body: unknown, signal?: AbortSignal): Promise<RasterImage> {
    const response = await this.fetchFn(`${this.baseUrl}${path}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    if (!response.ok) {
      throw await this.fail(response);
    }

    const contentType = response.headers.get("content-type") ?? "application/octet-stream";
    const mediaType = contentType.split(";")[0]?.trim() || "application/octet-stream";
    return {
      bytes: new Uint8Array(await response.arrayBuffer()),
      mediaType,
      width: Number(response.headers.get("x-raster-width") ?? 0),
      height: Number(response.headers.get("x-raster-height") ?? 0),
      format: formatOf(mediaType),
    };
  }

  private async get<T>(path: string, signal?: AbortSignal): Promise<T> {
    const response = await this.fetchFn(`${this.baseUrl}${path}`, { signal });
    return this.read<T>(response);
  }

  private async post<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
    const response = await this.fetchFn(`${this.baseUrl}${path}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    return this.read<T>(response);
  }

  private async send<T>(
    method: string,
    path: string,
    init: { body?: BodyInit; headers?: Record<string, string>; signal?: AbortSignal },
  ): Promise<T> {
    const response = await this.fetchFn(`${this.baseUrl}${path}`, {
      method,
      body: init.body,
      headers: init.headers,
      signal: init.signal,
    });
    return this.read<T>(response);
  }

  private async read<T>(response: Response): Promise<T> {
    if (response.ok) {
      return (await response.json()) as T;
    }

    throw await this.fail(response);
  }

  private async fail(response: Response): Promise<SpatialApiError> {
    let code = "http.error";
    let message = `the host failed with ${response.status}`;
    try {
      const error = (await response.json()) as { code?: string; message?: string };
      if (error.code) code = error.code;
      if (error.message) message = error.message;
    } catch {
      // Keep the status-only failure.
    }

    return new SpatialApiError(response.status, code, message);
  }
}

function formatOf(mediaType: string): RasterFormat {
  if (mediaType === "image/jpeg") return "jpeg";
  if (mediaType === "image/webp") return "webp";
  if (mediaType === "image/tiff") return "tiff";
  return "png";
}

function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

function authorization(token?: string): Record<string, string> {
  return token ? { authorization: `Bearer ${token}` } : {};
}

function fromBase64(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
