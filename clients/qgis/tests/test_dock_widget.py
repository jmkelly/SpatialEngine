"""Dock widget behaviour (requires QGIS: skipped in bare CI).

Runs the real widget offscreen against fake ``qgis.core`` project layers and
a fake-transport host: refresh populates the picker, upload/link flows run on
the worker and report back, cancel and failure surfaces are wired.
"""

import os
import sys
import time
import unittest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")


def _shadows_real_qgis(entry):
    """True when a sys.path entry provides the plugin package, not QGIS."""
    try:
        init = os.path.join(entry, "qgis", "__init__.py")
        return os.path.isfile(init) and "site-packages" not in os.path.abspath(init)
    except Exception:
        return False


def _load_real_qgis():
    """Import the real QGIS bindings despite the plugin shadowing ``qgis``.

    Returns the bound real modules; restores sys.path afterwards (the
    bindings stay in sys.modules until a test rebinds ``qgis.core``).
    """
    import importlib

    names = ("qgis", "qgis.PyQt", "qgis.PyQt.QtCore", "qgis.PyQt.QtGui",
             "qgis.PyQt.QtWidgets", "qgis.core")
    saved_path = list(sys.path)
    sys.path = [p for p in saved_path if not _shadows_real_qgis(p)]
    try:
        for name in names:
            sys.modules.pop(name, None)
        for name in names:
            importlib.import_module(name)
        return {name: sys.modules[name] for name in names}
    finally:
        sys.path = saved_path


try:
    _REAL_QGIS = _load_real_qgis()
    from qgis.PyQt.QtWidgets import QApplication

    HAS_QGIS = True
except ImportError:
    HAS_QGIS = False

from clients.qgis.tests.test_spatial_engine_dock import (
    _host_transport,
    _install_qgis_fakes,
)


def _pump_until(cond, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        QApplication.processEvents()
        if cond():
            return True
        time.sleep(0.02)
    QApplication.processEvents()
    return cond()


class FakeIface:
    def __init__(self):
        self.pushed = []

    def messageBar(self):
        return self

    def pushMessage(self, title, text, level=None):
        self.pushed.append((title, text, level))


@unittest.skipUnless(HAS_QGIS, "QGIS not installed")
class DockWidgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls._app = QApplication.instance() or QApplication([])

    def setUp(self):
        self._snapshot = dict(sys.modules)
        for mod in ("clients.qgis.spatial_engine_plugin",
                    "clients.qgis.dock_widget"):
            sys.modules.pop(mod, None)
        # Fake project layers; keep every other real binding (esp. PyQt).
        _, self.FakeProject, self.Layer = _install_qgis_fakes()
        for name, mod in _REAL_QGIS.items():
            if name != "qgis.core":
                sys.modules[name] = mod
        from clients.qgis.dock_widget import SpatialEngineWidget  # noqa: E402
        from clients.qgis.engine_client import EngineClient  # noqa: E402
        from clients.qgis.spatial_engine_plugin import (  # noqa: E402
            SpatialEngineDock,
        )
        self._log = []
        self._iface = FakeIface()
        self._widget_cls = SpatialEngineWidget
        self._deps = (EngineClient, SpatialEngineDock)

    def tearDown(self):
        for widget in getattr(self, "_widgets", []):
            try:
                widget.close()
            except Exception:
                pass
        current = dict(sys.modules)
        for key in current:
            if key not in self._snapshot:
                del sys.modules[key]
        for key, mod in self._snapshot.items():
            if sys.modules.get(key) is not mod:
                sys.modules[key] = mod
        self.FakeProject.layers = {}

    def _make_widget(self):
        EngineClient, SpatialEngineDock = self._deps
        log = self._log

        def make_logic(host, token):
            return SpatialEngineDock(
                EngineClient(host or "http://host",
                             transport=_host_transport(log)))

        widget = self._widget_cls(make_logic, self._iface)
        widget.SETTINGS_ORG = "SpatialEngineTest"
        widget.SETTINGS_APP = "DockWidgetTests"
        self._widgets = getattr(self, "_widgets", []) + [widget]
        return widget

    def _wait_idle(self, widget):
        self.assertTrue(
            _pump_until(widget.run_button.isEnabled),
            "worker did not finish:\n" + widget.log_view.toPlainText())

    def test_refresh_populates_picker_and_reports_hidden(self):
        self.FakeProject.layers = {
            "p": self.Layer("p", "pts", 0),
            "l": self.Layer("l", "lines", 1),
            "r": self.Layer("r", "hillshade", 2, ltype=1),
        }
        widget = self._make_widget()
        widget.refresh_layers()
        self.assertEqual(widget.layer_combo.count(), 2)
        self.assertEqual(widget.dataset_edit.text(), "pts")
        self.assertIn("hidden: hillshade", widget.log_view.toPlainText())

    def test_upload_flow_end_to_end(self):
        self.FakeProject.layers = {"p": self.Layer("p", "pts", 0)}
        widget = self._make_widget()
        widget.refresh_layers()
        widget.map_edit.setText("m")
        widget.dataset_edit.setText("d")
        widget.run_button.click()
        self._wait_idle(widget)
        text = widget.log_view.toPlainText()
        self.assertIn("uploaded: 'm' saved (1 layer(s))", text)
        self.assertTrue(any(u.endswith("/api/maps/m")
                            for _, u in self._log))

    def test_run_with_no_layers_logs_hint(self):
        widget = self._make_widget()
        widget.refresh_layers()
        widget.run_button.click()
        self.assertIn("pick an uploadable layer",
                      widget.log_view.toPlainText())

    def test_link_flow_end_to_end(self):
        widget = self._make_widget()
        widget.link_radio.setChecked(True)
        widget.load_button.click()
        self.assertTrue(
            _pump_until(lambda: widget.table_combo.count() == 1),
            "catalogue did not load:\n" + widget.log_view.toPlainText())
        widget.map_edit.setText("m")
        widget.run_button.click()
        self._wait_idle(widget)
        text = widget.log_view.toPlainText()
        self.assertIn("catalogue 'postgis': 1 table(s)", text)
        self.assertIn("linked: 'm' saved (1 layer(s))", text)

    def test_worker_cancel_reports_cancelled(self):
        from qgis.PyQt.QtCore import QEventLoop, QTimer  # noqa: E402

        from clients.qgis.dock_widget import FlowWorker  # noqa: E402
        from clients.qgis.engine_client import Cancelled  # noqa: E402

        def slow(is_cancelled, on_progress):
            for _ in range(1000):
                if is_cancelled():
                    raise Cancelled("stop")
                time.sleep(0.01)
            return {}

        outcome = []
        loop = QEventLoop()
        worker = FlowWorker(slow)
        worker.cancelled.connect(lambda: (outcome.append("cancelled"),
                                          loop.quit()))
        worker.failed.connect(lambda m: (outcome.append(f"failed:{m}"),
                                         loop.quit()))
        worker.finished.connect(lambda r: (outcome.append("finished"),
                                           loop.quit()))
        QTimer.singleShot(100, worker.cancel)
        QTimer.singleShot(10000, loop.quit)
        worker.start()
        loop.exec()
        worker.wait(5000)
        self.assertEqual(outcome, ["cancelled"])

    def test_worker_failure_surfaces_message(self):
        from qgis.PyQt.QtCore import QEventLoop, QTimer  # noqa: E402

        from clients.qgis.dock_widget import FlowWorker  # noqa: E402

        def boom(is_cancelled, on_progress):
            raise RuntimeError("host exploded")

        outcome = []
        loop = QEventLoop()
        worker = FlowWorker(boom)
        worker.failed.connect(lambda m: (outcome.append(m), loop.quit()))
        QTimer.singleShot(10000, loop.quit)
        worker.start()
        loop.exec()
        worker.wait(5000)
        self.assertEqual(outcome, ["host exploded"])


if __name__ == "__main__":
    unittest.main()
