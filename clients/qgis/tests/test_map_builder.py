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
                    "services": ["feature"]}
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

    def test_services_use_host_wire_values(self):
        # IMapRegistry pins the JSON wire keys to feature/map/tiles/wms/wfs;
        # member names (FeatureServer, …) are rejected with a 400.
        self.assertEqual(VECTOR_SERVICES, ["feature", "map", "tiles", "wms", "wfs"])
        self.assertEqual(build_map("m", "memory")["services"], VECTOR_SERVICES)

    def test_append_sets_store_override_for_foreign_store(self):
        existing = {"name": "m", "store": "memory", "layers": [], "services": []}
        out = append_layer(existing, "m", "public.a", None, "postgis")
        self.assertEqual(out["layers"][0]["store"], "postgis")
        same = append_layer(existing, "m", "public.b", None, "memory")
        self.assertIsNone(same["layers"][0]["store"])

    def test_append_dedupes_host_round_tripped_layers(self):
        # Host docs carry "store": null on inheriting layers; the effective
        # store (layer ?? map) must drive duplicate detection.
        existing = {"name": "m", "store": "memory",
                    "layers": [{"dataset": "public.a", "layerId": 0, "style": "[keep]",
                                "store": None}],
                    "services": list(VECTOR_SERVICES)}
        out = append_layer(existing, "m", "public.a", "[new]", "memory")
        self.assertEqual(len(out["layers"]), 1)
        self.assertEqual(out["layers"][0]["style"], "[keep]")


if __name__ == "__main__":
    unittest.main()
