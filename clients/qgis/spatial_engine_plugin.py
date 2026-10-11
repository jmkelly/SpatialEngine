"""QGIS dock widget: layer picker, mode radio, host/auth, map name, upload.

QGIS imports live inside methods so the pure modules (and pytest) never need
QGIS installed. v1 scope per grill: QGIS 3.34+, single dock, one flow.
"""

from __future__ import annotations

from .engine_client import EngineClient
from .layer_filter import filter_layers
from .map_builder import append_layer
from .style_convert import qml_to_fragments


def _geometry_name(geometry_type) -> str:
    """Map a ``QgsWkbTypes.GeometryType`` to the filter/style geometry name.

    ``str(geometryType())`` yields ``'0'``/``'1'``/``'2'`` on both QGIS 3 and
    4 (SIP enums stringify as ints), which the layer filter rejects — so the
    mapping must be explicit. ``line`` is normalised to ``linestring`` to
    match ``SUPPORTED_GEOMETRIES``.
    """
    from qgis.core import QgsWkbTypes

    if geometry_type == QgsWkbTypes.PointGeometry:
        return "point"
    if geometry_type == QgsWkbTypes.LineGeometry:
        return "linestring"
    if geometry_type == QgsWkbTypes.PolygonGeometry:
        return "polygon"
    if geometry_type == QgsWkbTypes.UnknownGeometry:
        return "unknown"
    return "null"


class SpatialEngineDock:
    """Wires QGIS widgets to :class:`EngineClient`; constructed by the plugin."""

    def __init__(self, client: EngineClient):
        self.client = client

    def _all_layer_infos(self) -> list:
        """Describe every project layer (visible or not) as filter inputs."""
        from qgis.core import QgsMapLayer, QgsProject

        infos = []
        for layer in QgsProject.instance().mapLayers().values():
            uri = layer.dataProvider().dataSourceUri() if layer.dataProvider() else ""
            infos.append({
                "id": layer.id(),
                "name": layer.name(),
                "provider": layer.providerType(),
                "geometry": _geometry_name(layer.geometryType()),
                "hasSpatial": layer.isSpatial(),
                "isRaster": layer.type() != QgsMapLayer.VectorLayer,
                "crsValid": layer.crs().isValid(),
                "uri": uri,
            })
        return infos

    def visible_layers(self):
        """Return only supported vector layers from the current QGIS project."""
        infos = self._all_layer_infos()
        visible, _ = filter_layers(infos)
        by_id = {i["id"]: i for i in infos}
        return [by_id[v["id"]] for v in visible]

    def describe_hidden(self) -> list:
        """Return ``[(name, reason)]`` for layers the picker hides (Q15)."""
        _, hidden = filter_layers(self._all_layer_infos())
        return [(info["name"], reason) for info, reason in hidden]

    def export_active_layer_geojson(self, layer) -> tuple[bytes, int]:
        """Export a vector layer to GeoJSON bytes plus its EPSG SRID."""
        from qgis.core import QgsVectorFileWriter
        import tempfile
        import os

        authid = layer.crs().authid()  # e.g. "EPSG:4326"
        srid = int(authid.split(":")[1]) if ":" in authid else 4326
        # The writer takes a SaveVectorOptions object, never a dict (the
        # binding raises TypeError otherwise on both QGIS 3 and 4). V3 exists
        # since QGIS 3.20, so it is safe for the 3.34 floor and current on 4.x.
        options = QgsVectorFileWriter.SaveVectorOptions()
        options.driverName = "GeoJSON"
        options.fileEncoding = "UTF-8"
        write = getattr(QgsVectorFileWriter, "writeAsVectorFormatV3",
                        QgsVectorFileWriter.writeAsVectorFormatV2)
        with tempfile.TemporaryDirectory(prefix="spatial-engine-") as tmp:
            path = os.path.join(tmp, "layer.geojson")
            result = write(layer, path, layer.transformContext(), options)
            error, message = result[0], result[1]
            if error != QgsVectorFileWriter.NoError:
                raise RuntimeError(f"layer export failed: {message or error}")
            with open(path, "rb") as handle:
                return handle.read(), srid

    def convert_layer_style(self, layer, geometry: str):
        """Read the layer QML and convert to a persisted style string (or None)."""
        from qgis.core import QgsReadWriteContext
        from qgis.PyQt.QtXml import QDomDocument

        # writeSymbology takes (node, doc, errorMessage, context); fewer args
        # raise TypeError on both QGIS 3 and 4.
        doc = QDomDocument("qgis")
        node = doc.createElement("qgis")
        doc.appendChild(node)
        if not layer.writeSymbology(node, doc, "", QgsReadWriteContext()):
            return None, "could not read layer symbology; falling back to engine default style"
        return qml_to_fragments(doc.toString(), geometry)

    def prepare_upload(self, layer) -> dict:
        """Collect everything the upload flow needs while on the GUI thread.

        Export, style conversion and geometry naming all touch QGIS objects,
        so the dock calls this before handing the returned plain-data dict to
        a worker thread running :meth:`run_upload_flow`.
        """
        data, srid = self.export_active_layer_geojson(layer)
        geometry = _geometry_name(layer.geometryType())
        style, warning = self.convert_layer_style(layer, geometry)
        return {"data": data, "srid": srid, "style": style,
                "style_warning": warning, "geometry": geometry,
                "layer_name": layer.name()}

    def run_upload_flow(self, prep: dict, *, map_name: str, dataset: str,
                        store: str = "memory",
                        source_srid: int | None = None,
                        is_cancelled=None, on_progress=None) -> dict:
        """Worker-safe upload flow: ingest bytes, append to map, save.

        Takes only the plain-data dict from :meth:`prepare_upload` — no QGIS
        objects — so it may run off the GUI thread.
        """
        result = self.client.ingest_resumable(
            prep["data"], f"{dataset}.geojson", dataset, prep["srid"],
            store=store, publish=None,
            source_srid=(source_srid if source_srid not in (None, prep["srid"])
                         else None),
            is_cancelled=is_cancelled, on_progress=on_progress)
        existing = self.client.get_map(map_name)
        merged = append_layer(existing, map_name, result["dataset"],
                              prep["style"], store,
                              layer_name=prep["layer_name"])
        return self.client.put_map(merged)

    def run_link_flow(self, *, map_name: str, dataset: str, style: str | None,
                      store: str, layer_name: str | None = None) -> dict:
        """Worker-safe link flow: catalogue-validated map append, no bytes."""
        existing = self.client.get_map(map_name)
        merged = append_layer(existing, map_name, dataset, style, store,
                              layer_name)
        return self.client.put_map(merged)

    def load_catalogue(self, store: str) -> list:
        """Return dataset summaries for *store* (worker-safe, plain data)."""
        return self.client.list_catalogue(store)

    def upload_layer(self, layer, map_name: str, dataset: str, store: str = "memory",
                     source_srid: int | None = None, is_cancelled=None) -> dict:
        """Upload-copy path: export, convert style, ingest, append to map."""
        return self.run_upload_flow(
            self.prepare_upload(layer), map_name=map_name, dataset=dataset,
            store=store, source_srid=source_srid, is_cancelled=is_cancelled)

    def link_table(self, map_name: str, dataset: str, style: str | None,
                   store: str, layer_name: str | None = None) -> dict:
        """Link path: no bytes, just catalogue-validated map append."""
        existing = self.client.get_map(map_name)
        merged = append_layer(existing, map_name, dataset, style, store, layer_name)
        return self.client.put_map(merged)

    def push_connection(self, store: str, connection_string: str) -> dict:
        """Explicit admin-only connection push (Q14: overwrite warning in UI)."""
        return self.client.set_connection(store, connection_string)
