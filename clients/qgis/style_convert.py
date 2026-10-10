"""Minimal QML subset to MapLibre-fragment converter (stdlib only).

Covers single-symbol + categorized/graduated renderers for vector geometry and
falls back to ``None`` (engine default ``#4fc3f7`` fill+line+circle from
``MapStyle.cs``) for rule-based/heatmap/3D/label-heavy symbology with a
warning. Fragments carry ``type``/``layout``/``paint`` (optionally
``filter``/``minzoom``/``maxzoom``) and never ``id``/``source-layer`` — the
host injects those at render (``IMapRegistry.cs``).
"""

from __future__ import annotations

import json
import xml.etree.ElementTree as ET

DEFAULT_FALLBACK_WARNING = "unsupported renderer; falling back to engine default style"

_UNSUPPORTED_RENDERERS = frozenset({
    "rulebased", "rule", "heatmap", "heatmaprenderer", "pointcluster",
    "pointdisplacement", "invertedpolygon", "mesh", "raster", "25d", "3d",
})


def _qml_color(value: str | None, default: str = "#4fc3f7") -> str:
    if not value:
        return default
    parts = [p.strip() for p in value.split(",")]
    if len(parts) < 3:
        return default
    try:
        r, g, b = (int(parts[0]), int(parts[1]), int(parts[2]))
    except ValueError:
        return default
    return f"#{r:02x}{g:02x}{b:02x}"


def _opacity(value: str | None) -> float:
    if value is None:
        return 1.0
    try:
        v = float(value)
    except ValueError:
        return 1.0
    if v > 1.0:
        v = v / 255.0
    return max(0.0, min(1.0, v))


def _fragments_for_color(geometry: str, color: str, opacity: float,
                         line_width: float = 1.0, radius: float = 5.0) -> list[dict]:
    geom = geometry.lower()
    if "point" in geom:
        return [{"type": "circle",
                 "paint": {"circle-color": color, "circle-opacity": opacity, "circle-radius": radius}}]
    if "line" in geom:
        return [{"type": "line",
                 "paint": {"line-color": color, "line-opacity": opacity, "line-width": line_width}}]
    return [{"type": "fill", "paint": {"fill-color": color, "fill-opacity": opacity}},
            {"type": "line", "paint": {"line-color": color, "line-opacity": opacity, "line-width": line_width}}]


def qml_to_fragments(qml: str, geometry: str = "polygon") -> tuple[str | None, str]:
    """Convert a QML string to a persisted style JSON array string.

    Returns ``(style_or_none, warning)`` where ``warning`` is ``""`` on a clean
    conversion. Raises ``ValueError`` (``invalid.arguments``) on unparseable XML.
    """
    try:
        root = ET.fromstring(qml)
    except ET.ParseError as exc:
        raise ValueError(f"invalid.arguments: unparseable QML: {exc}") from exc
    renderer = root.find("renderer-v2")
    rtype = (renderer.get("type") if renderer is not None else "") or ""
    if rtype.lower() in _UNSUPPORTED_RENDERERS:
        return None, DEFAULT_FALLBACK_WARNING
    if renderer is None or rtype.lower() in ("singlesymbol", "single", ""):
        symbol = renderer.find("symbol") if renderer is not None else None
        layer = symbol.find("layer") if symbol is not None else None
        props = {o.get("k"): o.get("v") for o in (layer.findall("Option") + layer.findall("prop"))} if layer is not None else {}
        color = _qml_color(props.get("color"), "#4fc3f7")
        opacity = _opacity(props.get("opacity", props.get("alpha")))
        try:
            width = float(props.get("width", props.get("line_width", 1.0)))
        except (ValueError, TypeError):
            width = 1.0
        try:
            radius = float(props.get("size", 5.0))
        except (ValueError, TypeError):
            radius = 5.0
        return json.dumps(_fragments_for_color(geometry, color, opacity, width, radius)), ""
    if rtype.lower() in ("categorizedsymbol", "categorized"):
        cats = renderer.findall("category")
        if not cats:
            return None, DEFAULT_FALLBACK_WARNING
        field = renderer.get("attr", "")
        fragments: list[dict] = []
        for cat in cats:
            value = cat.get("value", "")
            symbol = cat.find("symbol")
            layer = symbol.find("layer") if symbol is not None else None
            props = {o.get("k"): o.get("v") for o in (layer.findall("Option") + layer.findall("prop"))} if layer is not None else {}
            color = _qml_color(props.get("color"), "#4fc3f7")
            for frag in _fragments_for_color(geometry, color, 1.0):
                frag = dict(frag)
                if field:
                    frag["filter"] = ["==", ["get", field], value]
                fragments.append(frag)
        return json.dumps(fragments), ""
    if rtype.lower() in ("graduatedsymbol", "graduated"):
        ranges = renderer.findall("range")
        if not ranges:
            return None, DEFAULT_FALLBACK_WARNING
        field = renderer.get("attr", "")
        fragments = []
        for rng in ranges:
            symbol = rng.find("symbol")
            layer = symbol.find("layer") if symbol is not None else None
            props = {o.get("k"): o.get("v") for o in (layer.findall("Option") + layer.findall("prop"))} if layer is not None else {}
            color = _qml_color(props.get("color"), "#4fc3f7")
            try:
                lower, upper = float(rng.get("lower", 0)), float(rng.get("upper", 0))
            except (ValueError, TypeError):
                continue
            for frag in _fragments_for_color(geometry, color, 1.0):
                frag = dict(frag)
                if field:
                    frag["filter"] = ["all", [">=", ["get", field], lower], ["<", ["get", field], upper]]
                fragments.append(frag)
        return json.dumps(fragments), ""
    return None, DEFAULT_FALLBACK_WARNING
