# Census states fixture

`census-states.geojson` is a two-polygon, Census-2000-shaped stand-in for the
Esri `Census` MapServer `states` layer the workbench Parity page compares
against (`ParityDefaults.bbox` `-125,25,-66,50`, WKID 4326).

Both polygons sit inside that bbox: a California-like box
(-124..-114, 32..42) and a Texas-like box (-106..-93, 25..36), carrying the
Census attribute shape (`STATEFP`, `NAME`, `POP2000` with the real Census
2000 populations). It is deliberately tiny and synthetic — simplified boxes,
not TIGER geometry — so the parity integration test
(`ParityCensusTests`) stays offline and deterministic while proving the
localhost MapServer/export and FeatureServer/query URLs from the Parity page
return real data over the same bbox the Esri panels use.
