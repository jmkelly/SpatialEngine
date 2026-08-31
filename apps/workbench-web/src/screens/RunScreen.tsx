import { useMemo, useState } from "react";
import type { CapabilityDetailDto, CapabilitySummaryDto } from "@spatial/client";
import { fieldsFor, type FormField, type FormContext } from "../forms.ts";
import { buildWireArguments } from "../wire-args.ts";
import { useWorkbench, type RunOutcome } from "../state.tsx";
import { OutcomeView } from "./OverviewScreen.tsx";

/**
 * The capability panel (Phase 10): pick a capability, fill its generated
 * form (geometry args come from the map selection), invoke — inline or as a
 * job with progress — then preview and persist the result. Persistence is
 * browser-side (plan §17.8 "preview and persist the result").
 */
export function RunScreen() {
  const { capabilities, catalogueDatasets, selectedFeatureId, selectedGeometryWire, savedResults, runningJobs, recentOutcome, actions } = useWorkbench();
  const [capabilityId, setCapabilityId] = useState<string>("");
  const [formValues, setFormValues] = useState<Record<string, unknown>>({});
  const [outcome, setOutcome] = useState<RunOutcome | null>(null);
  const [saveName, setSaveName] = useState("result");
  const [saveNote, setSaveNote] = useState("");
  const [savedFlash, setSavedFlash] = useState(false);

  const capability = useMemo(
    () => capabilities.find((candidate) => candidate.id === capabilityId) ?? null,
    [capabilities, capabilityId],
  );

  const formContext: FormContext = {
    catalogDatasets: catalogueDatasets,
    selectedGeometryLabel: selectedFeatureId === null ? undefined : selectedFeatureId,
  };
  const fields = capability ? fieldsFor(capability, formContext) : [];

  async function invoke() {
    if (capability === null) return;
    const request = buildWireArguments(fields, formValues, selectedGeometryWire);
    const result = await actions.runInvocation(capability.id, request, permissionsFor(capability));
    setOutcome(result);
  }

  function selectCapability(id: string) {
    setCapabilityId(id);
    setOutcome(null);
    const selected = capabilities.find((candidate) => candidate.id === id);
    if (selected === undefined) return;
    const defaults: Record<string, unknown> = {};
    for (const field of fieldsFor(selected, formContext)) {
      if (field.defaultValue !== undefined) defaults[field.name] = field.defaultValue;
    }
    setFormValues(defaults);
  }

  function persist() {
    if (outcome === null) return;
    actions.persistResult(outcome, saveName.trim() || "result", saveNote.trim());
    setSavedFlash(true);
    setTimeout(() => setSavedFlash(false), 1500);
  }

  return (
    <section className="screen run-screen">
      <div className="run-layout">
        <div className="run-left">
          <h2>Capability forms</h2>
          <label className="field-label" htmlFor="capability-select">Capability</label>
          <select
            id="capability-select"
            data-testid="capability-select"
            value={capabilityId}
            onChange={(event) => selectCapability(event.target.value)}
          >
            <option value="" disabled>choose a capability…</option>
            {capabilities.map((item) => (
              <option key={item.id} value={item.id}>{item.id}</option>
            ))}
          </select>
          {capability !== null && (
            <div className="form">
              <p className="muted">{capability.purpose}</p>
              {fields.length === 0
                ? <p className="muted">This capability takes no arguments.</p>
                : fields.map((field) => (
                  <FieldInput
                    key={field.name}
                    field={field}
                    value={formValues[field.name]}
                    selectedGeometryWire={selectedGeometryWire}
                    onChange={(value) => setFormValues((current) => ({ ...current, [field.name]: value }))}
                  />
                ))}
              <div className="form-actions">
                <button
                  className="primary"
                  data-testid="invoke-button"
                  disabled={selectedGeometryWire === null && fields.some((field) => field.kind === "geometry" && field.required)}
                  onClick={() => void invoke()}
                >
                  {runningJobs.length > 0 ? "running…" : "Invoke"}
                </button>
                {runningJobs.length > 0 && <JobProgress jobs={runningJobs} />}
              </div>
            </div>
          )}
        </div>

        <div className="run-right">
          <h2>Result preview &amp; persistence</h2>
          {outcome === null ? (
            <p className="muted">Invoke a capability to preview its result here. Geometry results render on the Map tab (yellow layer) and can be saved below.</p>
          ) : (
            <>
              <OutcomeView outcome={outcome} />
              {outcome.ok && (
                <div className="save-box">
                  <input
                    data-testid="save-name"
                    value={saveName}
                    onChange={(event) => setSaveName(event.target.value)}
                    placeholder="result name"
                    aria-label="result name"
                  />
                  <input
                    data-testid="save-note"
                    value={saveNote}
                    onChange={(event) => setSaveNote(event.target.value)}
                    placeholder="note (optional)"
                    aria-label="note"
                  />
                  <button data-testid="save-button" onClick={persist} disabled={outcome.result === null && saveName.trim() === ""}>
                    {savedFlash ? "✓ saved" : "Save result"}
                  </button>
                </div>
              )}
            </>
          )}

          <h3>Saved results ({savedResults.length})</h3>
          {savedResults.length === 0 ? (
            <p className="muted">Nothing persisted yet — results survive reloads in this browser.</p>
          ) : (
            <ul className="saved-results">
              {savedResults.map((saved) => (
                <li key={saved.id} data-testid="saved-result">
                  <div>
                    <strong>{saved.name}</strong> <span className="muted">· {saved.capability}{saved.provider ? ` · ${saved.provider}` : ""}</span>
                    <div className="muted small">{new Date(saved.savedAt).toLocaleString()}{saved.note ? ` — ${saved.note}` : ""}</div>
                  </div>
                  <button className="ghost" onClick={() => actions.removeResult(saved.id)}>remove</button>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </section>
  );
}

function permissionsFor(capability: CapabilitySummaryDto): string[] {
  return capability.requiredPermissions;
}

/** Turns form values into wire arguments (geometry tags, $i64 int64s). */
export { buildWireArguments };

function FieldInput({ field, value, selectedGeometryWire, onChange }: {
  field: FormField;
  value: unknown;
  selectedGeometryWire: unknown;
  onChange: (value: unknown) => void;
}) {
  switch (field.kind) {
    case "geometry":
      return (
        <label className="field geometry-field" data-testid={`field-${field.name}`}>
          <span>{field.label} {field.required && <b>*</b>}</span>
          <input
            type="text"
            readOnly
            value={selectedGeometryWire !== null && selectedGeometryWire !== undefined ? "selected ✓" : "none selected"}
            placeholder="pick a feature on the map"
          />
          {field.hint && <small className="muted">{field.hint}</small>}
        </label>
      );
    case "select":
      return (
        <label className="field">
          <span>{field.label} {field.required && <b>*</b>}</span>
          <select value={typeof value === "string" ? value : ""} onChange={(event) => onChange(event.target.value)}>
            <option value="" disabled>choose…</option>
            {(field.options ?? []).map((option) => <option key={option} value={option}>{option}</option>)}
          </select>
          {field.hint && <small className="muted">{field.hint}</small>}
        </label>
      );
    case "json":
      return (
        <label className="field">
          <span>{field.label}</span>
          <textarea value={typeof value === "string" ? value : ""} onChange={(event) => onChange(event.target.value)} rows={2} placeholder='{"example": 1}' />
          {field.hint && <small className="muted">{field.hint}</small>}
        </label>
      );
    default:
      return (
        <label className="field">
          <span>{field.label} {field.required && <b>*</b>}</span>
          <input
            type={field.kind === "number" || field.kind === "int" ? "number" : "text"}
            value={value === undefined ? String(field.defaultValue ?? "") : String(value)}
            onChange={(event) => onChange(event.target.value)}
          />
          {field.hint && <small className="muted">{field.hint}</small>}
        </label>
      );
  }
}

function JobProgress({ jobs }: { jobs: string[] }) {
  return (
    <div className="job-progress" data-testid="job-progress">
      <span className="spinner" />
      watching job(s): {jobs.slice(0, 3).join(", ")}
    </div>
  );
}