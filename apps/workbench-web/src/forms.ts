/**
 * The workbench's operation forms (ADR-0033): one static descriptor per
 * typed host route. Geometry inputs come from the map selection; dataset
 * inputs from the catalogue; everything else is a scalar field.
 */

export type FieldKind = "text" | "number" | "int" | "geometry" | "geometry-text" | "select" | "json";

export interface FormField {
  /** The field name. */
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

export interface OperationDescriptor {
  id: string;
  label: string;
  purpose: string;
  fields: (context: FormContext) => FormField[];
}

export interface FormContext {
  /** The ids of datasets discovered from the catalogue. */
  catalogDatasets: string[];
  /** A human label for the currently selected geometry (its feature id). */
  selectedGeometryLabel?: string;
}

const Operations: OperationDescriptor[] = [
  {
    id: "buffer",
    label: "Buffer",
    purpose: "Expands or shrinks a geometry by a distance (negative distances erode).",
    fields: () => [
      geometryField("geometry"),
      { name: "distance", label: "Distance", kind: "number", required: true, defaultValue: 1 },
      { name: "quadrantSegments", label: "Quadrant segments", kind: "int", required: false, defaultValue: 8, hint: "Optional circle fidelity" },
    ],
  },
  {
    id: "intersection",
    label: "Intersection",
    purpose: "Returns the overlap of two geometries (empty when disjoint).",
    fields: () => [
      geometryField("geometry"),
      { name: "other", label: "Other geometry (base64 SGEOM)", kind: "geometry-text", required: true, hint: "Paste base64 SGEOM bytes, e.g. from a saved result" },
    ],
  },
  {
    id: "validate",
    label: "Validate",
    purpose: "Reports OGC validity — an invalid geometry is a successful false.",
    fields: () => [geometryField("geometry")],
  },
  {
    id: "simplify",
    label: "Simplify",
    purpose: "Douglas-Peucker simplification; zero tolerance returns the input unchanged.",
    fields: () => [
      geometryField("geometry"),
      { name: "tolerance", label: "Tolerance", kind: "number", required: true, defaultValue: 0.5 },
    ],
  },
  {
    id: "transform",
    label: "Transform",
    purpose: "Transforms a geometry to the target CRS (x-first convention).",
    fields: () => [
      geometryField("geometry"),
      { name: "target", label: "Target CRS", kind: "text", required: true, defaultValue: "EPSG:32632" },
      { name: "source", label: "Source CRS (optional)", kind: "text", required: false, hint: "Defaults to the geometry's own CRS" },
    ],
  },
  {
    id: "describe",
    label: "Describe CRS",
    purpose: "Describes one CRS identity (name, axes, datum, ellipsoid).",
    fields: () => [
      { name: "crs", label: "CRS", kind: "text", required: true, defaultValue: "EPSG:4326" },
    ],
  },
  {
    id: "scan",
    label: "Scan dataset",
    purpose: "Reads every feature of a dataset as canonical batches.",
    fields: (context) => [datasetField(context)],
  },
  {
    id: "query",
    label: "Query dataset",
    purpose: "Filters a dataset by bounding box and/or attribute expression.",
    fields: (context) => [
      datasetField(context),
      { name: "minx", label: "Min X", kind: "number", required: false, hint: "Bounding box (all-or-none)" },
      { name: "miny", label: "Min Y", kind: "number", required: false },
      { name: "maxx", label: "Max X", kind: "number", required: false },
      { name: "maxy", label: "Max Y", kind: "number", required: false },
      { name: "filter", label: "Attribute filter", kind: "text", required: false, hint: "PostGIS only, e.g. name = 'Berlin'" },
    ],
  },
  {
    id: "sleep",
    label: "Sleep (demo)",
    purpose: "A cancellable host-side delay — the progress and cancellation demo.",
    fields: () => [
      { name: "milliseconds", label: "Milliseconds", kind: "int", required: false, defaultValue: 1000 },
    ],
  },
];

/** Every operation the Run screen offers, in display order. */
export function operations(): OperationDescriptor[] {
  return Operations;
}

/** The descriptor for one operation id, or undefined. */
export function operationById(id: string): OperationDescriptor | undefined {
  return Operations.find((operation) => operation.id === id);
}

/** Returns the form fields for an operation. */
export function fieldsFor(operationId: string, context: FormContext): FormField[] {
  return operationById(operationId)?.fields(context) ?? [];
}

function geometryField(name: string): FormField {
  return {
    name,
    label: "Geometry",
    kind: "geometry",
    required: true,
    hint: "From the map selection",
  };
}

function datasetField(context: FormContext): FormField {
  return {
    name: "dataset",
    label: "Dataset",
    kind: "select",
    required: true,
    options: context.catalogDatasets.length > 0 ? context.catalogDatasets : ["demo.points", "demo.cities"],
  };
}
