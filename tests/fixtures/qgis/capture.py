"""Drive real QGIS WMS traffic at a live Spatial Engine host and print markers.

The host's OGC request diagnostics (commit f670fec) log the verbatim
parameters of every request, so the exact QGIS request URLs are recovered by
correlating this script's MARKER/URI lines with the host log's OGC lines::

    1. start a host with an admin token and an isolated maps file
    2. ingest ``lines.geojson``/``polys.geojson`` into the ``memory`` store
       and publish the ``qgis`` map (point + line + polygon, service ``wms``)
    3. QT_QPA_PLATFORM=offscreen python3 capture.py   (fresh XDG cache/config)

Requires QGIS with Python bindings (``import qgis.core``); QGIS 4.2.2 was
used for the checked-in traces. Headless rendering needs an event loop
around the parallel render job, otherwise the job silently issues nothing.
"""

import sys

HOST = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5213"
BASE = f"{HOST}/ogc/qgis/wms"

from qgis.core import (  # noqa: E402
    QgsApplication,
    QgsCoordinateReferenceSystem,
    QgsMapRendererParallelJob,
    QgsMapSettings,
    QgsPointXY,
    QgsRaster,
    QgsRasterLayer,
    QgsRectangle,
)
from qgis.PyQt.QtCore import QEventLoop, QSize  # noqa: E402

QgsApplication.setPrefixPath("/usr", True)
app = QgsApplication([], False)
QgsApplication.initQgis()


def marker(name):
    print(f"MARKER:{name}", flush=True)


def make_layer(layers, crs, name):
    parts = ["dpiMode=7", "featureCount=10", "format=image/png"]
    for layer in layers:
        parts.append(f"layers={layer}")
        parts.append("styles")
    parts.append(f"crs={crs}")
    parts.append(f"url={BASE}")
    uri = "&".join(parts)
    print(f"URI:{name}:{uri}", flush=True)
    layer = QgsRasterLayer(uri, name, "wms")
    print(f"VALID:{name}:{layer.isValid()}", flush=True)
    return layer


def render(layer, crs, rect, width, height, name):
    settings = QgsMapSettings()
    settings.setLayers([layer])
    settings.setDestinationCrs(QgsCoordinateReferenceSystem(crs))
    settings.setExtent(rect)
    settings.setOutputSize(QSize(width, height))
    job = QgsMapRendererParallelJob(settings)
    loop = QEventLoop()
    job.finished.connect(loop.quit)
    job.start()
    loop.exec()
    print(f"RENDER:{name}:{crs}:{job.errors()}", flush=True)
    job.renderedImage().save(f"{name}.png")


# 1. Adding the WMS layer: the provider issues GetCapabilities for url=.
combined = make_layer(["cities", "routes", "zones"], "EPSG:4326", "combined")
marker("add-layer-done")

# 2. Pan/zoom-to-full-extent GetMap in EPSG:4326. QGIS clips the BBOX to the
# layer extent and scales WIDTH/HEIGHT to match.
marker("getmap-4326-start")
render(combined, "EPSG:4326", QgsRectangle(-10, 35, 30, 60), 800, 600, "full-4326")
render(combined, "EPSG:4326", QgsRectangle(0, 50, 10, 60), 800, 800, "zoom-4326")
marker("getmap-4326-done")

# 3. GetMap in EPSG:3857. A layer negotiated in EPSG:4326 is reprojected
# client-side (the request stays CRS=EPSG:4326); the genuine 3857 request
# needs the layer negotiated in EPSG:3857.
marker("getmap-3857-start")
render(combined, "EPSG:3857", QgsRectangle(0, 6000000, 900000, 7000000), 800, 800, "zoom-4326-canvas")
mer = make_layer(["cities", "routes", "zones"], "EPSG:3857", "combined-mer")
render(mer, "EPSG:3857", QgsRectangle(0, 6000000, 900000, 7000000), 800, 800, "zoom-3857")
marker("getmap-3857-done")

# 4. Layer legend: QGIS issues no legend request against a server whose
# capabilities advertise no LegendURL (there is no GetLegendGraphic op).
marker("legend-start")
print(f"LEGEND:has-legend-graphic-hook:{hasattr(combined.dataProvider(), 'legendGraphicURI')}", flush=True)
marker("legend-done")

# 5. Identify (Ctrl+Shift+I equivalent) on point, line and polygon. Each
# provider identify issues GetFeatureInfo twice: GML then HTML.
marker("identify-start")
for layer_name, kind, point in [
    ("cities", "point", QgsPointXY(4.9041, 52.3676)),
    ("routes", "line", QgsPointXY(5.0, 53.0)),
    ("zones", "polygon", QgsPointXY(5.0, 52.0)),
]:
    single = make_layer([layer_name], "EPSG:4326", f"identify-{kind}")
    extent = QgsRectangle(-10, 35, 30, 60)
    for fmt in (QgsRaster.IdentifyFormatFeature, QgsRaster.IdentifyFormatHtml):
        result = single.dataProvider().identify(point, fmt, extent, 800, 600, 96)
        print(f"IDENTIFY:{kind}:{fmt}:{result.isValid()}:{len(result.results())}", flush=True)
    marker(f"identify-{kind}-done")
marker("identify-done")

QgsApplication.exitQgis()
print("CAPTURE:complete", flush=True)
