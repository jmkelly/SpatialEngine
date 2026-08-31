import { test } from "node:test";
import assert from "node:assert/strict";
import { decode, encode, fromBase64, toBase64 } from "../src/wire.ts";

test("encode passes scalars through", () => {
  assert.equal(encode(null), null);
  assert.equal(encode(true), true);
  assert.equal(encode("hello"), "hello");
  assert.equal(encode(2.5), 2.5);
});

test("encode tags bigint as $i64", () => {
  assert.deepEqual(encode(9007199254740993n), { $i64: "9007199254740993" });
  assert.deepEqual(encode(-42n), { $i64: "-42" });
});

test("encode tags bytes as $bytes and round trips", () => {
  const bytes = new Uint8Array([1, 2, 3, 250]);
  const encoded = encode(bytes);
  assert.deepEqual(encoded, { $bytes: "AQID+g==" });
  const decoded = decode(encoded);
  assert.deepEqual(decoded, bytes);
});

test("decode restores $i64 as bigint, scalars as themselves", () => {
  assert.equal(decode({ $i64: "80" }), 80n);
  assert.equal(decode("x"), "x");
  assert.equal(decode(1.5), 1.5);
  assert.equal(decode(null), null);
  assert.deepEqual(decode({ $geometry: "abc" }), { $geometry: "abc" });
});

test("encode rejects arrays, plain objects and non-finite numbers", () => {
  assert.throws(() => encode([1, 2]));
  assert.throws(() => encode({ a: 1 }));
  assert.throws(() => encode(Number.NaN));
  assert.throws(() => encode(Number.POSITIVE_INFINITY));
});

test("base64 helpers round trip binary", () => {
  const bytes = new Uint8Array(70_000).map((_, i) => i % 251);
  assert.deepEqual(fromBase64(toBase64(bytes)), bytes);
});