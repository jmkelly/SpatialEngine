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
  Map,
  MapRenderRequestDto,
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

/** The result of a neutral ingest (ADR-0041); the map is present when `publish` was requested. */
export interface AuthIdentity {
  issuer: string;
  subject: string;
  username: string;
  roles: string[];
}

export interface AuthTokenResult {
  token: string;
  expiresAt: string;
}

export interface IngestResult {
  dataset: string;
  features: number;
  srid: number;
  identityField?: null | string;
  map?: null | Map;
}

/**
 * How much of a staged upload has landed (ADR-0088). `complete` is true only
 * when every declared byte has arrived, so a partial upload is never presented
 * as a loadable one.
 */
export interface UploadState {
  uploadId: string;
  received: number;
  totalBytes?: null | number;
  complete: boolean;
  sha256?: null | string;
  /** Why the last append was refused, when it was. */
  fault?: null | string;
}

/** One downloadable source of a development seed document (ADR-0078). */
export interface SeedSource {
  id: string;
  url: string;
  format: string;
  srid: number;
  sourceSrid?: null | number;
  identity?: null | string;
  identityField?: null | string;
}

/** One layer of a seeded map, with the compact draw recipe. */
export interface SeedMapLayer {
  dataset: string;
  name?: null | string;
  geometry?: string;
  style?: null | { color?: string; opacity?: number; lineWidth?: number; radius?: number; visible?: boolean };
  kind?: string;
}

/** One map published by a development seed document (ADR-0078). */
export interface SeedMap {
  name: string;
  services: string[];
  layers: SeedMapLayer[];
  description?: null | string;
  copyright?: null | string;
}

/** A development seed document: downloadable sources plus maps to publish (ADR-0078). */
export interface SeedDocument {
  sources: SeedSource[];
  maps: SeedMap[];
  store?: string;
  force?: boolean;
  only?: null | string[];
}

/** One per-item seed failure; the run itself still returns 200. */
export interface SeedFailure {
  target: string;
  code: string;
  message: string;
}

/** The seed summary: counts plus per-item failures (ADR-0078). */
export interface SeedResult {
  store: string;
  ingested: number;
  reused: number;
  published: number;
  failures: SeedFailure[];
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
  private token?: string;

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

  // ---- auth (ADR-0071) ----

  /** Sets the bearer used automatically for subsequent mutations. */
  setToken(token?: string): void {
    this.token = token;
  }

  /** Authenticates and stores the returned opaque bearer in memory. */
  async login(username: string, password: string, signal?: AbortSignal): Promise<AuthTokenResult> {
    const result = await this.post<AuthTokenResult>("/api/auth/login", { username, password }, signal);
    this.token = result.token;
    return result;
  }

  /** Revokes the current bearer and clears the in-memory token. */
  async logout(signal?: AbortSignal): Promise<void> {
    await this.send<void>("POST", "/api/auth/logout", { headers: authorization(this.token), signal });
    this.token = undefined;
  }

  /** Rotates the current bearer and stores the replacement. */
  async refresh(signal?: AbortSignal): Promise<AuthTokenResult> {
    const result = await this.send<AuthTokenResult>("POST", "/api/auth/refresh", { headers: authorization(this.token), signal });
    this.token = result.token;
    return result;
  }

  /** Returns the current identity and roles. */
  me(signal?: AbortSignal): Promise<AuthIdentity> {
    return this.send<AuthIdentity>("GET", "/api/auth/me", { headers: authorization(this.token), signal });
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

  /** Renders a map's datasets using its persisted layer styles (ADR-0047/ADR-0053). */
  async renderMap(name: string, request: MapRenderRequestDto, signal?: AbortSignal): Promise<RasterImage> {
    return this.postForImage(`/api/maps/${encodeURIComponent(name)}/render`, request, signal);
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

  /** Gets one live Mapbox Vector Tile from a map's neutral vector-tile route. */
  async vectorTile(name: string, z: number, x: number, y: number, signal?: AbortSignal): Promise<Uint8Array> {
    return this.getBytes(`/api/maps/${encodeURIComponent(name)}/tiles/mvt/${z}/${x}/${y}.pbf`, signal);
  }

  /** Renders an ordered tile batch with server-side bounded parallelism. */
  async renderTiles(request: TileBatchRequest, signal?: AbortSignal): Promise<TileBatchResponse> {
    return this.post<TileBatchResponse>("/api/render/tiles/batch", request, signal);
  }

  /** Describes the registered tiling schemes, their levels of detail and the batch cap. */
  async tileCapabilities(signal?: AbortSignal): Promise<TileCapabilitiesResponse> {
    return this.get<TileCapabilitiesResponse>("/api/render/tiles/capabilities", signal);
  }

  // ---- maps & ingest (ADR-0041/ADR-0053) ----

  /** Lists every map (declared first, then runtime by name). */
  async listMaps(signal?: AbortSignal): Promise<Map[]> {
    return this.get<Map[]>("/api/maps", signal);
  }

  /** Gets one map by name. */
  async getMap(name: string, signal?: AbortSignal): Promise<Map> {
    return this.get<Map>(`/api/maps/${encodeURIComponent(name)}`, signal);
  }

  /** Creates or replaces a runtime map (requires the admin token). */
  async putMap(map: Map, adminToken?: string): Promise<Map> {
    return this.send<Map>("PUT", `/api/maps/${encodeURIComponent(map.name)}`, {
      body: JSON.stringify(map),
      headers: { "content-type": "application/json", ...authorization(adminToken ?? this.token) },
    });
  }

  /** Deletes a runtime map and reports whether it existed (requires the admin token). */
  async deleteMap(name: string, adminToken?: string): Promise<boolean> {
    return this.send<boolean>("DELETE", `/api/maps/${encodeURIComponent(name)}`, {
      headers: authorization(adminToken ?? this.token),
    });
  }

  /**
   * Uploads a GeoJSON/NDJSON/CSV blob and loads it atomically into a dataset,
   * optionally registering the dataset on a Feature map in the same call
   * (requires the admin token). The browser/tool path uses multipart; the body
   * is opaque bytes.
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
      headers: authorization(adminToken ?? this.token),
      signal,
    });
  }

  // ---- staged uploads (ADR-0088) ----

  /**
   * Opens a staged upload (requires the admin token). Passing the same
   * `uploadId` again returns the existing upload rather than truncating it,
   * so a client whose create response was lost recovers instead of restarting.
   */
  async startUpload(
    options: { uploadId?: string; totalBytes?: number; sha256?: string },
    adminToken?: string,
    signal?: AbortSignal,
  ): Promise<UploadState> {
    const query = new URLSearchParams();
    if (options.uploadId) query.set("id", options.uploadId);
    if (options.totalBytes !== undefined) query.set("total", String(options.totalBytes));
    if (options.sha256) query.set("sha256", options.sha256);
    const suffix = query.toString();
    return this.send<UploadState>("POST", `/api/uploads${suffix ? `?${suffix}` : ""}`, {
      headers: authorization(adminToken ?? this.token),
      signal,
    });
  }

  /** How much of a staged upload has landed — the offset to resume from. */
  async getUpload(uploadId: string, adminToken?: string, signal?: AbortSignal): Promise<UploadState> {
    return this.get<UploadState>(`/api/uploads/${encodeURIComponent(uploadId)}`, signal);
  }

  /** Lists every staged upload. */
  async listUploads(adminToken?: string, signal?: AbortSignal): Promise<UploadState[]> {
    return this.get<UploadState[]>("/api/uploads", signal);
  }

  /**
   * Appends a chunk whose first byte belongs at `offset`; the chunk declaring
   * the total size and digest is what completes the upload.
   */
  async appendUpload(
    uploadId: string,
    chunk: Blob,
    options: { offset: number; totalBytes?: number; sha256?: string },
    adminToken?: string,
    signal?: AbortSignal,
  ): Promise<UploadState> {
    const query = new URLSearchParams({ offset: String(options.offset) });
    if (options.totalBytes !== undefined) query.set("total", String(options.totalBytes));
    if (options.sha256) query.set("sha256", options.sha256);
    return this.send<UploadState>("PUT", `/api/uploads/${encodeURIComponent(uploadId)}?${query.toString()}`, {
      body: chunk,
      headers: { "content-type": "application/octet-stream", ...authorization(adminToken ?? this.token) },
      signal,
    });
  }

  /** Discards a staged upload and its bytes. */
  async deleteUpload(uploadId: string, adminToken?: string, signal?: AbortSignal): Promise<boolean> {
    return this.send<boolean>("DELETE", `/api/uploads/${encodeURIComponent(uploadId)}`, {
      headers: authorization(adminToken ?? this.token),
      signal,
    });
  }

  /**
   * Ingests a staged upload instead of a body (requires the admin token). The
   * host loads it only when every declared byte is staged, and discards the
   * staging once the load has committed.
   */
  async ingestUpload(
    uploadId: string,
    options: { dataset: string; srid: number; format?: string; store?: string; identity?: string; identityField?: string; publish?: string; sourceSrid?: number },
    adminToken?: string,
    signal?: AbortSignal,
  ): Promise<IngestResult> {
    const query = new URLSearchParams({
      dataset: options.dataset,
      srid: String(options.srid),
      format: options.format ?? "geojson",
      store: options.store ?? "memory",
      upload: uploadId,
    });
    if (options.identity) query.set("identity", options.identity);
    if (options.identityField) query.set("identityField", options.identityField);
    if (options.publish) query.set("publish", options.publish);
    if (options.sourceSrid !== undefined) query.set("sourceSrid", String(options.sourceSrid));
    return this.send<IngestResult>("POST", `/api/ingest?${query.toString()}`, {
      headers: authorization(adminToken ?? this.token),
      signal,
    });
  }

  /**
   * Uploads a blob in resumable chunks and ingests it (ADR-0088): the driver
   * asks the host where the staging got to, continues from there, and ingests
   * once every byte has landed. The load itself is not chunked — it is one
   * transaction over the whole document, as any other ingest is.
   */
  async ingestResumable(
    content: Blob,
    options: { dataset: string; srid: number; format?: string; store?: string; identity?: string; identityField?: string; publish?: string; sourceSrid?: number; chunkSize?: number; uploadId?: string; maxAttempts?: number },
    adminToken?: string,
    signal?: AbortSignal,
  ): Promise<IngestResult> {
    const chunkSize = options.chunkSize ?? 8 * 1024 * 1024;
    const maxAttempts = options.maxAttempts ?? 3;
    const digest = await sha256Hex(content);
    const started = await this.startUpload({ uploadId: options.uploadId, totalBytes: content.size, sha256: digest }, adminToken, signal);
    let offset = (await this.getUpload(started.uploadId, adminToken, signal)).received;
    while (offset < content.size) {
      offset = (await this.appendChunk(started.uploadId, content, offset, content.size, digest, chunkSize, maxAttempts, adminToken, signal)).received;
    }

    return this.ingestUpload(started.uploadId, options, adminToken, signal);
  }

  /**
   * Appends the chunk starting at `offset`, retrying a chunk that failed in
   * transit from whatever offset the host reports afterwards — a chunk that was
   * refused may still have landed, so the client's own count is not trusted.
   */
  private async appendChunk(
    uploadId: string,
    content: Blob,
    offset: number,
    total: number,
    digest: string,
    chunkSize: number,
    attempts: number,
    adminToken?: string,
    signal?: AbortSignal,
  ): Promise<UploadState> {
    for (let attempt = 1; ; attempt++) {
      const chunk = content.slice(offset, offset + chunkSize);
      try {
        return await this.appendUpload(uploadId, chunk, { offset, totalBytes: total, sha256: digest }, adminToken, signal);
      } catch (error) {
        if (attempt >= attempts || !isRetriable(error)) throw error;
        offset = (await this.getUpload(uploadId, adminToken, signal)).received;
      }
    }
  }

  /**
   * Runs a seed document against a Development host: download, ingest and
   * publish in one call (ADR-0078; requires the admin token when one is
   * configured). Hosts without the endpoint answer 404.
   */
  async seed(document: SeedDocument, adminToken?: string, signal?: AbortSignal): Promise<SeedResult> {
    return this.send<SeedResult>("POST", "/api/seed", {
      body: JSON.stringify(document),
      headers: { "content-type": "application/json", ...authorization(adminToken) },
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

  private async getBytes(path: string, signal?: AbortSignal): Promise<Uint8Array> {
    const response = await this.fetchFn(`${this.baseUrl}${path}`, { signal });
    if (!response.ok) throw await this.fail(response);
    return new Uint8Array(await response.arrayBuffer());
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
    if (response.status === 401 && typeof window !== "undefined") {
      window.dispatchEvent(new Event("spatial:auth-required"));
    }
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

/** The SHA-256 of a blob, lower-case hex, as the staging protocol names it. */
async function sha256Hex(content: Blob): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", await content.arrayBuffer());
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

/**
 * Whether a failed chunk is worth sending again: a transport failure or a
 * server-side answer might have landed the bytes, so the retry re-reads the
 * host's offset. A structured client error would fail identically.
 */
function isRetriable(error: unknown): boolean {
  if (error instanceof SpatialApiError) return error.status >= 500;
  return error instanceof TypeError;
}

function fromBase64(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
