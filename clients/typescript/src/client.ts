import { CapabilityStreamError, SpatialApiError } from "./errors.ts";
import type {
  CapabilityDetailDto,
  CapabilityErrorDto,
  CapabilitySummaryDto,
  InvocationRequest,
  InvocationResponse,
  JobEventsResponse,
  JobResponse,
  JobState,
  PluginDto,
  ResourceDto,
} from "./generated-types.ts";
import { decode } from "./wire.ts";
import { decodeFeatureBatch, type FeatureBatch } from "./feature-batch.ts";

/**
 * The TypeScript SDK client for the public spatial host API (plan §12): a
 * fetch-based client — browser and Node compatible — speaking the same
 * versioned HTTP contracts as the .NET SDK. Inline values use the shared
 * wire codec; streams read as decoded items (line-delimited JSON).
 */
export class SpatialClient {
  private readonly baseUrl: string;
  private readonly fetchFn: typeof fetch;

  constructor(
    baseUrl: string,
    fetchFn: typeof fetch = fetch,
  ) {
    this.baseUrl = baseUrl.replace(/\/+$/, "");
    this.fetchFn = fetchFn;
  }

  // ---- Capabilities ----

  /** Every registered capability with its serving providers. */
  async getCapabilities(): Promise<CapabilitySummaryDto[]> {
    return this.json<CapabilitySummaryDto[]>("/api/capabilities");
  }

  /** The full declaration of one capability (404 -> SpatialApiError). */
  async getCapability(capabilityId: string): Promise<CapabilityDetailDto> {
    return this.json<CapabilityDetailDto>(`/api/capabilities/${encodeURIComponent(capabilityId)}`);
  }

  // ---- Invocations ----

  /**
   * Invokes a capability: inline capabilities complete in the response;
   * long-running (or wait:false) invocations come back with kind "job".
   */
  async invoke(request: InvocationRequest): Promise<InvocationResponse> {
    return this.json<InvocationResponse>("/api/invocations", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(request),
    });
  }

  // ---- Jobs ----

  /** One job's public snapshot. */
  async getJob(jobId: string): Promise<JobResponse> {
    return this.json<JobResponse>(`/api/jobs/${encodeURIComponent(jobId)}`);
  }

  /** Requests cancellation of a running job and returns its snapshot. */
  async cancelJob(jobId: string): Promise<JobResponse> {
    return this.json<JobResponse>(`/api/jobs/${encodeURIComponent(jobId)}/cancel`, { method: "POST" });
  }

  /** The append-only event log of one job. */
  async getJobEvents(jobId: string): Promise<JobEventsResponse> {
    return this.json<JobEventsResponse>(`/api/jobs/${encodeURIComponent(jobId)}/events`);
  }

  /** Polls a job until a terminal state, backing off from 50 to 500 ms. */
  async waitForJob(jobId: string, pollIntervalMs = 50): Promise<JobResponse> {
    let interval = pollIntervalMs;
    for (;;) {
      const job = await this.getJob(jobId);
      if (isTerminal(job.state)) return job;
      await sleep(interval);
      interval = Math.min(interval + 50, 500);
    }
  }

  // ---- Resources ----

  /** One resource's metadata. */
  async getResource(resourceToken: string): Promise<ResourceDto> {
    return this.json<ResourceDto>(`/api/resources/${encodeURIComponent(resourceToken)}/metadata`);
  }

  /** Disposes a resource (closes a stream, releases its leases). */
  async deleteResource(resourceToken: string): Promise<void> {
    await this.request(`/api/resources/${encodeURIComponent(resourceToken)}`, { method: "DELETE" });
  }

  /**
   * Reads a bounded stream as decoded items: scalars, strings, Uint8Array
   * ($bytes), tagged geometry/resource values. Consuming a stream to its end
   * closes the resource; a stream that ended with a structured failure
   * throws CapabilityStreamError.
   */
  async *readStream(resourceToken: string): AsyncGenerator<unknown, void, void> {
    const response = await this.request(`/api/resources/${encodeURIComponent(resourceToken)}/stream`, {
      headers: { accept: "application/x-ndjson" },
    });
    const reader = response.body!.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";
      for (const line of lines) {
        if (line.trim().length === 0) continue;
        yield this.decodeLine(line);
      }
    }
    if (buffer.trim().length > 0) yield this.decodeLine(buffer);
  }

  /** Reads a feature stream (scan/query) as decoded canonical feature batches. */
  async *readFeatureBatches(resourceToken: string): AsyncGenerator<FeatureBatch, void, void> {
    for await (const item of this.readStream(resourceToken)) {
      if (!(item instanceof Uint8Array)) {
        throw new CapabilityStreamError({
          kind: "ContractViolation",
          code: "contract.violation",
          message: "a feature stream item is not canonical binary (Uint8Array)",
        });
      }
      yield decodeFeatureBatch(item);
    }
  }

  // ---- Plugins ----

  /** The activated plugin worker packages (empty when none are configured). */
  async getPlugins(): Promise<PluginDto[]> {
    return this.json<PluginDto[]>("/api/plugins");
  }

  /** One plugin worker package by provider id (404 -> SpatialApiError). */
  async getPlugin(providerId: string): Promise<PluginDto> {
    return this.json<PluginDto>(`/api/plugins/${encodeURIComponent(providerId)}`);
  }

  /**
   * Routes new work to the provider for every capability it serves
   * (active-preference routing, ADR-0031 — the browser replacement
   * demonstration) and returns its updated state.
   */
  async routeNewWork(providerId: string): Promise<PluginDto> {
    return this.json<PluginDto>(`/api/plugins/${encodeURIComponent(providerId)}/route-new-work`, { method: "POST" });
  }

  /**
   * Drains the plugin worker: stops routing new work, waits for in-flight
   * invocations, reclaims its resources and stops the process (ADR-0031).
   */
  async drainPlugin(providerId: string): Promise<PluginDto> {
    return this.json<PluginDto>(`/api/plugins/${encodeURIComponent(providerId)}/drain`, { method: "POST" });
  }

  /**
   * Rolls new work back to the provider: reactivates its package when it
   * was drained or failed, then routes new work to it again (ADR-0031).
   */
  async rollbackPlugin(providerId: string): Promise<PluginDto> {
    return this.json<PluginDto>(`/api/plugins/${encodeURIComponent(providerId)}/rollback`, { method: "POST" });
  }

  // ---- internals ----

  private async json<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await this.request(path, init);
    return (await response.json()) as T;
  }

  private async request(path: string, init?: RequestInit): Promise<Response> {
    try {
      const response = await this.fetchFn(this.baseUrl + path, init);
      if (!response.ok) {
        const detail = await response.text().catch(() => "");
        throw new SpatialApiError(response.status, detail || `request to ${path} failed`);
      }
      return response;
    } catch (error) {
      if (error instanceof SpatialApiError) throw error;
      if (error instanceof TypeError) {
        throw new SpatialApiError(0, `cannot reach the spatial host at ${this.baseUrl}: ${error.message}`);
      }
      throw error;
    }
  }

  private decodeLine(line: string): unknown {
    const node = JSON.parse(line);
    if (typeof node === "object" && node !== null && "$error" in node) {
      throw new CapabilityStreamError(node.$error as CapabilityErrorDto);
    }
    return decode(node);
  }
}

function isTerminal(state: JobState): boolean {
  return state === "completed" || state === "failed" || state === "cancelled" || state === "timedOut";
}

function sleep(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}