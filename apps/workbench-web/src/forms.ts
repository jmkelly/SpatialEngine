import type { CapabilityDetailDto } from "@spatial/client";

/**
 * The workbench's generated capability forms (Phase 10): each capability's
 * input schema is a stable shape name (host-api.md / ADR-0030 — the exact
 * interchange is defined by the plugin SDK contracts), so the workbench maps
 * the known shapes to declarative field descriptors and renders a form from
 * them. Unknown shapes fall back to a raw JSON argument editor — every
 * capability stays invocable, and the known shapes get first-class inputs.
 */

export type FieldKind = "text" | "number" | "int" | "geometry" | "select" | "json";

export interface FormField {
  /** The argument name on the wire. */
  name: string;
  label: string;
  kind: FieldKind;
  required: boolean;
  hint?: string;
  /** For select fields: the allowed values (e.g. catalogue dataset ids). */
  options?: string[];
  /** A sensible default the form pre-fills. */
  defaultValue?: unknown;
}

/** The capability shape the form builder reads (summary or detail DTO). */
interface CapabilityShape {
  id: string;
  inputSchema: string;
  purpose: string;
}

/** Returns the form fields for a capability, or null when the schema is empty. */
export function fieldsFor(capability: CapabilityShape, context: FormContext): FormField[] {
  switch (capability.id) {
    // ---- Geometry operations (ADR-0026) ----
    case "spatial.geometry.buffer@1":
      return [
        geometryField("geometry", context),
        { name: "distance", label: "Distance", kind: "number", required: true, defaultValue: 1 },
        { name: "quadrantSegments", label: "Quadrant segments", kind: "int", required: false, hint: "Optional circle fidelity" },
      ];
    case "spatial.geometry.simplify@1":
      return [
        geometryField("geometry", context),
        { name: "tolerance", label: "Tolerance", kind: "number", required: true, defaultValue: 0.5 },
      ];
    case "spatial.geometry.intersection@1":
      return [
        geometryField("left", context),
        geometryField("right", context, true),
        { name: "rightGeometry", label: "Right geometry (bytes)", kind: "json", required: false, hint: "Or pick a second geometry source" },
      ];
    case "spatial.geometry.validate@1":
      return [geometryField("geometry", context)];

    // ---- Data provider contracts (ADR-0028) ----
    case "spatial.catalogue.list@1":
      return [{ name: "pattern", label: "Pattern", kind: "text", required: false, hint: "LIKE-style filter, e.g. demo.%" }];
    case "spatial.dataset.describe@1":
      return [datasetField(context)];
    case "spatial.feature.scan@1":
    case "spatial.feature.query@1":
      return [
        datasetField(context),
        { name: "minx", label: "Min X", kind: "number", required: false, hint: "Bounding box (all-or-none)" },
        { name: "miny", label: "Min Y", kind: "number", required: false },
        { name: "maxx", label: "Max X", kind: "number", required: false },
        { name: "maxy", label: "Max Y", kind: "number", required: false },
      ];
    case "spatial.dataset.create@1":
    case "spatial.feature.write@1":
      return [
        datasetField(context),
        { name: "batch", label: "Feature batch (SFBAT bytes)", kind: "json", required: true, hint: "Canonical batch bytes" },
      ];
    case "spatial.transaction.begin@1":
      return [];
    case "spatial.transaction.commit@1":
    case "spatial.transaction.rollback@1":
      return [{ name: "transaction", label: "Transaction handle", kind: "json", required: true, hint: "The handle from begin" }];

    // ---- Long-running progress demos ----
    case "spatial.demo.sleep@1":
    case "spatial.fixture.sleep@1":
    case "spatial.fixture.timeout@1":
      return [{ name: "milliseconds", label: "Milliseconds", kind: "int", required: false, defaultValue: 1000 }];

    default:
      return [{ name: "arguments", label: "Arguments (JSON)", kind: "json", required: false, hint: capability.inputSchema }];
  }
}

function geometryField(name: string, context: FormContext, allowEmpty = false): FormField {
  return {
    name,
    label: name === "geometry" ? "Geometry" : `${name[0]!.toUpperCase()}${name.slice(1)} geometry`,
    kind: "geometry",
    required: !allowEmpty,
    hint: context.selectedGeometryLabel
      ? `From selection: ${context.selectedGeometryLabel}`
      : "No geometry selected — pick a feature on the map",
  };
}

function datasetField(context: FormContext): FormField {
  return {
    name: "dataset",
    label: "Dataset",
    kind: "select",
    required: true,
    options: context.catalogDatasets.length > 0 ? context.catalogDatasets : ["demo.points"],
    hint: context.catalogDatasets.length === 0 ? "No catalogue provider connected — using the demo dataset" : undefined,
  };
}

export interface FormContext {
  /** The ids of datasets discovered from the catalogue. */
  catalogDatasets: string[];
  /** A human label for the currently selected geometry (its feature id). */
  selectedGeometryLabel?: string;
}

/** Errors the form surfaces per field name. */
export interface InvokeFormValues {
  [name: string]: unknown;
}

/** Renders a human label for a capability's providers/state (runtime-status). */
export function providerStateLabel(state: string): string {
  switch (state) {
    case "Active":
      return "active";
    case "Healthy":
      return "healthy";
    case "Draining":
      return "draining";
    case "Stopped":
      return "stopped";
    case "Failed":
      return "failed";
    default:
      return state.toLowerCase();
  }
}