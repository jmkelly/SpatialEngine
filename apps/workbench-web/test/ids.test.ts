import { test } from "node:test";
import assert from "node:assert/strict";
import { newId, type IdCrypto } from "../src/ids.ts";

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** `crypto.getRandomValues` exists in every context, even where `randomUUID` does not. */
const getRandomValues = crypto.getRandomValues.bind(crypto);

test("newId uses the native randomUUID when the context exposes it", () => {
  const source: IdCrypto = { randomUUID: () => "native-id", getRandomValues };
  assert.equal(newId(source), "native-id");
});

test("newId builds an RFC 4122 v4 id when randomUUID is missing (insecure context)", () => {
  const source: IdCrypto = { getRandomValues };
  assert.match(newId(source), UUID_V4);
});

test("newId returns distinct ids without randomUUID", () => {
  const source: IdCrypto = { getRandomValues };
  assert.notEqual(newId(source), newId(source));
});
