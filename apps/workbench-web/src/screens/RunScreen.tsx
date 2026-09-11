import { useMemo, useState } from "react";
import { fieldsFor, operationById, operations, type FormField } from "../forms.ts";
import { useWorkbench, type RunOutcome } from "../state.tsx";
import { OutcomeView } from "./OverviewScreen.tsx";

/**
 * The operation panel: pick an operation, fill its form (geometry inputs
 * come from the map selection), run — cancellable while in flight — then
 * preview and persist the result. Persistence is browser-side.
 */
export function RunScreen() {
  const { catalogueDatasets, selectedFeatureId, selectedGeometryBase64, savedResults, running, recentOutcome, actions } = useWorkbench();
  const [opId, setOpId] = useState<string>("buffer");
  const [formValues, setFormValues] = useState<Record<string, unknown>>({ distance: 1, quadrantSegments: 8 });
  const [outcome, setOutcome] = useState<RunOutcome | null>(null);
  const [saveName, setSaveName] = useState("result");
  const [saveNote, setSaveNote] = useState("");
  const [savedFlash, setSavedFlash] = useState(false);

  const operation = useMemo(() => operationById(opId), [opId]);

  const formContext = {
    catalogDatasets: catalogueDatasets,
    selectedGeometryLabel: selectedFeatureId === null ? undefined : selectedFeatureId,
  };
  const fields = useMemo(() => fieldsFor(opId, formContext), [opId, catalogueDatasets, selectedFeatureId]);

  async function invoke() {
    if (operation === undefined) return;
    const result = await actions.runOperation(operation.id, formValues);
    setOutcome(result);
  }

  function selectOperation(id: string) {
    setOpId(id);
    setOutcome(null);
    const selected = operationById(id);
    if (selected === undefined) return;
    const defaults: Record<string, unknown> = {};
    for (const field of selected.fields(formContext)) {
      if (field.defaultValue !== undefined) defaults[field.name] = field.defaultValue;
    }
    setFormValues(defaults);
  }

  function persist() {
    // The preview falls back to the provider's recent outcome after a tab
    // switch (which unmounts this screen and clears the local `outcome`), so
    // saving must use the same result the preview shows.
    const result = outcome ?? recentOutcome;
    if (result === null) return;
    actions.persistResult(result, saveName.trim() || "result", saveNote.trim());
    setSavedFlash(true);
    setTimeout(() => setSavedFlash(false), 1500);
  }

  return (
    <section className="screen run-screen">
      <div className="run-layout">
        <div className="run-left">
          <h2>Operation forms</h2>
          <label className="field-label" htmlFor="capability-select">Operation</label>
          <select
            id="capability-select"
            data-testid="capability-select"
            value={opId}
            onChange={(event) => selectOperation(event.target.value)}
          >
            {operations().map((item) => (
              <option key={item.id} value={item.id}>{item.label}</option>
            ))}
          </select>
          {operation !== undefined && (
            <div className="form">
              <p className="muted">{operation.purpose}</p>
              {fields.map((field) => (
                <FieldInput
                  key={field.name}
                  field={field}
                  value={formValues[field.name]}
                  selectedGeometryBase64={selectedGeometryBase64}
                  onChange={(value) => setFormValues((current) => ({ ...current, [field.name]: value }))}
                />
              ))}
              <div className="form-actions">
                <button
                  className="primary"
                  data-testid="invoke-button"
                  disabled={running || (selectedGeometryBase64 === null && fields.some((field) => field.kind === "geometry"))}
                  onClick={() => void invoke()}
                >
                  {running ? "running…" : "Run"}
                </button>
                {running && (
                  <>
                    <div className="job-progress" data-testid="job-progress">
                      <span className="spinner" />
                      running…
                    </div>
                    <button className="danger" data-testid="cancel-button" onClick={() => actions.cancelRunning()}>
                      Cancel
                    </button>
                  </>
                )}
              </div>
            </div>
          )}
        </div>

        <div className="run-right">
          <h2>Result preview &amp; persistence</h2>
          {(outcome ?? recentOutcome) === null ? (
            <p className="muted">Run an operation to preview its result here. Geometry results render on the Map tab (yellow layer) and can be saved below; clear unsaved previews with “clear results” on the Map toolbar.</p>
          ) : (
            <>
              <OutcomeView outcome={(outcome ?? recentOutcome)!} />
              {(outcome ?? recentOutcome)!.ok && (
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
                  <button data-testid="save-button" onClick={persist} disabled={saveName.trim() === ""}>
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
                    <strong>{saved.name}</strong> <span className="muted">· {saved.op}{saved.summary ? ` · ${saved.summary}` : ""}</span>
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

function FieldInput({ field, value, selectedGeometryBase64, onChange }: {
  field: FormField;
  value: unknown;
  selectedGeometryBase64: string | null;
  onChange: (value: unknown) => void;
}) {
  switch (field.kind) {
    case "geometry":
      return (
        <label className="field geometry-field" data-testid={`field-${field.name}`}>
          <span>{field.label} <b>*</b></span>
          <input
            type="text"
            readOnly
            value={selectedGeometryBase64 !== null ? "selected ✓" : "none selected"}
            placeholder="pick a feature on the map"
          />
          {field.hint && <small className="muted">{field.hint}</small>}
        </label>
      );
    case "geometry-text":
      return (
        <label className="field">
          <span>{field.label} <b>*</b></span>
          <textarea value={typeof value === "string" ? value : ""} onChange={(event) => onChange(event.target.value)} rows={2} placeholder="base64 SGEOM bytes" />
          {field.hint && <small className="muted">{field.hint}</small>}
        </label>
      );
    case "select":
      return (
        <label className="field">
          <span>{field.label} <b>*</b></span>
          <select value={typeof value === "string" ? value : ""} onChange={(event) => onChange(event.target.value)}>
            <option value="" disabled>choose…</option>
            {(field.options ?? []).map((option) => <option key={option} value={option}>{option}</option>)}
          </select>
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
