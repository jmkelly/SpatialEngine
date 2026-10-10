"""QGIS dock widget: layer picker, mode radio, host/auth, map name, upload.

QGIS imports live inside methods so the pure modules (and pytest) never need
QGIS installed. v1 scope per grill: QGIS 3.34+, single dock, one flow.
"""

from __future__ import annotations

from .engine_client import EngineClient
from .layer_filter import filter_layers
from .map_builder import append_layer
from .style_convert import qml_to_fragments


class SpatialEngineDock:
    """Wires QGIS widgets to :class:`EngineClient`; constructed by the plugin."""

    def __init__(self, client: EngineClient):
        self.client = client

    def visible_layers(self):
        """Return only supported vector layers from the current QGIS project."""
        from qgis.core import QgsProject

        infos = []
        for layer in QgsProject.instance().mapLayers().values():
            uri = layer.dataProvider().dataSourceUri() if layer.dataProvider() else ""
            infos.append({
                "id": layer.id(),
                "name": layer.name(),
                "provider": layer.providerType(),
                "geometry": str(layer.geometryType()),
                "hasSpatial": layer.isSpatial(),
                "isRaster": layer.type() != 0,  # 0 == VectorLayer
                "crsValid": layer.crs().isValid(),
                "uri": uri,
            })
        visible, _ = filter_layers(infos)
        by_id = {i["id"]: i for i in infos}
        return [by_id[v["id"]] for v in visible]

    def export_active_layer_geojson(self, layer) -> tuple[bytes, int]:
        """Export a vector layer to GeoJSON bytes plus its EPSG SRID."""
        from qgis.core import QgsVectorFileWriter
        import tempfile
        import os

        authid = layer.crs().authid()  # e.g. "EPSG:4326"
        srid = int(authid.split(":")[1]) if ":" in authid else 4326
        path = os.path.join(tempfile.mkdtemp(), "layer.geojson")
        QgsVectorFileWriter.writeAsVectorFormatV2(layer, path, layer.transformContext(), {
            "driverName": "GeoJSON", "fileEncoding": "UTF-8"})
        with open(path, "rb") as handle:
            return handle.read(), srid

    def convert_layer_style(self, layer, geometry: str):
        """Read the layer QML and convert to a persisted style string (or None)."""
        from qgis.core import QgsReadWriteContext
        from qgis.PyQt.QtXml import QDomDocument

        doc = QDomDocument()
        layer.writeSymbology(doc, QgsReadWriteContext())
        return qml_to_fragments(doc.toString(), geometry)

    def upload_layer(self, layer, map_name: str, dataset: str, store: str = "memory",
                     source_srid: int | None = None, is_cancelled=None) -> dict:
        """Upload-copy path: export, convert style, ingest, append to map."""
        data, srid = self.export_active_layer_geojson(layer)
        style, _ = self.convert_layer_style(layer, str(layer.geometryType()))
        result = self.client.ingest_resumable(
            data, f"{dataset}.geojson", dataset, srid,
            store=store, publish=None,
            source_srid=source_srid if source_srid != srid else None,
            is_cancelled=is_cancelled)
        existing = self.client.get_map(map_name)
        merged = append_layer(existing, map_name, result["dataset"], style, store,
                              layer_name=layer.name())
        return self.client.put_map(merged)

    def link_table(self, map_name: str, dataset: str, style: str | None,
                   store: str, layer_name: str | None = None) -> dict:
        """Link path: no bytes, just catalogue-validated map append."""
        existing = self.client.get_map(map_name)
        merged = append_layer(existing, map_name, dataset, style, store, layer_name)
        return self.client.put_map(merged)

    def push_connection(self, store: str, connection_string: str) -> dict:
        """Explicit admin-only connection push (Q14: overwrite warning in UI)."""
        return self.client.set_connection(store, connection_string)
