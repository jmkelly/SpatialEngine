"""QGIS plugin bootstrap: dock widget registration (QGIS 3.34+)."""


class SpatialEnginePlugin:
    """Minimal dock-widget plugin: one dock, one flow (Q17)."""

    def __init__(self, iface):
        self.iface = iface
        self.dock = None

    def initGui(self):
        from qgis.PyQt.QtWidgets import QDockWidget, QLabel

        from .engine_client import EngineClient
        from .spatial_engine_plugin import SpatialEngineDock

        self.dock = QDockWidget("Spatial Engine", self.iface.mainWindow())
        self.dock.setWidget(QLabel("Configure host + token, pick a layer, Upload."))
        self.iface.addDockWidget(1, self.dock)
        self.logic = SpatialEngineDock(EngineClient("http://localhost:5000"))

    def unload(self):
        if self.dock is not None:
            self.iface.removeDockWidget(self.dock)
            self.dock = None
