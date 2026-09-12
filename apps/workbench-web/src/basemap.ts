/**
 * Basemap selection shared by the workbench map screens: the stored/URL
 * choice and the no-key raster tile sources behind engine data. The `none`
 * option keeps the offline dark canvas so browser tests and locked-down
 * environments never need network tiles.
 */

/** The background raster behind engine data. `?basemap=none` (or the stored choice) keeps the offline dark canvas. */
export type Basemap = "dark" | "light" | "none";

const BasemapStorageKey = "spatial:basemap";

/** The basemap selected by the URL query, then localStorage, else `dark`. */
export function initialBasemap(): Basemap {
  try {
    const param = new URLSearchParams(window.location.search).get("basemap");
    if (param === "dark" || param === "light" || param === "none") return param;
    const stored = window.localStorage.getItem(BasemapStorageKey);
    if (stored === "dark" || stored === "light" || stored === "none") return stored;
  } catch {
    // Storage or URL access can fail in locked-down browsers — fall back to the dark map.
  }
  return "dark";
}

/** Remembers the basemap choice, tolerating browsers that refuse storage. */
export function rememberBasemap(basemap: Basemap): void {
  try {
    window.localStorage.setItem(BasemapStorageKey, basemap);
  } catch {
    // Locked-down browsers may refuse storage — the map still switches.
  }
}

/**
 * The MapLibre source for the chosen basemap. Esri's Canvas gray basemaps
 * need no API key (CARTO's free tiles now print an "API KEY REQUIRED"
 * watermark). Note Esri's `{z}/{y}/{x}` order. `none` contributes no source.
 */
export function basemapSource(basemap: Basemap): Record<string, { type: "raster"; tiles: string[]; tileSize: number; maxzoom: number; attribution: string }> {
  if (basemap === "none") return {};
  const service = basemap === "dark" ? "Canvas/World_Dark_Gray_Base" : "Canvas/World_Light_Gray_Base";
  return {
    basemap: {
      type: "raster",
      tiles: [`https://server.arcgisonline.com/ArcGIS/rest/services/${service}/MapServer/tile/{z}/{y}/{x}`],
      tileSize: 256,
      maxzoom: 16,
      attribution: "© OpenStreetMap contributors © Esri",
    },
  };
}
