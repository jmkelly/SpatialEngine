"""QGIS plugin bootstrap: dock widget registration (QGIS 3.34+)."""


class SpatialEnginePlugin:
    """Single dock hosting the Spatial Engine uploader/linker widget."""

    def __init__(self, iface):
        self.iface = iface
        self.dock = None
        self.widget = None
        self.logic = None

    def initGui(self):
        from qgis.PyQt.QtCore import Qt
        from qgis.PyQt.QtWidgets import QDockWidget

        from .dock_widget import SpatialEngineWidget, make_logic

        self.dock = QDockWidget("Spatial Engine", self.iface.mainWindow())
        self.widget = SpatialEngineWidget(make_logic, self.iface, self.dock)
        self.dock.setWidget(self.widget)
        # Qt5 (QGIS 3) exposes Qt.LeftDockWidgetArea; Qt6 (QGIS 4) moved it to
        # Qt.DockWidgetArea.LeftDockWidgetArea and rejects raw ints.
        area = getattr(Qt, "LeftDockWidgetArea", None)
        if area is None:
            area = Qt.DockWidgetArea.LeftDockWidgetArea
        self.iface.addDockWidget(area, self.dock)
        self.logic = self.widget._logic()
        self.widget.refresh_layers()

    def unload(self):
        if self.widget is not None:
            self.widget.close()
            self.widget = None
        if self.dock is not None:
            self.iface.removeDockWidget(self.dock)
            self.dock.deleteLater()
            self.dock = None
        self.logic = None
