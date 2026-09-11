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
  SleepResponse,
  TransactionResponse,
  ValidateResponse,
} from "./generated-types.ts";
import { decodeFeatureBatch, type FeatureBatch } from "./feature-batch.ts";

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

  // ---- transport ----

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

  private async read<T>(response: Response): Promise<T> {
    if (response.ok) {
      return (await response.json()) as T;
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

    throw new SpatialApiError(response.status, code, message);
  }
}

function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

function fromBase64(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
