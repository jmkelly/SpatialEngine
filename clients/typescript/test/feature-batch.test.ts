import { test } from "node:test";
import assert from "node:assert/strict";
import { decodeFeatureBatch, FeatureBatchFormatError } from "../src/feature-batch.ts";
import { fromBase64 } from "../src/wire.ts";

// A real SFBAT v1 vector produced by the .NET FeatureBatchCodec (ADR-0020) —
// the cross-language pin: if the TS decoder and the .NET codec disagree, this
// test fails. Schema: name(String), count(Int64, nullable), ratio(Double),
// flag(Boolean), geom(Geometry, nullable), when(DateTimeOffset, nullable),
// id(Guid, nullable); two features, the second carrying nulls.
const VECTOR =
  "U0ZCQVQBBwAAAAQAAABuYW1lBAAABQAAAGNvdW50AgEABQAAAHJhdGlvAwAABAAAAGZsYWcBAAAEAAAAZ2VvbQUBAAQAAAB3aGVuBgEAAgAAAGlkBwEAAgAAAAkAAABmZWF0dXJlLTEABQAAAGFsaWNlACoAAAAAAAAAAAAAAAAAAARAAAEAGgAAAFNHRU9NAQABAAEAAAAAAAD4PwAAAAAAAATAAACIVaoUCN8IeAAAMyIRAFVEd2aImaq7zN3u/wkAAABmZWF0dXJlLTIAAwAAAGJvYgEAAAAAAAAA0L8AAAEBAQ==";

test("decodes the .NET-produced canonical vector exactly", () => {
  const batch = decodeFeatureBatch(fromBase64(VECTOR));

  assert.equal(batch.schema.fields.length, 7);
  assert.deepEqual(
    batch.schema.fields.map((field) => field.name),
    ["name", "count", "ratio", "flag", "geom", "when", "id"],
  );
  assert.equal(batch.schema.fields[1]!.kind, "Int64");
  assert.equal(batch.schema.fields[1]!.nullable, true);
  assert.equal(batch.schema.fields[3]!.kind, "Boolean");

  assert.equal(batch.features.length, 2);

  const first = batch.features[0]!;
  assert.equal(first.id, "feature-1");
  const name = first.attributes[0]!;
  assert.equal(name.kind, "String");
  assert.equal(name.kind === "String" && name.value, "alice");
  const count = first.attributes[1]!;
  assert.equal(count.kind, "Int64");
  assert.equal(count.kind === "Int64" && count.value, 42n);
  const ratio = first.attributes[2]!;
  assert.equal(ratio.kind, "Double");
  assert.equal(ratio.kind === "Double" && ratio.value, 2.5);
  const flag = first.attributes[3]!;
  assert.equal(flag.kind, "Boolean");
  assert.equal(flag.kind === "Boolean" && flag.value, true);

  const geometry = first.attributes[4]!;
  assert.equal(geometry.kind, "Geometry");
  assert.equal(geometry.kind === "Geometry" && geometry.value.length, 26);

  const when = first.attributes[5]!;
  assert.equal(when.kind, "DateTimeOffset");
  if (when.kind === "DateTimeOffset") {
    assert.equal(when.value.utcTicks, 639238556960000000n);
    assert.equal(when.value.offsetMinutes, 120);
  }

  const id = first.attributes[6]!;
  assert.equal(id.kind, "Guid");
  assert.equal(id.kind === "Guid" && id.value, "00112233-4455-6677-8899-aabbccddeeff");

  const second = batch.features[1]!;
  assert.equal(second.id, "feature-2");
  const secondName = second.attributes[0]!;
  assert.equal(secondName.kind, "String");
  assert.equal(secondName.kind === "String" && secondName.value, "bob");
  assert.equal(second.attributes[1]!.kind, "Null");
  const secondRatio = second.attributes[2]!;
  assert.equal(secondRatio.kind, "Double");
  assert.equal(secondRatio.kind === "Double" && secondRatio.value, -0.25);
  const secondFlag = second.attributes[3]!;
  assert.equal(secondFlag.kind, "Boolean");
  assert.equal(secondFlag.kind === "Boolean" && secondFlag.value, false);
  assert.equal(second.attributes[4]!.kind, "Null");
  assert.equal(second.attributes[5]!.kind, "Null");
  assert.equal(second.attributes[6]!.kind, "Null");
});

test("rejects malformed input with byte-accurate errors", () => {
  assert.throws(() => decodeFeatureBatch(new Uint8Array([1, 2, 3])), FeatureBatchFormatError);
  const wrongMagic = fromBase64(VECTOR);
  wrongMagic[0] = 0;
  assert.throws(() => decodeFeatureBatch(wrongMagic), /magic/);
  const truncated = fromBase64(VECTOR).subarray(0, 60);
  assert.throws(() => decodeFeatureBatch(truncated), /truncated/);
});