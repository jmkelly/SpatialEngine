// Leaflet client-compat proof (T-068): genuine Leaflet client against the
// host's public services — L.tileLayer.wms pinned to 1.1.1 (SRS +
// LatLonBoundingBox path) plus XYZ map tiles and a 1.1.1 GetFeatureInfo
// (SRS, X/Y) built from live map state. Same-origin via proof-server.mjs, so
// no CORS and no canvas taint. No app/spatial logic.
import L from "leaflet";
import "leaflet/dist/leaflet.css";

const proof = window.__proof;
const fail = (message) => proof.errors.push(String(message).slice(0, 300));
window.addEventListener("error", (event) => fail(event.message));
window.addEventListener("unhandledrejection", (event) => fail(event.reason?.message ?? event.reason));

const map = L.map("map", { center: [52, 8], zoom: 5, zoomControl: false, attributionControl: false });

const wms = L.tileLayer.wms("/ogc/qgis/wms", {
  layers: "cities,routes,zones",
  version: "1.1.1",
  format: "image/png",
  transparent: true,
  crossOrigin: true,
});
const xyz = L.tileLayer("/api/maps/qgis/tiles/{z}/{x}/{y}.png", { crossOrigin: true });

let wmsDone = false;
let xyzDone = false;
let gfiDone = false;
function maybeReady() {
  if (!proof.ready && wmsDone && xyzDone && gfiDone) proof.ready = true;
}

wms.on("tileload", () => {
  proof.wmsTiles += 1;
});
wms.on("load", () => {
  wmsDone = true;
  maybeReady();
});
wms.on("tileerror", () => fail("leaflet wms tile failed"));
xyz.on("tileload", () => {
  proof.xyzTiles += 1;
});
xyz.on("load", () => {
  xyzDone = true;
  maybeReady();
});
xyz.on("tileerror", () => fail("leaflet xyz tile failed"));

// GetFeatureInfo 1.1.1: SRS + X/Y from the live map viewport, aimed at
// Amsterdam. The BBOX is projected through the map CRS exactly the way
// Leaflet builds tile BBOXes, so pixels line up with the rendered tiles.
async function identify() {
  const size = map.getSize();
  const crs = map.options.crs;
  const projected = (lat, lng) => crs.project(L.latLng(lat, lng));
  const northWest = projected(map.getBounds().getNorth(), map.getBounds().getWest());
  const southEast = projected(map.getBounds().getSouth(), map.getBounds().getEast());
  const bbox = [northWest.x, southEast.y, southEast.x, northWest.y].join(",");
  const point = map.latLngToContainerPoint([52.3676, 4.9041]);
  const params = new URLSearchParams({
    SERVICE: "WMS",
    VERSION: "1.1.1",
    REQUEST: "GetFeatureInfo",
    LAYERS: "cities",
    QUERY_LAYERS: "cities",
    STYLES: "",
    FORMAT: "image/png",
    INFO_FORMAT: "text/html",
    FEATURE_COUNT: "10",
    SRS: crs.code,
    BBOX: bbox,
    WIDTH: String(size.x),
    HEIGHT: String(size.y),
    X: String(Math.round(point.x)),
    Y: String(Math.round(point.y)),
  });
  const url = `/ogc/qgis/wms?${params.toString()}`;
  proof.gfiUrl = url;
  const response = await fetch(url);
  proof.gfi = await response.text();
  gfiDone = true;
  maybeReady();
}

wms.addTo(map);
xyz.addTo(map);
map.whenReady(() => {
  identify().catch((error) => fail(error.message));
});
