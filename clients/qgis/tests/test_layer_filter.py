"""Layer picker hides unsupported layers instead of erroring later."""

import unittest

from clients.qgis.layer_filter import filter_layers


class LayerFilterTests(unittest.TestCase):
    def test_supported_vector_layer_is_visible(self):
        visible, hidden = filter_layers([{"id": "a", "provider": "ogr",
                                          "geometry": "polygon", "hasSpatial": True}])
        self.assertEqual(len(visible), 1)
        self.assertEqual(hidden, [])

    def test_raster_wms_mesh_virtual_join_hidden(self):
        infos = [{"id": "r", "isRaster": True},
                 {"id": "w", "provider": "wms"},
                 {"id": "f", "provider": "wfs"},
                 {"id": "m", "isMesh": True},
                 {"id": "v", "isVirtual": True},
                 {"id": "j", "hasJoin": True},
                 {"id": "n", "hasSpatial": False}]
        visible, hidden = filter_layers(infos)
        self.assertEqual(visible, [])
        self.assertEqual(len(hidden), 7)
        self.assertTrue(all(reason for _, reason in hidden))


if __name__ == "__main__":
    unittest.main()
