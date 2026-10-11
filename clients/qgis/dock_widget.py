"""Functional QGIS dock UI over :class:`SpatialEngineDock` logic.

Single dock, one flow per mode: configure host + token, pick a layer, upload
a copy (export → staged ingest → append to map) or link an existing
catalogue table (zero bytes moved), optionally push a connection string
(admin, explicit overwrite). Long-running network work runs on a worker
thread; everything touching QGIS objects stays on the GUI thread.

Top-level QGIS/PyQt imports are normal for a plugin UI module; the logic it
drives (``spatial_engine_plugin``, ``engine_client``) stays QGIS-free and is
covered by the pytest suite. Widget tests live in
``tests/test_dock_widget.py`` and skip when QGIS is not installed.
"""

from __future__ import annotations

import threading

from qgis.PyQt.QtCore import QObject, QSettings, QThread, pyqtSignal
from qgis.PyQt.QtGui import QFont
from qgis.PyQt.QtWidgets import (
    QComboBox,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPlainTextEdit,
    QProgressBar,
    QPushButton,
    QRadioButton,
    QSpinBox,
    QStackedWidget,
    QVBoxLayout,
    QWidget,
)

from .engine_client import Cancelled, EngineClient
from .spatial_engine_plugin import SpatialEngineDock

STORES = ("memory", "demo", "postgis", "sqlserver")


def make_logic(host: str, token: str) -> SpatialEngineDock:
    """Default logic factory: fresh client per run so host/token edits apply."""
    return SpatialEngineDock(EngineClient(host, token or None))


class FlowWorker(QThread):
    """Run ``fn(is_cancelled, on_progress)`` off the GUI thread.

    ``is_cancelled`` is a callable returning bool; ``on_progress`` accepts
    ``(sent_bytes, total_bytes)``. A :class:`Cancelled` escape (or an
    external :meth:`cancel` before completion) reports via ``cancelled``;
    any other exception reports via ``failed``.
    """

    progress = pyqtSignal(int, int)
    finished = pyqtSignal(object)
    failed = pyqtSignal(str)
    cancelled = pyqtSignal()

    def __init__(self, fn, parent: QObject | None = None):
        super().__init__(parent)
        self._fn = fn
        self._cancel = threading.Event()

    def cancel(self) -> None:
        self._cancel.set()

    def run(self) -> None:  # noqa: C901 - linear outcome mapping
        try:
            result = self._fn(is_cancelled=self._cancel.is_set,
                              on_progress=self.progress.emit)
        except Cancelled:
            self.cancelled.emit()
        except Exception as exc:  # network, host error codes, programming bugs
            self.failed.emit(str(exc))
        else:
            if self._cancel.is_set():
                self.cancelled.emit()
            else:
                self.finished.emit(result)


class SpatialEngineWidget(QWidget):
    """The dock contents. ``make_logic(host, token)`` builds the driver."""

    SETTINGS_ORG = "SpatialEngine"
    SETTINGS_APP = "QgisPlugin"

    def __init__(self, make_logic=make_logic, iface=None,
                 parent: QWidget | None = None):
        super().__init__(parent)
        self._make_logic = make_logic
        self._iface = iface
        self._worker: FlowWorker | None = None
        self._infos_by_id: dict = {}
        self._build()
        self._load_settings()
        self._set_running(False)

    # -- construction ----------------------------------------------------

    def _build(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(6, 6, 6, 6)

        conn = QGroupBox("Connection", self)
        form = QFormLayout(conn)
        self.host_edit = QLineEdit("http://localhost:5000", conn)
        self.token_edit = QLineEdit(conn)
        self.token_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.token_edit.setPlaceholderText("bearer token (optional)")
        form.addRow("Host:", self.host_edit)
        form.addRow("Token:", self.token_edit)
        root.addWidget(conn)

        layers = QGroupBox("Layer", self)
        lay_row = QHBoxLayout()
        self.refresh_button = QPushButton("Refresh", layers)
        self.refresh_button.clicked.connect(self.refresh_layers)
        self.layer_count = QLabel("no layers loaded", layers)
        self.layer_count.setWordWrap(True)
        lay_row.addWidget(self.refresh_button)
        lay_row.addWidget(self.layer_count, 1)
        lay_col = QVBoxLayout(layers)
        lay_col.addLayout(lay_row)
        self.layer_combo = QComboBox(layers)
        self.layer_combo.currentIndexChanged.connect(self._on_layer_changed)
        lay_col.addWidget(self.layer_combo)
        root.addWidget(layers)

        mode_row = QHBoxLayout()
        self.upload_radio = QRadioButton("Upload copy", self)
        self.link_radio = QRadioButton("Link existing", self)
        self.upload_radio.setChecked(True)
        self.upload_radio.toggled.connect(self._on_mode_changed)
        mode_row.addWidget(self.upload_radio)
        mode_row.addWidget(self.link_radio)
        mode_row.addStretch(1)
        root.addLayout(mode_row)

        target = QGroupBox("Target", self)
        target_form = QFormLayout(target)
        self.map_edit = QLineEdit("spatial-engine", target)
        target_form.addRow("Map:", self.map_edit)
        self.pages = QStackedWidget(target)

        upload_page = QWidget(self.pages)
        upload_form = QFormLayout(upload_page)
        self.dataset_edit = QLineEdit(upload_page)
        self.dataset_edit.setPlaceholderText("defaults to the layer name")
        self.store_combo = QComboBox(upload_page)
        self.store_combo.addItems(STORES)
        self.srid_spin = QSpinBox(upload_page)
        self.srid_spin.setRange(0, 999999)
        self.srid_spin.setSpecialValueText("layer CRS")
        upload_form.addRow("Dataset:", self.dataset_edit)
        upload_form.addRow("Store:", self.store_combo)
        upload_form.addRow("Source SRID:", self.srid_spin)
        self.pages.addWidget(upload_page)

        link_page = QWidget(self.pages)
        link_form = QFormLayout(link_page)
        link_row = QHBoxLayout()
        self.link_store_combo = QComboBox(link_page)
        self.link_store_combo.addItems(STORES)
        self.link_store_combo.setCurrentText("postgis")
        self.load_button = QPushButton("Load", link_page)
        self.load_button.clicked.connect(self._on_load_tables)
        link_row.addWidget(self.link_store_combo, 1)
        link_row.addWidget(self.load_button)
        link_form.addRow("Store:", link_row)
        self.table_combo = QComboBox(link_page)
        link_form.addRow("Table:", self.table_combo)
        self.pages.addWidget(link_page)

        target_form.addRow(self.pages)
        root.addWidget(target)

        run_row = QHBoxLayout()
        self.run_button = QPushButton("Upload", self)
        self.run_button.clicked.connect(self._on_run)
        self.cancel_button = QPushButton("Cancel", self)
        self.cancel_button.clicked.connect(self._on_cancel)
        run_row.addWidget(self.run_button, 1)
        run_row.addWidget(self.cancel_button)
        root.addLayout(run_row)
        self.progress = QProgressBar(self)
        self.progress.setVisible(False)
        root.addWidget(self.progress)

        admin = QGroupBox("Connection string (admin)", self)
        admin.setCheckable(True)
        admin.setChecked(False)
        admin_form = QFormLayout(admin)
        self.conn_edit = QLineEdit(admin)
        self.conn_edit.setPlaceholderText("prefilled from the selected layer")
        self.push_button = QPushButton("Push (overwrite)", admin)
        self.push_button.clicked.connect(self._on_push)
        admin_form.addRow("Connection:", self.conn_edit)
        admin_form.addRow("", self.push_button)
        root.addWidget(admin)

        self.log_view = QPlainTextEdit(self)
        self.log_view.setReadOnly(True)
        self.log_view.setMaximumBlockCount(500)
        self.log_view.setFont(QFont("monospace"))
        root.addWidget(self.log_view, 1)

    # -- settings --------------------------------------------------------

    def _settings(self) -> QSettings:
        return QSettings(self.SETTINGS_ORG, self.SETTINGS_APP)

    def _load_settings(self) -> None:
        settings = self._settings()
        self.host_edit.setText(
            settings.value("host", "http://localhost:5000"))
        self.token_edit.setText(settings.value("token", ""))
        self.map_edit.setText(settings.value("map", "spatial-engine"))
        store = settings.value("store", "memory")
        if store in STORES:
            self.store_combo.setCurrentText(store)

    def _save_settings(self) -> None:
        settings = self._settings()
        settings.setValue("host", self.host_edit.text().strip())
        settings.setValue("token", self.token_edit.text())
        settings.setValue("map", self.map_edit.text().strip())
        settings.setValue("store", self.store_combo.currentText())

    # -- helpers ---------------------------------------------------------

    def log(self, message: str) -> None:
        self.log_view.appendPlainText(message)

    def _message(self, level: str, title: str, text: str) -> None:
        from qgis.core import Qgis

        bar = getattr(self._iface, "messageBar", lambda: None)()
        push = getattr(bar, "pushMessage", None)
        if callable(push):
            push(title, text, getattr(Qgis, level, Qgis.Info))

    def _logic(self):
        return self._make_logic(self.host_edit.text().strip(),
                                self.token_edit.text())

    def _current_layer(self):
        from qgis.core import QgsProject

        lid = self.layer_combo.currentData()
        return QgsProject.instance().mapLayer(lid) if lid else None

    def _set_running(self, running: bool) -> None:
        for widget in (self.host_edit, self.token_edit, self.refresh_button,
                       self.layer_combo, self.upload_radio, self.link_radio,
                       self.map_edit, self.dataset_edit, self.store_combo,
                       self.srid_spin, self.link_store_combo, self.load_button,
                       self.table_combo, self.run_button, self.push_button,
                       self.conn_edit):
            widget.setEnabled(not running)
        self.cancel_button.setEnabled(running)
        self.progress.setVisible(running)
        if running:
            self.progress.setRange(0, 0)  # indeterminate until bytes report

    # -- layer picker ----------------------------------------------------

    def refresh_layers(self) -> None:
        try:
            logic = self._logic()
            visible = logic.visible_layers()
            hidden = logic.describe_hidden()
        except Exception as exc:
            self.log(f"layer refresh failed: {exc}")
            return
        self._infos_by_id = {info["id"]: info for info in visible}
        self.layer_combo.blockSignals(True)
        try:
            self.layer_combo.clear()
            for info in visible:
                self.layer_combo.addItem(
                    f"{info['name']} ({info['geometry']}, {info['provider']})",
                    info["id"])
        finally:
            self.layer_combo.blockSignals(False)
        self.layer_count.setText(
            f"{len(visible)} uploadable layer(s), {len(hidden)} hidden")
        for name, reason in hidden:
            self.log(f"hidden: {name} — {reason}")
        if visible:
            self._on_layer_changed()
        else:
            self.dataset_edit.clear()
            self.conn_edit.clear()
        self.log(f"refreshed: {len(visible)} layer(s) listed")

    def _on_layer_changed(self) -> None:
        lid = self.layer_combo.currentData()
        info = self._infos_by_id.get(lid, {})
        name = info.get("name", "")
        if name and not self.dataset_edit.text().strip():
            self.dataset_edit.setText(name.strip())
        uri = info.get("uri", "")
        if uri:
            self.conn_edit.setText(uri)

    def _on_mode_changed(self) -> None:
        link = self.link_radio.isChecked()
        self.pages.setCurrentIndex(1 if link else 0)
        self.run_button.setText("Link" if link else "Upload")

    # -- catalogue (link mode) -------------------------------------------

    def _on_load_tables(self) -> None:
        store = self.link_store_combo.currentText()
        try:
            logic = self._logic()
        except Exception as exc:
            self.log(f"catalogue load failed: {exc}")
            return
        self._set_running(True)
        self.log(f"loading catalogue for store '{store}' …")
        worker = FlowWorker(
            lambda is_cancelled, on_progress: logic.load_catalogue(store),
            self)
        worker.finished.connect(
            lambda summaries: self._on_tables_loaded(store, summaries))
        worker.failed.connect(self._on_worker_failed)
        worker.cancelled.connect(self._on_worker_cancelled)
        self._start_worker(worker)

    def _on_tables_loaded(self, store: str, summaries) -> None:
        self._finish_worker()
        self.table_combo.clear()
        for entry in summaries or []:
            label = (f"{entry.get('schema', '')}.{entry.get('table', '')}"
                     f" — {entry.get('estimatedRowCount', '?')} rows")
            self.table_combo.addItem(label, entry.get("id"))
        self.log(f"catalogue '{store}': {self.table_combo.count()} table(s)")
        if not summaries:
            self.log("catalogue is empty — ingest or link a table first")

    # -- run -------------------------------------------------------------

    def _on_run(self) -> None:
        if self.link_radio.isChecked():
            self._run_link()
        else:
            self._run_upload()

    def _validated_target(self, dataset: str) -> str | None:
        map_name = self.map_edit.text().strip()
        if not map_name:
            self.log("give the target map a name first")
            return None
        if not dataset:
            self.log("give the dataset a name first")
            return None
        return map_name

    def _run_upload(self) -> None:
        layer = self._current_layer()
        if layer is None:
            self.log("pick an uploadable layer first (Refresh if empty)")
            return
        dataset = self.dataset_edit.text().strip() or layer.name().strip()
        map_name = self._validated_target(dataset)
        if map_name is None:
            return
        store = self.store_combo.currentText()
        try:
            logic = self._logic()
            self.log(f"exporting '{layer.name()}' …")
            prep = logic.prepare_upload(layer)
        except Exception as exc:
            self.log(f"export failed: {exc}")
            return
        if prep.get("style_warning"):
            self.log(f"style: {prep['style_warning']}")
        srid_spin = self.srid_spin.value()
        source_srid = srid_spin if srid_spin not in (0, prep["srid"]) else None
        self._save_settings()
        self._set_running(True)
        self.log(f"uploading '{dataset}' to store '{store}' …")
        worker = FlowWorker(
            lambda is_cancelled, on_progress: logic.run_upload_flow(
                prep, map_name=map_name, dataset=dataset, store=store,
                source_srid=source_srid, is_cancelled=is_cancelled,
                on_progress=on_progress),
            self)
        worker.progress.connect(self._on_progress)
        worker.finished.connect(
            lambda saved: self._on_saved(map_name, saved, "uploaded"))
        worker.failed.connect(self._on_worker_failed)
        worker.cancelled.connect(self._on_worker_cancelled)
        self._start_worker(worker)

    def _run_link(self) -> None:
        dataset = self.table_combo.currentData()
        if not dataset:
            self.log("load the catalogue and pick a table first")
            return
        map_name = self._validated_target(dataset)
        if map_name is None:
            return
        store = self.link_store_combo.currentText()
        try:
            logic = self._logic()
        except Exception as exc:
            self.log(f"link failed: {exc}")
            return
        self._save_settings()
        self._set_running(True)
        self.log(f"linking '{dataset}' from store '{store}' …")
        worker = FlowWorker(
            lambda is_cancelled, on_progress: logic.run_link_flow(
                map_name=map_name, dataset=dataset, style=None, store=store,
                layer_name=self.table_combo.currentText()),
            self)
        worker.finished.connect(
            lambda saved: self._on_saved(map_name, saved, "linked"))
        worker.failed.connect(self._on_worker_failed)
        worker.cancelled.connect(self._on_worker_cancelled)
        self._start_worker(worker)

    # -- admin connection push -------------------------------------------

    def _on_push(self) -> None:
        store = self.link_store_combo.currentText()
        connection = self.conn_edit.text().strip()
        if not connection:
            self.log("paste a connection string first")
            return
        answer = QMessageBox.warning(
            self, "Overwrite connection?",
            f"This blind-overwrites the '{store}' connection on the host. "
            "The secret is never read back. Continue?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No)
        if answer != QMessageBox.StandardButton.Yes:
            return
        try:
            logic = self._logic()
        except Exception as exc:
            self.log(f"connection push failed: {exc}")
            return
        self._set_running(True)
        self.log(f"pushing '{store}' connection …")
        worker = FlowWorker(
            lambda is_cancelled, on_progress: logic.push_connection(
                store, connection),
            self)
        worker.finished.connect(
            lambda _saved: self._on_saved(store, None, "connection pushed"))
        worker.failed.connect(self._on_worker_failed)
        worker.cancelled.connect(self._on_worker_cancelled)
        self._start_worker(worker)

    # -- worker plumbing --------------------------------------------------

    def _start_worker(self, worker: FlowWorker) -> None:
        self._on_cancel_cleanup()
        self._worker = worker
        worker.finished.connect(self._forget_worker)
        worker.failed.connect(self._forget_worker)
        worker.cancelled.connect(self._forget_worker)
        worker.start()

    def _forget_worker(self, *args) -> None:
        self._worker = None

    def _on_cancel_cleanup(self) -> None:
        worker, self._worker = self._worker, None
        if worker is not None:
            worker.cancel()
            worker.wait(2000)

    def _on_cancel(self) -> None:
        if self._worker is not None:
            self.log("cancelling …")
            self.cancel_button.setEnabled(False)
            self._worker.cancel()

    def _finish_worker(self) -> None:
        self._forget_worker()
        self._set_running(False)

    def _on_progress(self, sent: int, total: int) -> None:
        if total > 0:
            self.progress.setRange(0, total)
            self.progress.setValue(sent)

    def _on_saved(self, name: str, saved, verb: str) -> None:
        self._finish_worker()
        if isinstance(saved, dict) and "layers" in saved:
            self.log(f"{verb}: '{name}' saved ({len(saved['layers'])} layer(s))")
        else:
            self.log(f"{verb}: '{name}'")
        self._message("Info", "Spatial Engine", f"{verb}: '{name}'")

    def _on_worker_failed(self, message: str) -> None:
        self._finish_worker()
        self.log(f"failed: {message}")
        self._message("Critical", "Spatial Engine", message)

    def _on_worker_cancelled(self) -> None:
        self._finish_worker()
        self.log("cancelled — staging cleaned up on the host")
        self._message("Warning", "Spatial Engine", "cancelled")

    # -- Qt overrides -----------------------------------------------------

    def closeEvent(self, event) -> None:  # noqa: N802 - Qt-mandated name
        self._on_cancel_cleanup()
        super().closeEvent(event)
