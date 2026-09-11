/**
 * Workbench persistence: results and recent runs are saved to browser
 * localStorage so the workbench survives reloads. Spatial values are stored
 * as canonical base64 SGEOM bytes (the same bytes the host produced), so
 * nothing spatial is re-encoded by the client.
 */

export interface SavedResult {
  id: string;
  name: string;
  op: string;
  savedAt: string;
  /** Canonical SGEOM bytes of the result geometry (base64). */
  geometryBase64: string | null;
  /** A short human summary of the result. */
  summary: string | null;
  note: string;
}

export interface SavedRun {
  id: string;
  op: string;
  ok: boolean;
  startedAt: string;
  detail: string | null;
}

const ResultsKey = "spatial.workbench.v2.results";
const RunsKey = "spatial.workbench.v2.runs";

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

export function loadRuns(): SavedRun[] {
  return read<SavedRun>(RunsKey);
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

export function recordRun(run: SavedRun): SavedRun[] {
  const runs = [run, ...loadRuns()]
    .filter((entry, index, all) => all.findIndex((candidate) => candidate.id === entry.id) === index)
    .slice(0, 50);
  write(RunsKey, runs);
  return runs;
}
