"""Dock wiring vs the QGIS API: geometry names, writer options, symbology args.

``spatial_engine_plugin`` imports QGIS lazily so tests inject fake ``qgis``
modules that mimic the QGIS 4 binding shapes (strict enums, typed options,
fixed-arity ``writeSymbology``). These reproduced three failures:
``str(geometryType())`` yields ``'0'``/``'1'``/``'2'`` so the layer filter hid
every layer; a dict passed as writer options raises ``TypeError``; the 2-arg
``writeSymbology`` call raises ``TypeError``.
"""

import sys
import types
import unittest

from clients.qgis.engine_client import EngineClient


POINT_QML = (
    "<qgis><renderer-v2 type=\"singlesymbol\">"
    "<symbol><layer>"
    "<Option k=\"color\" v=\"255,0,0,255\"/>"
    "</layer></symbol></renderer-v2></qgis>"
)


def _install_qgis_fakes(writer_error=0, writer_message=""):
    """Install fake qgis.core / qgis.PyQt.QtXml modules; returns (calls, project, layer_cls)."""
    calls = {}

    class FakeWkb:
        PointGeometry = 0
        LineGeometry = 1
        PolygonGeometry = 2
        UnknownGeometry = 3
        NullGeometry = 4

    class FakeMapLayerType:
        VectorLayer = 0

    class FakeCrs:
        def authid(self):
            return "EPSG:4326"

        def isValid(self):
            return True

    class FakeProvider:
        def dataSourceUri(self):
            return "dbname=test table=public.t"

    class FakeOptions:
        def __init__(self):
            self.driverName = ""
            self.fileEncoding = ""

    def _write(layer, file_name, context, options, calls=calls):
        calls["options_type"] = type(options).__name__
        if not isinstance(options, FakeOptions):
            raise TypeError(
                "writeAsVectorFormat(): argument 4 has unexpected type "
                f"'{type(options).__name__}'")
        with open(file_name, "wb") as handle:
            handle.write(b'{"type":"FeatureCollection","features":[]}')
        return writer_error, writer_message, file_name, "layer"

    class FakeWriter:
        NoError = 0
        SaveVectorOptions = FakeOptions
        writeAsVectorFormatV3 = staticmethod(_write)
        writeAsVectorFormatV2 = staticmethod(
            lambda layer, file_name, context, options: _write(
                layer, file_name, context, options)[:2])

    class FakeContext:
        pass

    class Layer:
        def __init__(self, lid, name, gtype):
            self._id, self._name, self._gtype = lid, name, gtype
            self.writeSymbology_args = None

        def id(self):
            return self._id

        def name(self):
            return self._name

        def providerType(self):
            return "postgres"

        def geometryType(self):
            return self._gtype

        def isSpatial(self):
            return True

        def type(self):
            return 0

        def crs(self):
            return FakeCrs()

        def dataProvider(self):
            return FakeProvider()

        def transformContext(self):
            return FakeContext()

        def writeSymbology(self, *args):
            self.writeSymbology_args = args
            if len(args) != 4:
                raise TypeError(
                    f"writeSymbology(): expected 4 arguments, got {len(args)}")
            return True

    class FakeProject:
        layers = {}

        @classmethod
        def instance(cls):
            return cls()

        def mapLayers(self):
            return dict(FakeProject.layers)

    class FakeDoc:
        def __init__(self, *args):
            pass

        def createElement(self, tag):
            return tag

        def appendChild(self, node):
            pass

        def toString(self):
            return POINT_QML

    qgis = types.ModuleType("qgis")
    core = types.ModuleType("qgis.core")
    core.QgsWkbTypes = FakeWkb
    core.QgsMapLayer = FakeMapLayerType
    core.QgsProject = FakeProject
    core.QgsVectorFileWriter = FakeWriter
    core.QgsReadWriteContext = FakeContext
    qgis.core = core
    pyqt = types.ModuleType("qgis.PyQt")
    qtxml = types.ModuleType("qgis.PyQt.QtXml")
    qtxml.QDomDocument = FakeDoc
    pyqt.QtXml = qtxml
    qgis.PyQt = pyqt
    sys.modules["qgis"] = qgis
    sys.modules["qgis.core"] = core
    sys.modules["qgis.PyQt"] = pyqt
    sys.modules["qgis.PyQt.QtXml"] = qtxml
    return calls, FakeProject, Layer


def _dock():
    sys.modules.pop("clients.qgis.spatial_engine_plugin", None)
    from clients.qgis.spatial_engine_plugin import SpatialEngineDock
    return SpatialEngineDock(EngineClient("http://host"))


class DockQgisApiTests(unittest.TestCase):
    def test_visible_layers_maps_geometry_types_to_filter_names(self):
        _, FakeProject, Layer = _install_qgis_fakes()
        FakeProject.layers = {
            "p": Layer("p", "pts", 0),
            "l": Layer("l", "lines", 1),
            "g": Layer("g", "polys", 2),
        }
        try:
            visible = _dock().visible_layers()
        finally:
            FakeProject.layers = {}
        self.assertEqual(
            sorted(v["geometry"] for v in visible),
            ["linestring", "point", "polygon"])

    def test_export_passes_save_vector_options_and_returns_bytes(self):
        calls, _, Layer = _install_qgis_fakes()
        data, srid = _dock().export_active_layer_geojson(Layer("p", "pts", 0))
        self.assertTrue(data.startswith(b"{"))
        self.assertEqual(srid, 4326)
        self.assertEqual(calls.get("options_type"), "FakeOptions")

    def test_export_surfaces_writer_error(self):
        _, _, Layer = _install_qgis_fakes(
            writer_error=1, writer_message="disk full")
        with self.assertRaises(RuntimeError):
            _dock().export_active_layer_geojson(Layer("p", "pts", 0))

    def test_convert_style_reads_symbology_with_full_args(self):
        _, _, Layer = _install_qgis_fakes()
        layer = Layer("p", "pts", 0)
        style, warning = _dock().convert_layer_style(layer, "point")
        self.assertEqual(len(layer.writeSymbology_args), 4)
        self.assertEqual(warning, "")
        self.assertIn("circle", style)


if __name__ == "__main__":
    unittest.main()
