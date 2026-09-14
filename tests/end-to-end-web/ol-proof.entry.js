// OpenLayers client-compat proof (T-068): genuine OL client against the host's
// public services — TileWMS pinned to 1.3.0 (CRS path), XYZ map tiles, and a
// vector layer assembled from two WFS GetFeature pages with srsName
// reprojection. Same-origin with the services (proof-server.mjs proxies /ogc
// + /api), so there is no CORS and no canvas taint. No app/spatial logic.
import Map from "ol/Map.js";
import View from "ol/View.js";
import TileLayer from "ol/layer/Tile.js";
import VectorLayer from "ol/layer/Vector.js";
import TileWMS from "ol/source/TileWMS.js";
import XYZ from "ol/source/XYZ.js";
import VectorSource from "ol/source/Vector.js";
import GeoJSON from "ol/format/GeoJSON.js";
import { fromLonLat } from "ol/proj.js";
import "ol/ol.css";

const proof = window.__proof;
const fail = (message) => proof.errors.push(String(message).slice(0, 300));
window.addEventListener("error", (event) => fail(event.message));
window.addEventListener("unhandledrejection", (event) => fail(event.reason?.message ?? event.reason));

const layers = "cities,routes,zones";

const wmsSource = new TileWMS({
  url: "/ogc/qgis/wms",
  params: { LAYERS: layers, VERSION: "1.3.0", TRANSPARENT: "TRUE" },
  serverType: "geoserver",
  crossOrigin: "anonymous",
});
const xyzSource = new XYZ({ url: "/api/maps/qgis/tiles/{z}/{x}/{y}.png", crossOrigin: "anonymous" });

wmsSource.on("tileloadend", () => {
  proof.wmsTiles += 1;
  maybeReady();
});
wmsSource.on("tileloaderror", () => fail("ol wms tile failed"));
xyzSource.on("tileloadend", () => {
  proof.xyzTiles += 1;
  maybeReady();
});
xyzSource.on("tileloaderror", () => fail("ol xyz tile failed"));

const vectorSource = new VectorSource();
const map = new Map({
  target: "map",
  layers: [
    new TileLayer({ source: xyzSource }),
    new TileLayer({ source: wmsSource }),
    new VectorLayer({ source: vectorSource }),
  ],
  view: new View({ center: fromLonLat([8, 52]), zoom: 5 }),
});

let vectorDone = false;
let gfiDone = false;
function maybeReady() {
  if (!proof.ready && proof.wmsTiles > 0 && proof.xyzTiles > 0 && vectorDone && gfiDone) {
    proof.ready = true;
  }
}

// WFS GetFeature GeoJSON, two pages merged, geometries reprojected by srsName
// so the view needs no client-side reprojection.
async function loadVector() {
  const format = new GeoJSON();
  const merged = [];
  for (const page of ["count=5&startIndex=0", "count=50&startIndex=5"]) {
    const params = new URLSearchParams({
      service: "WFS",
      request: "GetFeature",
      typeNames: layers,
      outputFormat: "application/geo+json",
      srsName: "EPSG:3857",
      ...(page === "count=5&startIndex=0"
        ? { count: "5", startIndex: "0" }
        : { count: "50", startIndex: "5" }),
    });
    const response = await fetch(`/ogc/qgis/wfs?${params.toString()}`);
    if (!response.ok) throw new Error(`wfs ${page} -> ${response.status}`);
    const collection = await response.json();
    merged.push(...format.readFeatures(collection, { dataProjection: "EPSG:3857", featureProjection: "EPSG:3857" }));
  }
  vectorSource.addFeatures(merged);
  proof.vectorCount = vectorSource.getFeatures().length;
  vectorDone = true;
  maybeReady();
}

// GetFeatureInfo through the client's own URL builder at Amsterdam.
async function identify() {
  const view = map.getView();
  const url = wmsSource.getFeatureInfoUrl(fromLonLat([4.9041, 52.3676]), view.getResolution(), "EPSG:3857", {
    INFO_FORMAT: "text/html",
    FEATURE_COUNT: "10",
  });
  if (!url) throw new Error("ol getFeatureInfoUrl returned nothing");
  const response = await fetch(url);
  proof.gfi = await response.text();
  gfiDone = true;
  maybeReady();
}

Promise.all([loadVector(), identify()]).catch((error) => fail(error.message));
