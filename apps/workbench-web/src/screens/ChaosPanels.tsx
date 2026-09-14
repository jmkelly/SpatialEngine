import { useEffect, useRef, useState } from "react";
import { createClient, hostBaseUrl } from "../api.ts";
import {
  chaosScenarioById,
  chaosScenarios,
  formatExpectation,
  isErrorEnvelope,
  mappingMatches,
  stormScenarioIds,
  summarizeFailure,
} from "../chaos.ts";

interface ChaosResult {
  /** The observed HTTP status (null when the request never got an answer). */
  status: number | null;
  /** The observed ErrorResponse code (null when there was no envelope). */
  code: string | null;
  message: string;
  /** The raw envelope (or status-only failure) shown as JSON. */
  raw: unknown;
  /** Whether the body had the {code, message} ErrorResponse shape. */
  envelopeOk: boolean;
  pass: boolean;
  /** Honest-reporting note when the host legitimately disagrees (e.g. PostGIS configured). */
  note: string | null;
}

type Phase = "idle" | "running";

/**
 * Visual conformance harness D (T-074): one toggle per typed failure.
 * Every toggle drives a public host route through the SDK (or plain fetch
 * for the deliberately malformed body the SDK cannot construct) and
 * asserts the typed mapping — 400/404/503 plus the ErrorResponse shape,
 * 499 for the aborted fetch. The screen holds no spatial logic: requests
 * out, envelopes in.
 */
export function ChaosPanels() {
  const client = useRef(createClient()).current;
  const abortRef = useRef<AbortController | null>(null);
  const [phases, setPhases] = useState<Record<string, Phase>>({});
  const [results, setResults] = useState<Record<string, ChaosResult>>({});

  useEffect(() => () => abortRef.current?.abort(), []);

  const running = Object.values(phases).some((phase) => phase === "running");

  async function runOne(id: string): Promise<void> {
    const scenario = chaosScenarioById(id);
    if (scenario === undefined || phases[id] === "running") return;
    setPhases((current) => ({ ...current, [id]: "running" }));
    try {
      const result = await probe(client, id);
      setResults((current) => ({ ...current, [id]: result }));
    } finally {
      setPhases((current) => ({ ...current, [id]: "idle" }));
    }
  }

  async function runMany(ids: string[]): Promise<void> {
    for (const id of ids) {
      // eslint-disable-next-line no-await-in-loop
      await runOne(id);
    }
  }

  const done = Object.keys(results).length;
  const passed = Object.values(results).filter((result) => result.pass).length;

  return (
    <div>
      <p className="muted small">
        Simulation toggles proving the typed error mapping: each toggle drives one public host
        route and asserts its HTTP status plus the <code>{"{code, message}"}</code> ErrorResponse
        shape. The cancelled fetch aborts client-side — the host answers 499 with no envelope.
      </p>
      <div className="parity-controls">
        <div className="field parity-apply-field">
          <span aria-hidden="true" />
          <div className="chaos-actions">
            <button className="primary" data-testid="chaos-run-all" disabled={running} onClick={() => void runMany(chaosScenarios().map((scenario) => scenario.id))}>
              run all
            </button>
            <button data-testid="chaos-run-storm" disabled={running} onClick={() => void runMany(stormScenarioIds())}>
              run invalid.arguments storm
            </button>
            <span className="muted small" data-testid="chaos-tally">
              {done === 0 ? "not run yet" : `${passed}/${done} toggles match`}
            </span>
          </div>
        </div>
      </div>
      <div className="chaos-grid">
        {chaosScenarios().map((scenario) => {
          const result = results[scenario.id] ?? null;
          const phase = phases[scenario.id] ?? "idle";
          return (
            <article key={scenario.id} className="chaos-card" data-testid={`chaos-card-${scenario.id}`}>
              <header className="chaos-head">
                <div>
                  <b>{scenario.label}</b>{" "}
                  <code className="muted small">{scenario.method} {scenario.route}</code>
                </div>
                <span className="chaos-expect" data-testid={`chaos-expect-${scenario.id}`} title="expected mapping">
                  → {formatExpectation(scenario.expect)}
                </span>
              </header>
              <p className="muted small">{scenario.detail}</p>
              <div>
                <button data-testid={`chaos-run-${scenario.id}`} disabled={phase === "running" || running} onClick={() => void runOne(scenario.id)}>
                  {phase === "running" ? "running…" : "run toggle"}
                </button>
              </div>
              {result !== null && <ChaosResultView id={scenario.id} result={result} />}
            </article>
          );
        })}
      </div>
    </div>
  );
}

function ChaosResultView({ id, result }: { id: string; result: ChaosResult }) {
  return (
    <div className={`chaos-result ${result.pass ? "ok" : "fail"}`} data-testid={`chaos-result-${id}`}>
      <div className="chaos-result-head">
        <span className={`state ${result.pass ? "completed" : "failed"}`} data-testid={`chaos-status-${id}`}>
          {result.pass ? "mapping matches" : "mapping differs"}
        </span>
        <code className="small">
          got {result.status ?? "no answer"}{result.code ? ` · ${result.code}` : ""}{result.envelopeOk ? " · envelope ✓" : ""}
        </code>
      </div>
      <div className="muted small" data-testid={`chaos-message-${id}`}>{result.message}</div>
      {result.note !== null && <div className="muted small" data-testid={`chaos-note-${id}`}>{result.note}</div>}
      <pre className="parity-pre" data-testid={`chaos-envelope-${id}`}>{JSON.stringify(result.raw, null, 2)}</pre>
    </div>
  );
}

type ProbeClient = ReturnType<typeof createClient>;

/** Drives one toggle's public host route and assesses the observed mapping. */
async function probe(client: ProbeClient, id: string): Promise<ChaosResult> {
  const scenario = chaosScenarioById(id);
  if (scenario === undefined) {
    return fail(null, null, `unknown chaos toggle ${id}`, null, false, null);
  }
  try {
    switch (id) {
      case "postgis-down": {
        const catalogue = await client.catalogue("postgis");
        return fail(200, null, `the host answered 200 with ${catalogue.datasets.length} datasets`, catalogue, false,
          "PostGIS is configured on this host, so there is no outage to map — the toggle reports honestly instead of forcing 503.");
      }
      case "missing-dataset":
        await client.describeDataset("chaos-no-such-dataset", "demo");
        return fail(200, null, "the host described a dataset that should not exist", null, false, null);
      case "missing-map":
        await client.getMap("chaos-no-such-map");
        return fail(200, null, "the host returned a map that should not exist", null, false, null);
      case "bad-geometry":
        return await probeMalformedBuffer();
      case "bad-crs":
        await client.describeCrs("NOT-A-CRS");
        return fail(200, null, "the host described a CRS that should not parse", null, false, null);
      case "demo-filter":
        await client.query("demo.points", { filter: "name = 'chaos'" }, "demo");
        return fail(200, null, "the demo store accepted an attribute filter it should reject", null, false, null);
      case "unknown-store":
        await client.catalogue("chaos-no-such-store");
        return fail(200, null, "the host listed a store that should not exist", null, false, null);
      case "cancel-sleep": {
        const abort = new AbortController();
        const timer = setTimeout(() => abort.abort(), 250);
        try {
          await client.sleep(15000, abort.signal);
          return fail(200, null, "the sleep finished despite the abort", null, false, null);
        } catch (error) {
          const failure = summarizeFailure(error);
          if (failure.kind === "aborted") {
            return {
              status: 499,
              code: null,
              message: "the fetch aborted client-side; the host maps the cut to 499 with no envelope",
              raw: { status: 499, code: null, note: "ErrorMapper returns a bare 499 Client Closed Request — no ErrorResponse body by design" },
              envelopeOk: false,
              pass: true,
              note: null,
            };
          }
          return assess(scenario, failure.status, failure.code, failure.message, { status: failure.status, code: failure.code, message: failure.message }, false);
        } finally {
          clearTimeout(timer);
        }
      }
      default:
        return fail(null, null, `no probe drives chaos toggle ${id}`, null, false, null);
    }
  } catch (error) {
    const failure = summarizeFailure(error);
    if (failure.kind === "aborted") {
      return fail(null, null, "the request was aborted outside the cancellation toggle", null, false, null);
    }
    if (failure.kind === "other") {
      return fail(null, null, failure.message, { message: failure.message }, false,
        "No HTTP answer arrived — the host may be unreachable. Start it before trusting this toggle.");
    }
    const raw = { status: failure.status, code: failure.code, message: failure.message };
    return assess(scenario, failure.status, failure.code, failure.message, raw, isErrorEnvelope({ code: failure.code, message: failure.message }) && failure.code !== null);
  }
}

/**
 * The malformed-body probe: the SDK always Base64-encodes bytes, so the
 * not-SGEOM payload goes out as plain fetch — still the public host route.
 */
async function probeMalformedBuffer(): Promise<ChaosResult> {
  const scenario = chaosScenarioById("bad-geometry")!;
  const response = await fetch(`${hostBaseUrl()}/api/geometry/buffer`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ geometry: "!!!not-base64-sgeom!!!", distance: 1 }),
  });
  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }
  if (response.ok) {
    return fail(200, null, "the host buffered bytes that should not decode", body, false, null);
  }
  const envelope = isErrorEnvelope(body) ? body : null;
  return assess(
    scenario,
    response.status,
    envelope?.code ?? null,
    envelope?.message ?? `the host failed with ${response.status}`,
    body,
    envelope !== null,
  );
}

function assess(
  scenario: NonNullable<ReturnType<typeof chaosScenarioById>>,
  status: number | null,
  code: string | null,
  message: string,
  raw: unknown,
  envelopeOk: boolean,
): ChaosResult {
  return {
    status,
    code,
    message,
    raw,
    envelopeOk: scenario.expect.code === null ? true : envelopeOk,
    pass: mappingMatches(scenario.expect, { status, code }),
    note: null,
  };
}

function fail(
  status: number | null,
  code: string | null,
  message: string,
  raw: unknown,
  envelopeOk: boolean,
  note: string | null,
): ChaosResult {
  return { status, code, message, raw, envelopeOk, pass: false, note };
}
