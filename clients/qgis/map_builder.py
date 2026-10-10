"""Pure map-document helpers (no QGIS imports so pytest runs anywhere)."""

from __future__ import annotations

import copy

VECTOR_SERVICES = ["feature", "map", "tiles", "wms", "wfs"]


def next_layer_id(layers: list[dict]) -> int:
    """Return the next free integer layer id for an append."""
    ids = [l.get("layerId") for l in layers if isinstance(l.get("layerId"), int)]
    return (max(ids) + 1) if ids else 0


def build_map(name: str, store: str, services: list[str] | None = None) -> dict:
    """Create a new map document with the v1 vector-service default."""
    return {
        "name": name,
        "store": store,
        "layers": [],
        "services": list(services) if services is not None else list(VECTOR_SERVICES),
    }


def append_layer(existing: dict | None, name: str, dataset: str, style: str | None,
                 store: str, layer_name: str | None = None, kind: str = "feature") -> dict:
    """Append a layer to a copy of *existing* (or a new map), preserving the rest.

    Uses the ``layerId: -1`` sentinel so the host assigns the next free id,
    matching ``WithMapAsync`` / composer behaviour. Never mutates the input.
    Skips the append when the dataset is already present (style preserved).
    Unions the vector services when creating or extending.
    """
    if existing is None:
        base = build_map(name, store)
    else:
        base = copy.deepcopy(existing)
        base["name"] = name
    layers = base.get("layers", [])
    for layer in layers:
        effective = layer.get("store") or base.get("store")
        if layer.get("dataset") == dataset and effective == store:
            return base
    layers.append({
        "dataset": dataset,
        "layerId": -1,
        "name": layer_name,
        "style": style,
        "kind": kind,
        "store": store if store != base.get("store") else None,
    })
    base["layers"] = layers
    services = base.get("services", [])
    for service in VECTOR_SERVICES:
        if service not in services:
            services.append(service)
    base["services"] = services
    return base
