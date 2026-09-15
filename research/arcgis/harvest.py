#!/usr/bin/env python3
"""Autoresearch harvester for public ESRI ArcGIS REST (GeoServices) endpoints.

The SpatialEngine ArcGIS REST provider (`Spatial.Stores.ArcGisRest`) and the
GeoServices facade (`Spatial.Adapter.GeoServices`) are both compliance claims:
they claim to speak the dialect real ArcGIS servers speak. This script builds
the ground-truth corpus for those claims by crawling public, unauthenticated
catalog roots, discovering FeatureServer/MapServer services, and recording the
exact JSON they return for service-root, layer-metadata, query, count and
error requests.

Recorded responses land in `tests/fixtures/arcgis/captured/` with an
`index.json` manifest. `report.py` measures the corpus against the dimensions
the provider cares about (geometry types, field types, spatial references,
paging, errors) and prints the gaps that the next loop iteration should close.

Design notes:
- Idempotent: responses are cached on disk under `research/arcgis/cache/`, so
  re-runs are cheap and re-record without re-hitting the network.
- Polite: small concurrency, a fixed delay, bounded retries, capped fan-out.
- Deterministic output: sorted manifest, stable slugs, `capturedAt` from the
  cache file mtime so a cached run does not churn the fixtures.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlencode, urlsplit, urlunsplit

import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(__file__).resolve().parent / "cache"
FIXTURES = ROOT / "tests" / "fixtures" / "arcgis"
CAPTURED = FIXTURES / "captured"
SEEDS = Path(__file__).resolve().parent / "seeds.txt"

USER_AGENT = "SpatialEngine-ArcGisRestResearch/1.0 (+compliance fixtures)"
REQUEST_TIMEOUT = 30
RETRIES = 2
DELAY_SECONDS = 0.15
WORKERS = 5

# Query probe: small, bounded, geometry + attributes, JSON.
PROBE_QUERY = {
    "where": "1=1",
    "outFields": "*",
    "returnGeometry": "true",
    "resultRecordCount": "5",
    "f": "json",
}

# Keep fixtures small; drop very wide attribute payloads but keep the shape.
MAX_FIELDS_PER_LAYER = 60
MAX_FEATURES_PER_QUERY = 5


@dataclass
class Service:
    url: str
    name: str
    service_type: str
    catalog: str


@dataclass
class LayerReport:
    layer_id: int
    name: str
    geometry_type: str | None
    srid: int | None
    field_types: list[str] = field(default_factory=list)
    max_record_count: int | None = None
    has_metadata: bool = False
    query_feature_count: int = 0
    query_has_geometry: bool = False
    query_exceeded: bool = False
    count_only: int | None = None
    ids_only: int = 0
    error_code: int | None = None


@dataclass
class ServiceReport:
    slug: str
    url: str
    catalog: str
    service_type: str
    current_version: float | None = None
    layers: list[LayerReport] = field(default_factory=list)
    error: str | None = None
    # Stable relative filename -> full HTTP envelope ({status, contentType, body}).
    fixtures: dict[str, dict[str, Any]] = field(default_factory=dict)


def cache_path(url: str, params: dict[str, str] | None) -> Path:
    key = url + ("?" + urlencode(sorted((params or {}).items())) if params else "")
    digest = hashlib.sha256(key.encode("utf-8")).hexdigest()[:32]
    return CACHE / f"{digest}.json"


def fetch(url: str, params: dict[str, str] | None = None, *, force: bool = False) -> dict[str, Any] | None:
    """Fetch a URL (cached) and return the parsed JSON envelope."""
    params = dict(params or {})
    params.setdefault("f", "json")
    path = cache_path(url, params)
    if path.exists() and not force:
        try:
            return json.loads(path.read_text("utf-8"))
        except (OSError, json.JSONDecodeError):
            pass

    query = urlencode(params)
    full = f"{url}?{query}" if query else url
    full = normalize_url(full)
    last_error: str | None = None
    for attempt in range(RETRIES + 1):
        request = urllib.request.Request(full, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT) as response:
                body = response.read()
                status = response.status
                content_type = response.headers.get("Content-Type", "")
            envelope = _envelope(status, content_type, body)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(envelope, sort_keys=True), "utf-8")
            time.sleep(DELAY_SECONDS)
            return envelope
        except urllib.error.HTTPError as exc:
            body = exc.read()
            envelope = _envelope(exc.code, exc.headers.get("Content-Type", ""), body)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(envelope, sort_keys=True), "utf-8")
            return envelope
        except (urllib.error.URLError, TimeoutError, OSError) as exc:  # transient
            last_error = str(exc)
            time.sleep(0.5 * (attempt + 1))
    sys.stderr.write(f"! fetch failed: {full}: {last_error}\n")
    return None


def normalize_url(url: str) -> str:
    """Percent-encode path segments so ArcGIS service names with spaces or
    non-ASCII characters are requested legally."""
    parts = urlsplit(url)
    path = urllib.parse.quote(parts.path, safe="/%")
    return urlunsplit((parts.scheme, parts.netloc, path, parts.query, parts.fragment))


def _envelope(status: int, content_type: str, body: bytes) -> dict[str, Any]:
    try:
        parsed = json.loads(body.decode("utf-8", "replace"))
    except json.JSONDecodeError:
        parsed = None
    return {
        "status": status,
        "contentType": content_type,
        "body": parsed,
        "rawLength": len(body),
    }


def crawl_catalog(root: str, max_services: int, max_depth: int) -> list[Service]:
    """Breadth-first crawl of a catalog root, returning Feature/Map servers.

    ArcGIS service names in a folder listing are relative to the *catalog
    root* (e.g. ``Monumentation/Monumentation``), and ArcGIS Online listings
    also carry an explicit absolute ``url``. Prefer the explicit URL and
    otherwise resolve the name against the root.
    """
    root = root.rstrip("/")
    services: list[Service] = []
    seen_folders: set[str] = set()
    seen_urls: set[str] = set()
    frontier = [(root, 0)]
    while frontier and len(services) < max_services:
        base, depth = frontier.pop(0)
        if base in seen_folders:
            continue
        seen_folders.add(base)
        envelope = fetch(base)
        body = envelope.get("body") if envelope else None
        if not isinstance(body, dict):
            continue
        for service in body.get("services", []) or []:
            if not isinstance(service, dict):
                continue
            service_type = service.get("type", "")
            name = service.get("name", "")
            if service_type not in {"FeatureServer", "MapServer"} or not name:
                continue
            explicit = service.get("url")
            url = explicit if isinstance(explicit, str) and explicit else f"{root}/{name}/{service_type}"
            if url not in seen_urls:
                seen_urls.add(url)
                services.append(Service(url=url, name=name, service_type=service_type, catalog=root))
        if depth < max_depth:
            for folder in body.get("folders", []) or []:
                if isinstance(folder, str) and folder:
                    frontier.append((f"{base}/{folder}", depth + 1))
    return services[:max_services]


def slugify(value: str) -> str:
    value = re.sub(r"[^A-Za-z0-9]+", "-", value).strip("-").lower()
    return value[:80] or "service"


def probe_service(service: Service) -> ServiceReport:
    report = ServiceReport(
        slug=slugify(urlsplit(service.url).path.replace("/arcgis/rest/services", "").strip("/")),
        url=service.url,
        catalog=service.catalog,
        service_type=service.service_type,
    )
    envelope = fetch(service.url)
    body = envelope.get("body") if envelope else None
    if not isinstance(body, dict):
        report.error = "no service root JSON"
        return report
    if "error" in body:
        report.error = json.dumps(body["error"])[:200]
        return report

    report.current_version = body.get("currentVersion")
    report.fixtures["service.json"] = envelope
    layers = [
        layer
        for layer in (body.get("layers", []) or []) + (body.get("tables", []) or [])
        if isinstance(layer, dict) and isinstance(layer.get("id"), int)
    ]
    for layer in layers:
        layer_id = layer["id"]
        layer_name = str(layer.get("name", f"l{layer_id}"))
        layer_report = LayerReport(layer_id=layer_id, name=layer_name, geometry_type=None, srid=None)
        metadata_env = fetch(f"{service.url}/{layer_id}")
        metadata = metadata_env.get("body") if metadata_env else None
        if metadata_env:
            report.fixtures[f"layer-{layer_id}.json"] = metadata_env
        if isinstance(metadata, dict) and "error" not in metadata:
            layer_report.has_metadata = True
            layer_report.geometry_type = metadata.get("geometryType")
            sr = metadata.get("spatialReference") or {}
            wkid = sr.get("latestWkid") or sr.get("wkid")
            layer_report.srid = wkid if isinstance(wkid, int) else None
            layer_report.max_record_count = metadata.get("maxRecordCount")
            for field in (metadata.get("fields") or [])[:MAX_FIELDS_PER_LAYER]:
                if isinstance(field, dict) and field.get("type"):
                    layer_report.field_types.append(str(field["type"]))

        query_env = fetch(f"{service.url}/{layer_id}/query", PROBE_QUERY)
        if query_env:
            report.fixtures[f"query-{layer_id}.json"] = query_env
        query = query_env.get("body") if query_env else None
        if isinstance(query, dict) and "error" not in query:
            features = query.get("features") or []
            layer_report.query_feature_count = len(features)
            layer_report.query_exceeded = bool(query.get("exceededTransferLimit"))
            layer_report.query_has_geometry = any(
                isinstance(feature, dict) and feature.get("geometry") for feature in features
            )

        count_env = fetch(
            f"{service.url}/{layer_id}/query",
            {"where": "1=1", "returnCountOnly": "true", "f": "json"},
        )
        if count_env:
            report.fixtures[f"count-{layer_id}.json"] = count_env
        count = count_env.get("body") if count_env else None
        if isinstance(count, dict) and isinstance(count.get("count"), int):
            layer_report.count_only = count["count"]

        ids_env = fetch(
            f"{service.url}/{layer_id}/query",
            {"where": "1=1", "returnIdsOnly": "true", "f": "json"},
        )
        if ids_env:
            report.fixtures[f"ids-{layer_id}.json"] = ids_env
        ids = ids_env.get("body") if ids_env else None
        if isinstance(ids, dict) and isinstance(ids.get("objectIds"), list):
            layer_report.ids_only = len(ids["objectIds"])

        # Deliberate error probe: unknown field in a where clause.
        error_env = fetch(
            f"{service.url}/{layer_id}/query",
            {"where": "__spatialengine_no_such_field__ = 1", "f": "json"},
        )
        if error_env:
            report.fixtures[f"error-{layer_id}.json"] = error_env
        error = error_env.get("body") if error_env else None
        if isinstance(error, dict) and isinstance(error.get("error"), dict):
            code = error["error"].get("code")
            layer_report.error_code = code if isinstance(code, int) else None

        report.layers.append(layer_report)
    return report


def write_service_fixtures(report: ServiceReport) -> None:
    if report.error or not report.layers:
        return
    service_dir = CAPTURED / report.slug
    service_dir.mkdir(parents=True, exist_ok=True)
    for filename, envelope in report.fixtures.items():
        (service_dir / filename).write_text(json.dumps(envelope, indent=2, sort_keys=True) + "\n", "utf-8")
    entry = index_entry(report)
    if entry:
        (service_dir / "manifest.json").write_text(json.dumps(entry, indent=2, sort_keys=True) + "\n", "utf-8")


def index_entry(report: ServiceReport) -> dict[str, Any] | None:
    if report.error:
        return None
    entry: dict[str, Any] = {
        "slug": report.slug,
        "url": report.url,
        "catalog": report.catalog,
        "serviceType": report.service_type,
        "currentVersion": report.current_version,
        "directory": report.slug,
        "layers": [],
    }
    for layer in report.layers:
        if not layer.has_metadata:
            continue
        entry["layers"].append(
            {
                "id": layer.layer_id,
                "name": layer.name,
                "metadata": f"layer-{layer.layer_id}.json",
                "features": f"query-{layer.layer_id}.json",
                "count": f"count-{layer.layer_id}.json",
                "ids": f"ids-{layer.layer_id}.json",
                "error": f"error-{layer.layer_id}.json",
                "geometryType": layer.geometry_type,
                "srid": layer.srid,
                "maxRecordCount": layer.max_record_count,
                "fieldTypes": sorted(set(layer.field_types)),
                "queryFeatureCount": layer.query_feature_count,
                "queryHasGeometry": layer.query_has_geometry,
                "queryExceeded": layer.query_exceeded,
                "countOnly": layer.count_only,
                "idsOnly": layer.ids_only,
                "errorCode": layer.error_code,
            }
        )
    return entry if entry["layers"] else None


def write_index(reports: list[ServiceReport] | None = None) -> None:
    """Rebuild index.json from every per-service manifest on disk.

    Scanning (rather than using only this run's reports) keeps partial and
    explicit-service runs additive instead of destructive.
    """
    entries_by_url: dict[str, dict[str, Any]] = {}
    for manifest in CAPTURED.glob("*/manifest.json"):
        try:
            entry = json.loads(manifest.read_text("utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if entry.get("url"):
            entries_by_url[entry["url"]] = entry
    for report in reports or []:
        entry = index_entry(report)
        if entry:
            entries_by_url[entry["url"]] = entry
    index = [entries_by_url[url] for url in sorted(entries_by_url)]
    (CAPTURED / "index.json").write_text(
        json.dumps({"generatedAt": now_iso(), "endpoints": index}, indent=2) + "\n", "utf-8"
    )


def now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def load_seeds() -> list[str]:
    lines = []
    for raw in SEEDS.read_text("utf-8").splitlines():
        line = raw.strip()
        if line and not line.startswith("#"):
            lines.append(line)
    return lines


def load_services_file(path: Path) -> list[Service]:
    services: list[Service] = []
    for raw in path.read_text("utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        name = urlsplit(line).path.rstrip("/").split("/")[-2] if "/" in line else line
        service_type = "MapServer" if line.rstrip("/").endswith("MapServer") else "FeatureServer"
        services.append(Service(url=line.rstrip("/"), name=name, service_type=service_type, catalog="explicit"))
    return services


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--max-services", type=int, default=40, help="services to probe across all catalogs")
    parser.add_argument("--max-per-catalog", type=int, default=10)
    parser.add_argument("--depth", type=int, default=2)
    parser.add_argument("--force", action="store_true", help="ignore the on-disk cache")
    parser.add_argument("--only", help="comma-separated seed substrings to restrict to")
    parser.add_argument("--services-file", help="probe explicit service URLs instead of crawling seeds")
    args = parser.parse_args()

    if args.services_file:
        candidates = load_services_file(Path(args.services_file))
        print(f"explicit services: {len(candidates)}")
        selected = candidates[: args.max_services]
        run_probes(selected)
        return 0

    seeds = load_seeds()
    if args.only:
        needles = [part.strip() for part in args.only.split(",") if part.strip()]
        seeds = [seed for seed in seeds if any(needle in seed for needle in needles)]

    candidates: list[Service] = []
    for seed in seeds:
        found = crawl_catalog(seed, args.max_per_catalog, args.depth)
        print(f"catalog {seed}: {len(found)} services")
        candidates.extend(found)

    # Prefer FeatureServer (editable/queryable) then MapServer, and de-dup URLs.
    seen: set[str] = set()
    ordered: list[Service] = []
    for service in sorted(candidates, key=lambda s: (s.service_type != "FeatureServer", s.url)):
        if service.url not in seen:
            seen.add(service.url)
            ordered.append(service)

    # Round-robin across catalogs so one huge portal cannot crowd out the rest.
    by_catalog: dict[str, list[Service]] = {}
    for service in ordered:
        by_catalog.setdefault(service.catalog, []).append(service)
    interleaved: list[Service] = []
    while any(by_catalog.values()):
        for catalog in list(by_catalog):
            bucket = by_catalog[catalog]
            if bucket:
                interleaved.append(bucket.pop(0))
    selected = interleaved[: args.max_services]
    run_probes(selected)
    return 0


def run_probes(selected: list[Service]) -> None:
    reports: list[ServiceReport] = []
    CAPTURED.mkdir(parents=True, exist_ok=True)
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        futures = {pool.submit(probe_service, service): service for service in selected}
        for future in as_completed(futures):
            service = futures[future]
            try:
                report = future.result()
            except Exception as exc:  # noqa: BLE001 - research harness
                report = ServiceReport(slug=slugify(service.name), url=service.url, catalog=service.catalog,
                                       service_type=service.service_type, error=str(exc))
            reports.append(report)
            write_service_fixtures(report)
            write_index(reports)
            print(f"  probed {service.url} -> layers={len(report.layers)} error={report.error}", flush=True)

    write_index(reports)
    print(f"wrote {CAPTURED / 'index.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
