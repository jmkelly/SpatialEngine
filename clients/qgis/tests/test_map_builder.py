"""Map-document append rules: no wipe, no dup, sentinel id, service union."""

import unittest

from clients.qgis.map_builder import VECTOR_SERVICES, append_layer, build_map


class MapBuilderTests(unittest.TestCase):
    def test_build_map_defaults_to_five_vector_services(self):
        self.assertEqual(build_map("m", "memory")["services"], VECTOR_SERVICES)
        self.assertNotIn("ImageServer", build_map("m", "memory")["services"])

    def test_append_preserves_existing_layers_and_styles(self):
        existing = {"name": "m", "store": "memory",
                    "layers": [{"dataset": "public.a", "layerId": 0, "style": "[x]"}],
                    "services": ["FeatureServer"]}
        out = append_layer(existing, "m", "public.b", None, "memory")
        self.assertEqual(len(out["layers"]), 2)
        self.assertEqual(out["layers"][0]["style"], "[x]")
        self.assertEqual(out["layers"][1]["layerId"], -1)
        # input untouched
        self.assertEqual(len(existing["layers"]), 1)

    def test_append_skips_duplicate_dataset(self):
        existing = {"name": "m", "store": "postgis",
                    "layers": [{"dataset": "public.a", "layerId": 0, "style": "[keep]"}],
                    "services": list(VECTOR_SERVICES)}
        out = append_layer(existing, "m", "public.a", "[new]", "postgis")
        self.assertEqual(len(out["layers"]), 1)
        self.assertEqual(out["layers"][0]["style"], "[keep]")

    def test_append_unions_vector_services(self):
        out = append_layer(None, "m", "public.a", None, "memory")
        self.assertEqual(out["services"], VECTOR_SERVICES)


if __name__ == "__main__":
    unittest.main()
