/** One finished invocation the Run screen shows (inline or job terminal). */
export interface RunOutcome {
  capability: string;
  provider: string | null;
  ok: boolean;
  error: string | null;
  result: unknown;
  jobId: string | null;
  step: string | null;
  finishedAt: string;
}