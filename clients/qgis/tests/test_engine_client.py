"""Engine-client wiring: routes, typed errors, resume, cancel cleanup."""

import json
import unittest

from clients.qgis.engine_client import Cancelled, EngineClient, SpatialEngineError


def _resp(status, payload):
    return status, json.dumps(payload).encode()


class EngineClientTests(unittest.TestCase):
    def test_put_map_hits_encoded_route_with_bearer(self):
        calls = []

        def transport(method, url, body, headers, timeout):
            calls.append((method, url, headers))
            return _resp(200, {"name": "m", "store": "memory", "layers": [], "services": []})

        EngineClient("http://host", "tok", transport).put_map(
            {"name": "my map", "store": "memory", "layers": [], "services": []})
        method, url, headers = calls[0]
        self.assertEqual(method, "PUT")
        self.assertIn("/api/maps/my%20map", url)
        self.assertEqual(headers.get("Authorization"), "Bearer tok")

    def test_get_map_404_is_none(self):
        client = EngineClient("http://host", None,
                              lambda *a: (404, b'{"code":"not.found"}'))
        self.assertIsNone(client.get_map("missing"))

    def test_error_code_preserved(self):
        def transport(*a):
            return _resp(400, {"code": "invalid.arguments", "message": "bad srid"})

        with self.assertRaises(SpatialEngineError) as ctx:
            EngineClient("http://h", "t", transport).put_map(
                {"name": "m", "store": "memory", "layers": [], "services": []})
        self.assertEqual(ctx.exception.code, "invalid.arguments")

    def test_store_unavailable_preserved(self):
        def transport(*a):
            return _resp(503, {"code": "store.unavailable", "message": "no conn"})

        with self.assertRaises(SpatialEngineError) as ctx:
            EngineClient("http://h", "t", transport).list_catalogue("postgis")
        self.assertEqual(ctx.exception.code, "store.unavailable")

    def test_ingest_uses_upload_param_for_staged_bytes(self):
        seen = {}

        def transport(method, url, body, headers, timeout):
            seen["url"] = url
            return _resp(200, {"dataset": "public.a", "features": 1, "srid": 4326})

        EngineClient("http://h", "t", transport).ingest_upload("u1", "public.a", 4326)
        self.assertIn("upload=u1", seen["url"])
        self.assertIn("/api/ingest?", seen["url"])

    def test_resumable_resumes_from_reported_offset(self):
        state = {"received": 5 * 1024 * 1024}

        def transport(method, url, body, headers, timeout):
            if method == "POST" and "/api/uploads" in url:
                return _resp(200, {"uploadId": "u", "received": 0, "complete": False})
            if method == "GET":
                return _resp(200, {"uploadId": "u", "received": state["received"], "complete": False})
            self.fail(f"unexpected {method} {url}")

        client = EngineClient("http://h", "t", transport)
        started = client.start_upload(total_bytes=11 * 1024 * 1024, sha256="d")
        self.assertEqual(started.upload_id, "u")
        # the driver resumes from what the host reports, not from zero
        self.assertEqual(client.get_upload("u").received, state["received"])

    def test_cancel_deletes_staging(self):
        deleted = []

        def transport(method, url, body, headers, timeout):
            if method == "POST" and "/api/uploads?" in url:
                return _resp(200, {"uploadId": "u", "received": 0, "complete": False})
            if method == "GET":
                return _resp(200, {"uploadId": "u", "received": 0, "complete": False})
            if method == "DELETE":
                deleted.append(url)
                return 200, b"true"
            self.fail(f"unexpected {method} {url}")

        client = EngineClient("http://h", "t", transport)
        big = b"y" * (11 * 1024 * 1024)
        with self.assertRaises(Cancelled):
            client.ingest_resumable(big, "f.geojson", "d", 4326, is_cancelled=lambda: True)
        self.assertTrue(any(u.endswith("/api/uploads/u") for u in deleted))

    def test_small_ingest_is_single_shot(self):
        posts = []

        def transport(method, url, body, headers, timeout):
            posts.append((method, url))
            return _resp(200, {"dataset": "d", "features": 1, "srid": 4326})

        EngineClient("http://h", "t", transport).ingest_resumable(
            b'{"type":"FeatureCollection","features":[]}', "f.geojson", "d", 4326)
        self.assertEqual(len(posts), 1)
        self.assertIn("/api/ingest?", posts[0][1])

    def test_set_connection_puts_secret_in_body_not_url(self):
        seen = {}

        def transport(method, url, body, headers, timeout):
            seen.update(method=method, url=url, body=json.loads(body.decode()))
            return _resp(200, {"store": "postgis", "configured": True})

        EngineClient("http://h", "t", transport).set_connection("postgis", "Host=x;")
        self.assertEqual(seen["method"], "PUT")
        self.assertTrue(seen["url"].endswith("/api/connections/postgis"))
        self.assertEqual(seen["body"]["connectionString"], "Host=x;")


if __name__ == "__main__":
    unittest.main()
