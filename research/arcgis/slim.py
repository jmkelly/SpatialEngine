#!/usr/bin/env python3
"""Slim the recorded ArcGIS fixtures to a committable size without losing the
shapes the compliance tests assert on.

Raw process output can be tens of megabytes (contour layers with thousands of
vertices, national geographies with 5 MB of rings). The provider only cares
about field definitions, feature identities, attribute types/values and the
geometry category, so this pass:

- keeps query responses to at most a few features,
- caps each coordinate list (rings/paths/points) and each string attribute,
- keeps layer metadata to the properties the provider reads (dropping
  drawingInfo/templates/domains, which the provider never looks at),
- caps ``objectIds`` in ids-only responses.

Idempotent: slimming a slim fixture is a no-op.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
CAPTURED = ROOT / "tests" / "fixtures" / "arcgis" / "captured"

MAX_FEATURES = 3
MAX_PARTS = 3
MAX_COORDS = 40
MAX_STRING = 256
MAX_OBJECT_IDS = 25
MAX_METADATA_FIELDS = 60
MAX_FIELDS_ECHO = 60

SERVICE_KEYS = {
    "currentVersion", "serviceDescription", "serviceItemId", "mapName", "description",
    "copyrightText", "capabilities", "supportedQueryFormats", "maxRecordCount",
    "hasVersionedData", "hasArchivedData", "type", "layers", "tables", "spatialReference",
    "initialExtent", "fullExtent", "units", "allowGeometryUpdates", "geometryType",
}
LAYER_KEYS = {
    "id", "name", "type", "geometryType", "objectIdField", "globalIdField", "displayField",
    "description", "copyrightText", "capabilities", "supportedQueryFormats", "maxRecordCount",
    "hasZ", "hasM", "hasAttachments", "hasM", "isDataVersioned", "supportsStatistics",
    "supportsAdvancedQueries", "spatialReference", "extent", "fields", "relationships",
}
FIELD_KEYS = {"name", "type", "alias", "nullable", "length", "precision", "scale", "editable", "defaultValue"}


def trim_attributes(attributes: Any) -> Any:
    if not isinstance(attributes, dict):
        return attributes
    return {key: trim_value(value) for key, value in attributes.items()}


def trim_value(value: Any, depth: int = 0) -> Any:
    if isinstance(value, str):
        return value if len(value) <= MAX_STRING else value[:MAX_STRING] + "…"
    if isinstance(value, list):
        return [trim_value(item, depth + 1) for item in value[:MAX_COORDS]]
    if isinstance(value, dict):
        if depth > 4:
            return None
        return {key: trim_value(item, depth + 1) for key, item in list(value.items())[:MAX_FIELDS_ECHO]}
    return value


def trim_coordinates(geometry: Any) -> Any:
    if not isinstance(geometry, dict):
        return geometry
    result = dict(geometry)
    for key in ("points",):
        if isinstance(result.get(key), list):
            result[key] = result[key][:MAX_COORDS]
    for key in ("paths", "rings"):
        if isinstance(result.get(key), list):
            result[key] = [part[:MAX_COORDS] for part in result[key][:MAX_PARTS] if isinstance(part, list)]
    return result


def slim_fields(fields: Any, limit: int) -> list[dict[str, Any]]:
    if not isinstance(fields, list):
        return []
    kept: list[dict[str, Any]] = []
    for field in fields[:limit]:
        if isinstance(field, dict):
            kept.append({key: field[key] for key in FIELD_KEYS if key in field})
    return kept


def slim_layer(body: dict[str, Any]) -> dict[str, Any]:
    result = {key: body[key] for key in LAYER_KEYS if key in body}
    result["fields"] = slim_fields(body.get("fields"), MAX_METADATA_FIELDS)
    return result


def slim_query(body: dict[str, Any]) -> dict[str, Any]:
    result = {key: body[key] for key in body if key != "features"}
    if "fields" in result:
        result["fields"] = slim_fields(result["fields"], MAX_FIELDS_ECHO)
    features: list[dict[str, Any]] = []
    for feature in (body.get("features") or [])[:MAX_FEATURES]:
        if not isinstance(feature, dict):
            continue
        trimmed = dict(feature)
        if "attributes" in trimmed:
            trimmed["attributes"] = trim_attributes(trimmed["attributes"])
        if "geometry" in trimmed:
            trimmed["geometry"] = trim_coordinates(trimmed["geometry"])
        features.append(trimmed)
    result["features"] = features
    return result


def slim_body(kind: str, body: Any) -> Any:
    if not isinstance(body, dict):
        return body
    if kind == "service":
        return {key: body[key] for key in SERVICE_KEYS if key in body}
    if kind == "layer":
        return slim_layer(body)
    if kind == "query":
        return slim_query(body)
    if kind == "count":
        return {key: body[key] for key in ("count",) if key in body}
    if kind == "ids":
        result = {key: body[key] for key in body if key != "objectIds"}
        ids = body.get("objectIds")
        result["objectIds"] = ids[:MAX_OBJECT_IDS] if isinstance(ids, list) else ids
        return result
    return body


def kind_of(filename: str) -> str:
    for prefix in ("service", "layer", "query", "count", "ids", "error"):
        if filename == f"{prefix}.json" or filename.startswith(f"{prefix}-"):
            return prefix
    return "other"


def process(path: Path) -> bool:
    envelope = json.loads(path.read_text("utf-8"))
    kind = kind_of(path.name)
    new_body = slim_body(kind, envelope.get("body"))
    changed = new_body != envelope.get("body")
    envelope["body"] = new_body
    text = json.dumps(envelope, indent=2, sort_keys=True) + "\n"
    if changed or len(text) < path.stat().st_size:
        path.write_text(text, "utf-8")
        return True
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", default=str(CAPTURED))
    args = parser.parse_args()
    root = Path(args.dir)
    before = after = count = 0
    for path in sorted(root.rglob("*.json")):
        if path.name == "index.json":
            continue
        before += path.stat().st_size
        if process(path):
            count += 1
        after += path.stat().st_size
    print(f"slimmed {count} files; {before/1e6:.1f} MB -> {after/1e6:.1f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
