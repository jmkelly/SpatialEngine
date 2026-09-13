import type { LayerSpecification } from "maplibre-gl";
import type { Publication, PublicationKind, PublicationLayer } from "@spatial/client";
import { newId } from "./ids.ts";

/**
 * The composer's pure model: the
 * ordered layer list, the MapLibre style for each layer, and the mapping to
 * and from the host's neutral `Publication` contract. Deliberately free of
 * React and of the SDK transport so Node can type-strip it and the unit
 * tests pin the mapping without a renderer.
 *
 * Per-layer style is authored in browser state and persisted with the
 * publication as a MapLibre style fragment on each layer (ADR-0047), so a
 * publish/load round-trip restores it instead of resetting it.
 */

/** The geometry family a layer's features share; `mixed` draws every symbol kind. */
export type GeometryKind = "point" | "line" | "polygon" | "mixed";

/** The editable MapLibre-derived style of one composer layer. */
export interface LayerStyle {
  color: string;
  opacity: number;
  lineWidth: number;
  radius: number;
  visible: boolean;
}

/** One dataset placed on the composer map. */
export interface ComposerLayer {
  /** Stable client identity (never published; used to key sources and features). */
  id: string;
  store: string;
  dataset: string;
  name: string;
  geometry: GeometryKind;
  style: LayerStyle;
  featureCount: number;
  /** The published layer id when loaded from a publication; null for a new layer. */
  layerId: number | null;
}

/** The editable state behind one publication. */
export interface ComposerDraft {
  name: string;
  kind: PublicationKind;
  store: string;
  layers: ComposerLayer[];
}

export const StyleDefaults: LayerStyle = { color: "#4fc3f7", opacity: 0.3, lineWidth: 2, radius: 5, visible: true };

const Palette = ["#4fc3f7", "#ffd54f", "#81c784", "#f06292", "#ba68c8", "#4db6ac", "#ff8a65", "#9575cd"];

/** A sensible starting style for a geometry family, cycling a distinct palette by list position. */
export function defaultStyle(geometry: GeometryKind, index = 0): LayerStyle {
  const color = Palette[index % Palette.length] ?? StyleDefaults.color;
  switch (geometry) {
    case "point":
      return { ...StyleDefaults, color, radius: 6, opacity: 0.9 };
    case "line":
      return { ...StyleDefaults, color, lineWidth: 2, opacity: 0.9 };
    case "polygon":
      return { ...StyleDefaults, color, opacity: 0.3 };
    default:
      return { ...StyleDefaults, color };
  }
}

/** Maps one GeoJSON geometry type onto its family, or null for unsupported/unknown. */
export function kindOfGeometryType(type: string): Exclude<GeometryKind, "mixed"> | null {
  switch (type) {
    case "Point":
    case "MultiPoint":
      return "point";
    case "LineString":
    case "MultiLineString":
      return "line";
    case "Polygon":
    case "MultiPolygon":
      return "polygon";
    default:
      return null;
  }
}

/** Infers a single family from the geometry types present; a mix (or nothing) is `mixed`. */
export function inferGeometryKind(types: readonly string[]): GeometryKind {
  const kinds = new Set<Exclude<GeometryKind, "mixed">>();
  for (const type of types) {
    const kind = kindOfGeometryType(type);
    if (kind !== null) kinds.add(kind);
  }

  if (kinds.size === 1) return [...kinds][0]!;
  return "mixed";
}

/** The geometry types of every feature (a null geometry is reported as `Unknown`). */
export function geometryTypesOf(collection: GeoJSON.FeatureCollection<GeoJSON.Geometry | null>): string[] {
  return collection.features.map((feature) => feature.geometry?.type ?? "Unknown");
}

/** Appends a layer to the end of the list (the top of the draw order). */
export function addLayer(layers: readonly ComposerLayer[], layer: ComposerLayer): ComposerLayer[] {
  return [...layers, layer];
}

/** Removes a layer by its client id. */
export function removeLayer(layers: readonly ComposerLayer[], id: string): ComposerLayer[] {
  return layers.filter((layer) => layer.id !== id);
}

/**
 * Moves the layer at `from` to `to` (indices into the current list). An
 * out-of-range or no-op move returns the list unchanged. Pure so the drag
 * handlers and the tests share one definition of "reorder".
 */
export function reorderLayers(layers: readonly ComposerLayer[], from: number, to: number): ComposerLayer[] {
  if (from === to || from < 0 || to < 0 || from >= layers.length || to >= layers.length) return [...layers];
  const next = [...layers];
  const [moved] = next.splice(from, 1);
  if (moved === undefined) return [...layers];
  next.splice(to, 0, moved);
  return next;
}

/** Merges a style patch into one layer. */
export function updateLayerStyle(layers: readonly ComposerLayer[], id: string, patch: Partial<LayerStyle>): ComposerLayer[] {
  return layers.map((layer) => (layer.id === id ? { ...layer, style: { ...layer.style, ...patch } } : layer));
}

/** Renames one layer (empty stays empty and is rejected by the host on publish). */
export function updateLayerName(layers: readonly ComposerLayer[], id: string, name: string): ComposerLayer[] {
  return layers.map((layer) => (layer.id === id ? { ...layer, name } : layer));
}

/**
 * The MapLibre style layers for one composer layer. Point-only and line-only
 * layers emit one layer; polygons emit a fill plus an outline; `mixed` emits
 * all three so a heterogeneous dataset still previews.
 */
export function layerSpecs(layer: ComposerLayer, sourceId: string): LayerSpecification[] {
  const { color, opacity, lineWidth, radius, visible } = layer.style;
  const visibility = visible ? "visible" : "none";
  const specs: LayerSpecification[] = [];

  if (layer.geometry === "polygon" || layer.geometry === "mixed") {
    specs.push({
      id: `${layer.id}-fill`,
      type: "fill",
      source: sourceId,
      layout: { visibility },
      paint: { "fill-color": color, "fill-opacity": opacity, "fill-outline-color": color },
    });
  }

  if (layer.geometry === "line" || layer.geometry === "polygon" || layer.geometry === "mixed") {
    specs.push({
      id: `${layer.id}-line`,
      type: "line",
      source: sourceId,
      layout: { visibility },
      paint: { "line-color": color, "line-width": lineWidth, "line-opacity": opacity },
    });
  }

  if (layer.geometry === "point" || layer.geometry === "mixed") {
    specs.push({
      id: `${layer.id}-circle`,
      type: "circle",
      source: sourceId,
      layout: { visibility },
      paint: {
        "circle-color": color,
        "circle-radius": radius,
        "circle-opacity": opacity,
        "circle-stroke-color": "#0b0f14",
        "circle-stroke-width": 1,
      },
    });
  }

  return specs;
}

/** The MapLibre paint/layout a persisted style fragment carries per layer. */
interface PersistedSpec {
  type?: string;
  layout?: { visibility?: string };
  paint?: Record<string, string | number>;
}

/**
 * The layer's persisted style (ADR-0047): the same MapLibre draw recipe as
 * the preview, stripped of `id`/`source` so the host injects them (plus
 * `source-layer`) when it assembles a render document.
 */
export function persistedStyle(layer: ComposerLayer): string {
  const fragments = layerSpecs(layer, "").map((spec) => {
    const fragment: Record<string, unknown> = { ...(spec as Record<string, unknown>) };
    delete fragment["id"];
    delete fragment["source"];
    return fragment;
  });
  return JSON.stringify(fragments);
}

/**
 * Rebuilds a composer {@link LayerStyle} from a persisted style fragment,
 * falling back to `fallback` for an absent or unparseable value. Missing
 * paint fields keep the fallback value, so a fragment authored elsewhere
 * still edits sensibly.
 */
export function styleFromPersisted(value: string | null | undefined, fallback: LayerStyle): LayerStyle {
  const specs = parsePersistedSpecs(value);
  const first = specs[0];
  if (first === undefined) return fallback;

  const paintOf = (type: string): Record<string, string | number> =>
    specs.find((spec) => spec.type === type)?.paint ?? {};
  const fill = paintOf("fill");
  const line = paintOf("line");
  const circle = paintOf("circle");

  return {
    color: firstString(line["line-color"], fill["fill-color"], circle["circle-color"]) ?? fallback.color,
    opacity: firstNumber(fill["fill-opacity"], line["line-opacity"], circle["circle-opacity"]) ?? fallback.opacity,
    lineWidth: firstNumber(line["line-width"]) ?? fallback.lineWidth,
    radius: firstNumber(circle["circle-radius"]) ?? fallback.radius,
    visible: (first.layout?.visibility ?? "visible") !== "none",
  };
}

function parsePersistedSpecs(value: string | null | undefined): PersistedSpec[] {
  if (value === null || value === undefined || value === "") return [];
  try {
    const parsed: unknown = JSON.parse(value);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((spec): spec is PersistedSpec => typeof spec === "object" && spec !== null);
  } catch {
    return [];
  }
}

function firstString(...values: Array<string | number | undefined>): string | undefined {
  return values.find((value): value is string => typeof value === "string");
}

function firstNumber(...values: Array<string | number | undefined>): number | undefined {
  return values.find((value): value is number => typeof value === "number");
}

/** The GeoJSON source id behind a composer layer. */
export function sourceId(layerId: string): string {
  return `composer-src-${layerId}`;
}

/**
 * Maps the ordered layer list onto the host's publication layers. Order is
 * preserved; `layerId` is the loaded stable id or `-1`, the registry's
 * "assign the next free id" sentinel, so appended layers never renumber
 * existing ones (ADR-0041).
 */
export function toPublicationLayers(layers: readonly ComposerLayer[]): PublicationLayer[] {
  return layers.map((layer) => ({
    dataset: layer.dataset,
    layerId: layer.layerId ?? -1,
    name: layer.name,
    style: persistedStyle(layer),
  }));
}

/** Builds the `Publication` body for a draft. */
export function toPublication(draft: ComposerDraft): Publication {
  return { name: draft.name.trim(), kind: draft.kind, store: draft.store, layers: toPublicationLayers(draft.layers) };
}

/** Hydrates a draft from a stored publication, preserving stable layer ids (geometry is inferred after scanning). */
export function fromPublication(publication: Publication): ComposerDraft {
  return {
    name: publication.name,
    kind: publication.kind,
    store: publication.store,
    layers: publication.layers.map((layer, index) => ({
      id: newLayerId(),
      store: publication.store,
      dataset: layer.dataset,
      name: layer.name ?? layer.dataset,
      geometry: "mixed",
      style: styleFromPersisted(layer.style, defaultStyle("mixed", index)),
      featureCount: 0,
      layerId: toLayerId(layer.layerId),
    })),
  };
}

/** Generates a stable client id; see {@link newId} for insecure-context handling. */
export function newLayerId(): string {
  return newId();
}

function toLayerId(value: number | string): number | null {
  const numeric = typeof value === "number" ? value : Number.parseInt(value, 10);
  return Number.isFinite(numeric) ? numeric : null;
}
