"""QML subset conversion plus default-style fallback."""

import json
import unittest

from clients.qgis.style_convert import qml_to_fragments

SINGLE = ('<qgis><renderer-v2 type="singleSymbol"><symbol><layer>'
          '<Option k="color" v="255,0,0,255"/><Option k="width" v="2"/>'
          '</layer></symbol></renderer-v2></qgis>')

CATEGORIZED = ('<qgis><renderer-v2 type="categorizedSymbol" attr="kind">'
               '<category value="a"><symbol><layer>'
               '<Option k="color" v="0,255,0,255"/></layer></symbol></category>'
               '<category value="b"><symbol><layer>'
               '<Option k="color" v="0,0,255,255"/></layer></symbol></category>'
               '</renderer-v2></qgis>')

RULE = ('<qgis><renderer-v2 type="RuleRenderer">'
        '<rules><rule/></rules></renderer-v2></qgis>')


class StyleConvertTests(unittest.TestCase):
    def test_single_symbol_point(self):
        style, warning = qml_to_fragments(SINGLE, "point")
        self.assertEqual(warning, "")
        frags = json.loads(style)
        self.assertEqual(frags[0]["type"], "circle")
        self.assertEqual(frags[0]["paint"]["circle-color"], "#ff0000")
        self.assertNotIn("id", frags[0])
        self.assertNotIn("source-layer", frags[0])

    def test_categorized_carries_filters(self):
        style, warning = qml_to_fragments(CATEGORIZED, "polygon")
        self.assertEqual(warning, "")
        frags = json.loads(style)
        self.assertTrue(all("filter" in f for f in frags))

    def test_rule_renderer_falls_back_to_default(self):
        style, warning = qml_to_fragments(RULE, "polygon")
        self.assertIsNone(style)
        self.assertTrue(warning)

    def test_unparseable_qml_is_invalid_arguments(self):
        with self.assertRaises(ValueError) as ctx:
            qml_to_fragments("<qgis><oops>", "polygon")
        self.assertIn("invalid.arguments", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
