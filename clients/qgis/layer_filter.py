"""Pure layer-support filter (no QGIS imports so pytest runs anywhere).

The plugin dialog lists only these; everything else is hidden with a tooltip
reason (Q15). The host would reject the rest at ``DescribeAsync`` time anyway.
"""

from __future__ import annotations

SUPPORTED_GEOMETRIES = frozenset({"point", "linestring", "multipoint", "multilinestring", "polygon", "multipolygon", "geometry"})

_HIDDEN_PROVIDERS = frozenset({"wms", "wfs", "wcs", "xyz", "vectortile", "mesh", "plugin"})


def support_reason(info: dict) -> tuple[bool, str]:
    """Decide whether a layer descriptor is listable. Returns (ok, reason)."""
    provider = str(info.get("provider", "")).lower()
    if provider in _HIDDEN_PROVIDERS:
        return False, f"provider '{provider}' is not uploadable"
    if info.get("isRaster"):
        return False, "raster layers are out of v1 scope"
    if info.get("isMesh"):
        return False, "mesh layers are not supported"
    if info.get("isVirtual") or info.get("isVirtualLayer"):
        return False, "virtual layers are not supported"
    if info.get("hasJoin") or info.get("isJoined"):
        return False, "joined layers must be materialised first"
    geom = str(info.get("geometry", "")).lower()
    if geom and geom not in SUPPORTED_GEOMETRIES and geom != "unknown":
        return False, f"geometry '{geom}' is not supported"
    if not info.get("hasSpatial", True):
        return False, "non-spatial tables cannot be served"
    if info.get("crsValid") is False:
        return False, "layer CRS is unknown"
    return True, ""


def filter_layers(infos: list[dict]) -> tuple[list[dict], list[tuple[dict, str]]]:
    """Split descriptors into (visible, [(hidden, reason)])."""
    visible: list[dict] = []
    hidden: list[tuple[dict, str]] = []
    for info in infos:
        ok, reason = support_reason(info)
        if ok:
            visible.append(info)
        else:
            hidden.append((info, reason))
    return visible, hidden
