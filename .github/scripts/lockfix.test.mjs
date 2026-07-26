// Self-tests for the Dependabot lock-file classifier (#203). Run with
// `node --test .github/scripts/lockfix.test.mjs`.
//
// The load-bearing cases are the ones that MUST refuse to commit: any non-lock
// path in the diff, a NUL-delimited path with a space, and a rename that drags
// in a foreign source. Each asserts the fail-closed verdict, not just absence.

import test from "node:test";
import assert from "node:assert/strict";
import { changedPaths, classify, LOCK_FILES } from "./lockfix.mjs";

// Build a `git status --porcelain -z` stream: each record is `XY <path>` and
// the stream is NUL-terminated per record.
const z = (...records) => records.map((r) => r + "\0").join("");

test("the allowlist is exactly the 7 solution lock files", () => {
  assert.equal(LOCK_FILES.length, 7);
  assert.ok(LOCK_FILES.every((p) => p.endsWith("/packages.lock.json")));
});

test("a diff of only lock files -> commit", () => {
  const r = classify(
    z(" M src/Cluckwork.Domain/packages.lock.json",
      " M tests/Cluckwork.Api.IntegrationTests/packages.lock.json"),
  );
  assert.equal(r.action, "commit");
  assert.equal(r.locks.length, 2);
  assert.deepEqual(r.foreign, []);
});

test("no changes -> noop", () => {
  assert.equal(classify("").action, "noop");
});

test("a foreign path mixed in -> abort (fail closed)", () => {
  const r = classify(
    z(" M src/Cluckwork.Api/packages.lock.json",
      " M src/Cluckwork.Api/Cluckwork.Api.csproj"),
  );
  assert.equal(r.action, "abort");
  assert.deepEqual(r.foreign, ["src/Cluckwork.Api/Cluckwork.Api.csproj"]);
});

test("a planted nuget.config -> abort", () => {
  assert.equal(classify(z(" M nuget.config")).action, "abort");
});

test("NUL-delimited: a path containing a space is ONE field", () => {
  // porcelain -z does not quote; a naive whitespace split would mis-read this.
  const r = classify(z(" M weird dir/packages.lock.json"));
  assert.equal(r.action, "abort");
  assert.deepEqual(r.foreign, ["weird dir/packages.lock.json"]);
});

test("a rename that drags in a non-lock SOURCE is caught", () => {
  // Rename record: `R  <new>\0<old>\0` — both paths must be inspected.
  const r = classify("R  src/Cluckwork.Api/packages.lock.json\0evil/secrets.txt\0");
  assert.equal(r.action, "abort");
  assert.ok(r.foreign.includes("evil/secrets.txt"));
});

test("changedPaths returns both target and source of a rename", () => {
  assert.deepEqual(
    changedPaths("R  a/packages.lock.json\0b/old.json\0"),
    ["a/packages.lock.json", "b/old.json"],
  );
});

test("a copy that drags in a non-lock SOURCE is caught", () => {
  // Copy record: `C  <new>\0<old>\0` — same shape as rename, both paths must
  // be inspected.
  const r = classify("C  src/Cluckwork.Api/packages.lock.json\0evil/copied.txt\0");
  assert.equal(r.action, "abort");
  assert.ok(r.foreign.includes("evil/copied.txt"));
});
