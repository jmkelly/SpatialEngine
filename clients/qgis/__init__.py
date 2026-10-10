"""QGIS plugin entry point (QGIS 3.34+)."""


def classFactory(iface):  # noqa: N802 - QGIS-mandated entry-point name
    from .plugin_impl import SpatialEnginePlugin

    return SpatialEnginePlugin(iface)
