import { fromBase64 } from "@spatial/client";

/**
 * Workbench persistence (Phase 10): results and recent jobs are saved to
 * browser localStorage so the workbench survives reloads — the browser-side
 * "persist the result" of plan §17.8. Spatial values are stored as canonical
 * base64 bytes (the same SGEOM wire bytes the host produced), so nothing
 * spatial is re-encoded by the client.
 */

export interface SavedResult {
  id: string;
  name: string;
  capability: string;
  provider: string | null;
  savedAt: string;
  /** Canonical SGEOM bytes of the result geometry (base64 on the wire). */
  geometryBase64: string | null;
  /** The JSON-encodable result value (scalar results cross inline). */
  valueJson: string | null;
  note: string;
}

export interface SavedJob {
  id: string;
  capability: string;
  state: string;
  startedAt: string;
  provider: string | null;
}

const ResultsKey = "spatial.workbench.results";
const JobsKey = "spatial.workbench.jobs";

function read<T>(key: string): T[] {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed) ? (parsed as T[]) : [];
  } catch {
    return [];
  }
}

function write<T>(key: string, items: T[]): void {
  localStorage.setItem(key, JSON.stringify(items));
}

export function loadResults(): SavedResult[] {
  return read<SavedResult>(ResultsKey);
}

export function loadJobs(): SavedJob[] {
  return read<SavedJob>(JobsKey);
}

export function saveResult(result: SavedResult): SavedResult[] {
  const results = [result, ...loadResults()].slice(0, 50);
  write(ResultsKey, results);
  return results;
}

export function deleteResult(id: string): SavedResult[] {
  const results = loadResults().filter((result) => result.id !== id);
  write(ResultsKey, results);
  return results;
}

export function recordJob(job: SavedJob): SavedJob[] {
  const jobs = [job, ...loadJobs()]
    .filter((entry, index, all) => all.findIndex((candidate) => candidate.id === entry.id) === index)
    .slice(0, 50);
  write(JobsKey, jobs);
  return jobs;
}

/** Turns a wire-decoded geometry value into a saved-result geometry payload. */
export function geometryToBase64(geometry: unknown): string | null {
  if (typeof geometry !== "object" || geometry === null) return null;
  const tagged = geometry as Record<string, unknown>;
  if (typeof tagged.$geometry === "string") return tagged.$geometry;
  return null;
}

/** Reads a saved result's geometry back to canonical bytes for the map layer. */
export function resultGeometryBytes(result: SavedResult): Uint8Array | null {
  if (result.geometryBase64 === null) return null;
  try {
    return fromBase64(result.geometryBase64);
  } catch {
    return null;
  }
}