"""Stdlib HTTP client for the neutral Spatial Engine REST surface.

Mirrors ``clients/typescript/src/client.ts`` routes without new dependencies:
``GET/PUT /api/maps[/{name}]``, ``POST /api/ingest`` (multipart ``file`` or
``?upload=``), staged ``/api/uploads`` (POST/GET/PUT/DELETE),
``PUT /api/connections/{store}`` and ``GET /api/catalogue?store=``.

The transport is injectable so unit tests run without a host or QGIS.
Long-running chunk loops accept ``is_cancelled`` and clean up staging on
cancel (Q19). Engine error codes (``invalid.arguments``, ``not.found``,
``store.unavailable``) are preserved on ``SpatialEngineError.code`` (Q18).
"""

from __future__ import annotations

import hashlib
import json
import uuid
from dataclasses import dataclass
from typing import Callable
from urllib import parse, request
from urllib.error import HTTPError

CHUNK_SIZE = 8 * 1024 * 1024
RESUMABLE_THRESHOLD_BYTES = 10 * 1024 * 1024
RESUMABLE_THRESHOLD_FEATURES = 50_000


class Cancelled(Exception):
    """Raised when the caller cancels a chunked transfer."""


class SpatialEngineError(Exception):
    """Typed host failure: HTTP status plus the engine ``code`` string."""

    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status = status
        self.code = code


@dataclass
class UploadState:
    upload_id: str
    received: int
    complete: bool
    total_bytes: int | None = None


def _auth_headers(token: str | None) -> dict:
    return {"Authorization": f"Bearer {token}"} if token else {}


def _raise_for_http(status: int, body: bytes) -> None:
    try:
        payload = json.loads(body.decode("utf-8") or "{}")
    except ValueError:
        payload = {}
    code = str(payload.get("code", "unknown"))
    message = str(payload.get("message", payload.get("title", body.decode("utf-8", "replace")[:500])))
    raise SpatialEngineError(status, code, message)


def _default_transport(method: str, url: str, body: bytes | None,
                       headers: dict, timeout: float) -> tuple[int, bytes]:
    req = request.Request(url, data=body, headers=headers, method=method)
    try:
        with request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read()
    except HTTPError as exc:
        return exc.code, exc.read()


Transport = Callable[[str, str, bytes | None, dict, float], tuple[int, bytes]]


class EngineClient:
    """Thin wrapper over the host; pass a fake *transport* in tests."""

    def __init__(self, base_url: str, token: str | None = None,
                 transport: Transport | None = None, timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self._transport = transport or _default_transport
        self.timeout = timeout

    def _call(self, method: str, path: str, body: bytes | None = None,
              headers: dict | None = None) -> tuple[int, bytes]:
        merged = dict(_auth_headers(self.token))
        if headers:
            merged.update(headers)
        return self._transport(method, self.base_url + path, body, merged, self.timeout)

    def _json(self, method: str, path: str, payload: dict | None = None):
        body = json.dumps(payload).encode() if payload is not None else None
        headers = {"Content-Type": "application/json"} if body else None
        status, raw = self._call(method, path, body, headers)
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        return json.loads(raw.decode() or "null")

    def get_map(self, name: str) -> dict | None:
        status, raw = self._call("GET", f"/api/maps/{parse.quote(name)}")
        if status == 404:
            return None
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        return json.loads(raw.decode())

    def put_map(self, map_doc: dict) -> dict:
        return self._json("PUT", f"/api/maps/{parse.quote(map_doc['name'])}", map_doc)

    def list_catalogue(self, store: str) -> list:
        status, raw = self._call("GET", f"/api/catalogue?store={parse.quote(store)}")
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        return json.loads(raw.decode())

    def set_connection(self, store: str, connection_string: str) -> dict:
        return self._json("PUT", f"/api/connections/{parse.quote(store.lower())}",
                          {"connectionString": connection_string})

    def start_upload(self, total_bytes: int | None = None, sha256: str | None = None,
                     upload_id: str | None = None) -> UploadState:
        query = {}
        if upload_id:
            query["id"] = upload_id
        if total_bytes is not None:
            query["total"] = str(total_bytes)
        if sha256:
            query["sha256"] = sha256
        suffix = ("?" + parse.urlencode(query)) if query else ""
        status, raw = self._call("POST", f"/api/uploads{suffix}")
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        payload = json.loads(raw.decode())
        return UploadState(str(payload["uploadId"]), int(payload.get("received", 0)),
                           bool(payload.get("complete", False)), payload.get("totalBytes"))

    def get_upload(self, upload_id: str) -> UploadState:
        status, raw = self._call("GET", f"/api/uploads/{parse.quote(upload_id)}")
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        payload = json.loads(raw.decode())
        return UploadState(str(payload["uploadId"]), int(payload.get("received", 0)),
                           bool(payload.get("complete", False)), payload.get("totalBytes"))

    def append_upload(self, upload_id: str, chunk: bytes, offset: int,
                      total_bytes: int | None = None, sha256: str | None = None) -> UploadState:
        query = {"offset": str(offset)}
        if total_bytes is not None:
            query["total"] = str(total_bytes)
        if sha256:
            query["sha256"] = sha256
        status, raw = self._call(
            "PUT", f"/api/uploads/{parse.quote(upload_id)}?{parse.urlencode(query)}",
            chunk, {"Content-Type": "application/octet-stream"})
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        payload = json.loads(raw.decode())
        return UploadState(str(payload["uploadId"]), int(payload.get("received", 0)),
                           bool(payload.get("complete", False)), payload.get("totalBytes"))

    def delete_upload(self, upload_id: str) -> None:
        status, raw = self._call("DELETE", f"/api/uploads/{parse.quote(upload_id)}")
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)

    def ingest_bytes(self, data: bytes, file_name: str, dataset: str, srid: int,
                     file_format: str = "geojson", store: str = "memory",
                     publish: str | None = None, source_srid: int | None = None) -> dict:
        query = {"dataset": dataset, "srid": str(srid), "format": file_format, "store": store}
        if publish:
            query["publish"] = publish
        if source_srid is not None:
            query["sourceSrid"] = str(source_srid)
        boundary = uuid.uuid4().hex
        prefix = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
                  f"filename=\"{file_name}\"\r\nContent-Type: application/octet-stream\r\n\r\n").encode()
        suffix = f"\r\n--{boundary}--\r\n".encode()
        status, raw = self._call(
            "POST", f"/api/ingest?{parse.urlencode(query)}", prefix + data + suffix,
            {"Content-Type": f"multipart/form-data; boundary={boundary}"})
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        return json.loads(raw.decode())

    def ingest_upload(self, upload_id: str, dataset: str, srid: int,
                      file_format: str = "geojson", store: str = "memory",
                      publish: str | None = None, source_srid: int | None = None) -> dict:
        query = {"dataset": dataset, "srid": str(srid), "format": file_format,
                 "store": store, "upload": upload_id}
        if publish:
            query["publish"] = publish
        if source_srid is not None:
            query["sourceSrid"] = str(source_srid)
        status, raw = self._call("POST", f"/api/ingest?{parse.urlencode(query)}")
        if status < 200 or status >= 300:
            _raise_for_http(status, raw)
        return json.loads(raw.decode())

    def ingest_resumable(self, data: bytes, file_name: str, dataset: str, srid: int,
                         file_format: str = "geojson", store: str = "memory",
                         publish: str | None = None, source_srid: int | None = None,
                         chunk_size: int = CHUNK_SIZE,
                         is_cancelled: Callable[[], bool] | None = None) -> dict:
        """Upload in chunks when large, else single-shot; cleans staging on cancel."""
        use_staged = len(data) >= RESUMABLE_THRESHOLD_BYTES
        if not use_staged:
            return self.ingest_bytes(data, file_name, dataset, srid, file_format,
                                     store, publish, source_srid)
        digest = hashlib.sha256(data).hexdigest()
        upload_id: str | None = None
        try:
            started = self.start_upload(total_bytes=len(data), sha256=digest)
            upload_id = started.upload_id
            offset = self.get_upload(upload_id).received
            while offset < len(data):
                if is_cancelled and is_cancelled():
                    raise Cancelled("upload cancelled")
                chunk = data[offset:offset + chunk_size]
                state = self.append_upload(upload_id, chunk, offset, len(data), digest)
                offset = state.received
            return self.ingest_upload(upload_id, dataset, srid, file_format,
                                      store, publish, source_srid)
        except Exception:
            if upload_id is not None:
                try:
                    self.delete_upload(upload_id)
                except Exception:
                    pass
            raise
