#!/usr/bin/env python3
"""Coverage report + gap detection for the recorded ArcGIS corpus.

Reads ``tests/fixtures/arcgis/captured/index.json`` and measures it against the
dimensions the ArcGIS REST provider (`Spatial.Stores.ArcGisRest`) and the
Esri interop codec (`Spatial.Interop.Esri`) actually branch on. The point is
not "how many endpoints" but "which real-world shapes are we *not* proving we
handle". Gaps printed at the end are the next harvest iteration's shopping
list.

Keep the supported sets below in sync with:
- ``src/Spatial.Interop.Esri/EsriFieldType.cs``  (field type map)
- ``src/Spatial.Interop.Esri/WkidMap.cs``        (curated WKID -> EPSG map)
- ``src/Spatial.Stores.ArcGisRest/ArcGisRestMapper.cs`` (geometry type names)
"""

from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parents[2]
CAPTURED = ROOT / "tests" / "fixtures" / "arcgis" / "captured"

# EsriFieldType.cs
SUPPORTED_FIELD_TYPES = {
    "esriFieldTypeOID", "esriFieldTypeSmallInteger", "esriFieldTypeInteger",
    "esriFieldTypeSingle", "esriFieldTypeDouble", "esriFieldTypeString",
    "esriFieldTypeDate", "esriFieldTypeGUID", "esriFieldTypeGlobalID",
    "esriFieldTypeGeometry",
}
# WkidMap.cs
SUPPORTED_WKIDS = {
    4326, 4258, 4269, 4277, 4171, 3857, 102100, 102113, 32610, 32612, 32632,
    32633, 25832, 25833, 26910, 27700, 2154,
}
# ArcGisRestMapper.EngineGeometryType
MAPPED_GEOMETRY_TYPES = {
    "esriGeometryPoint", "esriGeometryMultipoint", "esriGeometryPolyline", "esriGeometryPolygon",
}
TARGET_GEOMETRY_TYPES = MAPPED_GEOMETRY_TYPES | {"esriGeometryEnvelope"}
def iter_layers(index: dict[str, Any]) -> Iterable[dict[str, Any]]:
    for endpoint in index.get("endpoints", []):
        for layer in endpoint.get("layers", []):
            yield {"endpoint": endpoint, **layer}


def histogram(values: Iterable[Any]) -> collections.Counter:
    return collections.Counter(values)


def load_fixture(directory: Path, name: str | None) -> Any:
    if not name:
        return None
    path = directory / name
    if not path.exists():
        return None
    return json.loads(path.read_text("utf-8")).get("body")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", default=str(CAPTURED))
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    args = parser.parse_args()
    root = Path(args.dir)
    index = json.loads((root / "index.json").read_text("utf-8"))
    layers = list(iter_layers(index))

    geometry_types = histogram(layer.get("geometryType") for layer in layers)
    field_types = collections.Counter()
    for layer in layers:
        field_types.update(layer.get("fieldTypes") or [])
    srids = histogram(layer.get("srid") for layer in layers)
    error_codes = histogram(layer.get("errorCode") for layer in layers)
    catalogs = histogram(endpoint["catalog"] for endpoint in index["endpoints"])
    service_types = histogram(endpoint["serviceType"] for endpoint in index["endpoints"])

    # Real per-fixture facts that the manifest alone can misreport after slimming.
    empty_queries = 0
    null_geometries = 0
    paged_queries = 0
    query_srids = collections.Counter()
    non_queryable = 0
    group_layers = 0
    table_layers = 0
    for layer in layers:
        directory = root / layer["endpoint"]["directory"]
        metadata = load_fixture(directory, layer.get("metadata")) or {}
        query = load_fixture(directory, layer.get("features")) or {}
        if not metadata.get("geometryType"):
            non_queryable += 1
            # The provider skips group layers (containers, not data) and
            # keeps tables (queryable non-spatial datasets).
            if metadata.get("type") == "Group Layer":
                group_layers += 1
            elif metadata.get("type") == "Table":
                table_layers += 1
        features = query.get("features") or []
        if not features:
            empty_queries += 1
        if any(not feature.get("geometry") for feature in features):
            null_geometries += 1
        if query.get("exceededTransferLimit"):
            paged_queries += 1
        sr = query.get("spatialReference") or {}
        wkid = sr.get("latestWkid") or sr.get("wkid")
        if isinstance(wkid, int):
            query_srids[wkid] += 1

    report = {
        "endpoints": len(index["endpoints"]),
        "layers": len(layers),
        "catalogs": dict(catalogs),
        "serviceTypes": dict(service_types),
        "geometryTypes": {str(k): v for k, v in geometry_types.items()},
        "fieldTypes": dict(field_types),
        "srids": {str(k): v for k, v in srids.items()},
        "querySrids": {str(k): v for k, v in query_srids.items()},
        "errorCodes": {str(k): v for k, v in error_codes.items()},
        "emptyQueries": empty_queries,
        "nullGeometryQueries": null_geometries,
        "pagedQueries": paged_queries,
        "nonQueryableLayers": non_queryable,
        "groupLayers": group_layers,
        "tableLayers": table_layers,
    }

    gaps: list[str] = []
    for geometry_type in sorted(TARGET_GEOMETRY_TYPES):
        if not geometry_types.get(geometry_type):
            gaps.append(f"geometry type not represented: {geometry_type}")
    for field_type in sorted(SUPPORTED_FIELD_TYPES):
        if not field_types.get(field_type):
            gaps.append(f"supported field type not represented: {field_type}")
    supported_wkids_seen = {int(k) for k in report["srids"] if k != "None"} & SUPPORTED_WKIDS
    if len(supported_wkids_seen) < 3:
        gaps.append(f"only {len(supported_wkids_seen)} curated WKIDs represented: {sorted(supported_wkids_seen)}")
    unsupported_wkids = {int(k) for k in report["querySrids"] if k != "None"} - SUPPORTED_WKIDS
    if unsupported_wkids:
        gaps.append(f"query SRIDs outside the curated WKID map (provider drops CRS): {sorted(unsupported_wkids)}")
    if not report["pagedQueries"]:
        gaps.append("no response with exceededTransferLimit=true (pagination branch unproven)")
    if not report["emptyQueries"]:
        gaps.append("no empty query response (empty-result branch unproven)")
    if not report["nullGeometryQueries"]:
        gaps.append("no feature with a null geometry (null-geometry branch unproven)")
    unclassified = report["nonQueryableLayers"] - report["groupLayers"] - report["tableLayers"]
    if unclassified:
        gaps.append(f"{unclassified} layers have no geometryType and are neither group layers nor tables")

    report["gaps"] = gaps
    if args.json:
        print(json.dumps(report, indent=2, sort_keys=True))
        return 0

    print(f"endpoints: {report['endpoints']}  layers: {report['layers']}")
    print(f"service types: {report['serviceTypes']}")
    print("\nendpoints per catalog:")
    for catalog, count in sorted(catalogs.items(), key=lambda item: -item[1]):
        print(f"  {count:3}  {catalog}")
    print("\ngeometry types:", dict(geometry_types))
    print("field types:", dict(field_types))
    print("layer srids:", dict(srids))
    print("query srids:", dict(query_srids))
    print("error codes:", dict(error_codes))
    print(f"empty queries: {empty_queries}  null-geometry queries: {null_geometries}  paged queries: {paged_queries}")
    print(
        f"non-queryable (no geometryType) layers: {non_queryable} "
        f"({group_layers} group layers skipped, {table_layers} tables as non-spatial datasets)"
    )
    print("\nGAPS (next harvest targets):")
    for gap in gaps:
        print(f"  - {gap}")
    if not gaps:
        print("  (none)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
