// The seed manifest: real, publicly available datasets and the styled
// services built from them. Kept as data (not code) so a new source or
// service is a manifest edit, and so `seed.mjs --list` can describe the
// whole seed without touching the network.
//
// Sources are ingested through the neutral admin API (ADR-0041). `sourceSrid`
// exercises the engine's server-side reprojection: the file is decoded in
// that CRS and transformed to `srid` by the ProjNet service before it is
// stored. `identity: "auto"` makes the dataset editable/lookup-able.
//
// Services are maps (ADR-0053). A map exposing `map` is served as a
// MapServer (ADR-0048); one exposing `feature` is a queryable/editable
// FeatureServer. Each layer's `style` is a compact draw recipe
// ({ color, opacity, lineWidth, radius, visible }) and `geometry` is its
// family; `seed.mjs` lowers them to the persisted MapLibre style fragment
// (ADR-0047) that the host stores and the MapServer projects to drawingInfo.

const naturalEarth = (file) =>
  `https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/${file}`;

/** The datasets the seed loads, in load order. */
export const sources = [
  {
    id: "public.world_countries",
    url: naturalEarth("ne_110m_admin_0_countries.geojson"),
    format: "geojson",
    srid: 4326,
    identity: "auto",
    note: "Natural Earth 1:110m country polygons",
  },
  {
    id: "public.world_places",
    url: naturalEarth("ne_110m_populated_places.geojson"),
    format: "geojson",
    srid: 4326,
    identity: "none",
    note: "Natural Earth 1:110m populated places",
  },
  {
    id: "public.world_rivers",
    url: naturalEarth("ne_110m_rivers_lake_centerlines.geojson"),
    format: "geojson",
    srid: 4326,
    identity: "none",
    note: "Natural Earth 1:110m river centrelines",
  },
  {
    id: "public.world_lakes",
    url: naturalEarth("ne_110m_lakes.geojson"),
    format: "geojson",
    srid: 4326,
    identity: "none",
    note: "Natural Earth 1:110m lakes",
  },
  {
    id: "public.us_states",
    url: naturalEarth("ne_50m_admin_1_states_provinces.geojson"),
    format: "geojson",
    srid: 4326,
    identity: "none",
    note: "Natural Earth 1:50m admin-1 states and provinces",
  },
  {
    id: "public.earthquakes",
    url: "https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/2.5_week.geojson",
    format: "geojson",
    srid: 4326,
    identity: "none",
    note: "USGS magnitude 2.5+ earthquakes, past 7 days",
  },
  {
    id: "public.world_places_mercator",
    url: naturalEarth("ne_110m_populated_places.geojson"),
    format: "geojson",
    srid: 3857,
    sourceSrid: 4326,
    identity: "none",
    note: "Populated places reprojected 4326 -> 3857 on ingest",
  },
];

const palette = {
  country: { color: "#3d7ea6", opacity: 0.35, lineWidth: 1 },
  state: { color: "#8d6e63", opacity: 0.25, lineWidth: 1 },
  lake: { color: "#4fc3f7", opacity: 0.7, lineWidth: 1 },
  river: { color: "#1e88e5", opacity: 0.9, lineWidth: 1.5 },
  place: { color: "#ffd54f", opacity: 0.9, radius: 3 },
  quake: { color: "#ff5252", opacity: 0.85, radius: 5 },
  mercator: { color: "#7e57c2", opacity: 0.3, lineWidth: 1 },
};

/** The services the seed publishes, in create order. */
export const services = [
  {
    name: "WorldCountries",
    services: ["feature"],
    description: "Every country as an editable, queryable feature layer.",
    copyright: "Natural Earth",
    layers: [{ dataset: "public.world_countries", name: "Countries", geometry: "polygon", style: palette.country }],
  },
  {
    name: "WorldPlaces",
    services: ["feature"],
    description: "Populated places, queryable by name and population.",
    copyright: "Natural Earth",
    layers: [{ dataset: "public.world_places", name: "Places", geometry: "point", style: palette.place }],
  },
  {
    name: "UnitedStates",
    services: ["feature"],
    description: "US states as features.",
    copyright: "Natural Earth",
    layers: [{ dataset: "public.us_states", name: "States", geometry: "polygon", style: palette.state }],
  },
  {
    name: "SeismicActivity",
    services: ["feature"],
    description: "Recent magnitude 2.5+ earthquakes.",
    copyright: "USGS Earthquake Hazards Program",
    layers: [{ dataset: "public.earthquakes", name: "Earthquakes", geometry: "point", style: palette.quake }],
  },
  {
    name: "WorldPlacesMercator",
    services: ["feature"],
    description: "Populated places stored in EPSG:3857 (reprojected on ingest).",
    copyright: "Natural Earth",
    layers: [{ dataset: "public.world_places_mercator", name: "Places (Mercator)", geometry: "point", style: palette.mercator }],
  },
  {
    name: "WorldReference",
    services: ["map"],
    description: "A styled reference map: countries, lakes, rivers and places.",
    copyright: "Natural Earth",
    layers: [
      { dataset: "public.world_countries", name: "Countries", geometry: "polygon", style: palette.country },
      { dataset: "public.world_lakes", name: "Lakes", geometry: "polygon", style: palette.lake },
      { dataset: "public.world_rivers", name: "Rivers", geometry: "line", style: palette.river },
      { dataset: "public.world_places", name: "Places", geometry: "point", style: palette.place },
    ],
  },
  {
    name: "WorldAtlas",
    services: ["map"],
    description: "A second styled map with a different palette for the same data.",
    copyright: "Natural Earth",
    layers: [
      { dataset: "public.world_countries", name: "Countries", geometry: "polygon", style: { color: "#2e7d32", opacity: 0.4, lineWidth: 1 } },
      { dataset: "public.us_states", name: "States", geometry: "polygon", style: palette.state },
    ],
  },
  {
    name: "SeismicMap",
    services: ["map"],
    description: "A styled map of recent earthquakes.",
    copyright: "USGS Earthquake Hazards Program",
    layers: [{ dataset: "public.earthquakes", name: "Earthquakes", geometry: "point", style: palette.quake }],
  },
];
