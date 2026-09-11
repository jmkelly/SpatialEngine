/** One finished operation the Run screen shows. */
export interface RunOutcome {
  /** The operation id (buffer, scan, …). */
  op: string;
  ok: boolean;
  error: string | null;
  /** Canonical SGEOM bytes of a geometry result (base64), for the map and persistence. */
  geometryBase64: string | null;
  /** A short human summary (counts, flags, descriptions). */
  summary: string | null;
  finishedAt: string;
}
