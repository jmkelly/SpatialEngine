/**
 * Visual conformance harness D (T-074): chaos toggles proving the typed
 * error mapping. This module holds the scenario table (one entry per
 * toggle: the public host route it drives and the expected HTTP status +
 * ErrorResponse code) and the pure envelope helpers the screen and the
 * unit tests share. Fetching stays in the screen so Node can type-strip
 * this module directly — no spatial logic, no SDK import.
 */

/** The typed mapping one toggle asserts: an HTTP status plus an ErrorResponse code. */
export interface ChaosExpectation {
  status: number;
  /**
   * The expected `ErrorResponse.code`; null when the host answers no
   * envelope (499 cancellation: ErrorMapper returns a bare status code).
   */
  code: string | null;
}

/** One chaos toggle: a public host route plus the mapping it proves. */
export interface ChaosScenario {
  id: string;
  label: string;
  /** What the toggle proves, in one line. */
  detail: string;
  method: string;
  route: string;
  expect: ChaosExpectation;
}

const Scenarios: ChaosScenario[] = [
  {
    id: "postgis-down",
    label: "PostGIS down",
    detail: "Catalogue on the postgis store with no connection string fails as store.unavailable.",
    method: "GET",
    route: "/api/catalogue?store=postgis",
    expect: { status: 503, code: "store.unavailable" },
  },
  {
    id: "missing-dataset",
    label: "Missing dataset",
    detail: "Describing a dataset the demo store never seeded fails as not.found.",
    method: "GET",
    route: "/api/datasets/chaos-no-such-dataset?store=demo",
    expect: { status: 404, code: "not.found" },
  },
  {
    id: "missing-map",
    label: "Missing map",
    detail: "Fetching a map name neither declared nor published fails as not.found.",
    method: "GET",
    route: "/api/maps/chaos-no-such-map",
    expect: { status: 404, code: "not.found" },
  },
  {
    id: "bad-geometry",
    label: "Malformed geometry bytes",
    detail: "A buffer request whose geometry is not Base64 SGEOM fails as invalid.arguments.",
    method: "POST",
    route: "/api/geometry/buffer",
    expect: { status: 400, code: "invalid.arguments" },
  },
  {
    id: "bad-crs",
    label: "Unknown CRS identity",
    detail: "Describing a CRS outside the authority:code shape fails as invalid.arguments.",
    method: "POST",
    route: "/api/crs/describe",
    expect: { status: 400, code: "invalid.arguments" },
  },
  {
    id: "demo-filter",
    label: "Unsupported attribute filter",
    detail: "The bbox-only demo store rejects attribute filters as invalid.arguments.",
    method: "POST",
    route: "/api/features/query?store=demo",
    expect: { status: 400, code: "invalid.arguments" },
  },
  {
    id: "unknown-store",
    label: "Unknown store",
    detail: "Naming a store the registry never registered fails as invalid.arguments.",
    method: "GET",
    route: "/api/catalogue?store=chaos-no-such-store",
    expect: { status: 400, code: "invalid.arguments" },
  },
  {
    id: "cancel-sleep",
    label: "Cancelled fetch",
    detail: "Aborting a long host sleep cancels client-side; the host maps the cut to 499 with no envelope.",
    method: "POST",
    route: "/api/demo/sleep",
    expect: { status: 499, code: null },
  },
];

/** Every chaos toggle, in display order. */
export function chaosScenarios(): ChaosScenario[] {
  return Scenarios;
}

/** The descriptor for one toggle id, or undefined. */
export function chaosScenarioById(id: string): ChaosScenario | undefined {
  return Scenarios.find((scenario) => scenario.id === id);
}

/**
 * The invalid.arguments storm (T-074): the 400 toggles run as one batch,
 * proving the client-error mapping holds across routes, not just once.
 */
export function stormScenarioIds(): string[] {
  return Scenarios.filter((scenario) => scenario.expect.status === 400).map((scenario) => scenario.id);
}

/** The host ErrorResponse shape: `{code, message}`, extra keys allowed. */
export interface ErrorEnvelope {
  code: string;
  message: string;
}

export function isErrorEnvelope(value: unknown): value is ErrorEnvelope {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const candidate = value as Record<string, unknown>;
  return typeof candidate.code === "string" && typeof candidate.message === "string";
}

export type ChaosFailureKind = "api" | "aborted" | "other";

export interface ChaosFailure {
  kind: ChaosFailureKind;
  status: number | null;
  code: string | null;
  message: string;
}

/**
 * Sorts a caught failure without importing the SDK (duck-typed, so the
 * unit tests can feed plain objects): SpatialApiError-likes carry a
 * numeric `status`; an AbortError is a cancellation; anything else is
 * opaque (network down, bug in the harness).
 */
export function summarizeFailure(error: unknown): ChaosFailure {
  if (typeof error === "object" && error !== null) {
    if ((error as { name?: unknown }).name === "AbortError") {
      return { kind: "aborted", status: null, code: null, message: "aborted" };
    }
    const candidate = error as { status?: unknown; code?: unknown; message?: unknown };
    if (typeof candidate.status === "number") {
      return {
        kind: "api",
        status: candidate.status,
        code: typeof candidate.code === "string" ? candidate.code : null,
        message: typeof candidate.message === "string" ? candidate.message : `the host failed with ${candidate.status}`,
      };
    }
  }
  return {
    kind: "other",
    status: null,
    code: null,
    message: error instanceof Error ? error.message : String(error),
  };
}

/**
 * Whether an observed status/code pair satisfies an expectation. A null
 * expected code (499) matches on status alone — the host answers no
 * envelope there by design.
 */
export function mappingMatches(
  expect: ChaosExpectation,
  actual: { status: number | null; code: string | null },
): boolean {
  if (actual.status === null || actual.status !== expect.status) return false;
  if (expect.code === null) return true;
  return actual.code === expect.code;
}

/** Renders an expectation as a badge string, e.g. "503 · store.unavailable". */
export function formatExpectation(expect: ChaosExpectation): string {
  return `${expect.status} · ${expect.code ?? "(no envelope)"}`;
}
