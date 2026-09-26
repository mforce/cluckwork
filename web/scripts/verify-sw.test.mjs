// web/scripts/verify-sw.test.mjs — #825.
//
// Behavior-level: runs the real script as a subprocess against a minimal but
// otherwise-valid generated service worker, the same way CI runs it against
// the real one. Proves the precache-ceiling guard added in verify-sw.mjs both
// passes under budget and fails closed over it, with the mutant's own failure
// message readable in the assertion. Not a refactor into pure functions:
// verify-sw.mjs's own header states its whole purpose is checking the REAL
// emitted worker, so a fixture-driven subprocess run is the faithful test.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { matchesInterExtendedSubsetFont } from "./font-cache-match.mjs";

const scriptPath = fileURLToPath(new URL("./verify-sw.mjs", import.meta.url));

// Real Workbox-emitted patterns (copied from an actual `vite build` output),
// so this fixture exercises the same regex logic as the guards above it, not
// a simplified stand-in.
const DENYLIST = "[/^\\/api(?:[/?]|$)/i,/^\\/health(?:[/?]|$)/i]";
// #948 round 2 — copied from an actual `vite build` output (esbuild expands
// shorthand destructuring into `key:renamedLocal` while minifying, so the
// PROPERTY names `url:`/`request:`/`sameOrigin:` survive verbatim even
// though the local bindings `s`/`e`/`l` do not; check 2 keys on exactly that).
const RUNTIME_ROUTE_FN =
  'function({url:s,request:e,sameOrigin:l}){return l&&s.pathname.startsWith("/assets/")' +
  '&&s.pathname.endsWith(".woff2")&&"font"===e.destination}';
// #948 round 1's shape, kept only as the negative fixture below: a RegExp is
// tested against the request's full href, unanchored, so this one also
// matched `/api/v1/x/inter-greek-opsz-normal-deadbeef.woff2`.
const RUNTIME_ROUTE_REGEX_ROUND_1 =
  "/\\/inter-(cyrillic-ext|cyrillic|greek-ext|greek|vietnamese)-opsz-normal-[^/]+\\.woff2$/";

// #835 added check 5: the Inter latin/latin-ext opsz faces must be precached.
// Zero-byte stubs so they satisfy that check without moving the byte totals
// this suite asserts on — `assetByteSize` stays the only KiB in play.
const OPSZ_FONTS = ["inter-latin-opsz-normal.woff2", "inter-latin-ext-opsz-normal.woff2"];

/** Builds a minimal dist/ that satisfies every OTHER verify-sw.mjs check, so a test only exercises the precache-size guard. */
function buildFixture(assetByteSize, runtimeRoute = RUNTIME_ROUTE_FN) {
  const dist = mkdtempSync(join(tmpdir(), "verify-sw-fixture-"));
  mkdirSync(join(dist, "assets", "fonts"), { recursive: true });
  writeFileSync(join(dist, "assets", "app.js"), "a".repeat(assetByteSize));
  for (const font of OPSZ_FONTS) writeFileSync(join(dist, "assets", "fonts", font), "");
  const entries = [
    { url: "assets/app.js", revision: "deadbeef" },
    ...OPSZ_FONTS.map((font) => ({ url: `assets/fonts/${font}`, revision: "deadbeef" })),
  ].map(({ url, revision }) => `{url:"${url}",revision:"${revision}"}`).join(",");
  const sw = `precacheAndRoute([${entries}]);` +
    `registerRoute(new wb.NavigationRoute(wb.createHandlerBoundToURL("/index.html"),{denylist:${DENYLIST}}));` +
    `registerRoute(${runtimeRoute},new wb.CacheFirst({cacheName:"inter-extended-subsets"}),"GET");`;
  writeFileSync(join(dist, "sw.js"), sw);
  return dist;
}

// execFileSync's default stdio inherits the child's stderr straight to this
// process's real stderr (in addition to capturing it), so a fixture's
// expected `::error::` line would otherwise reach the CI job log and get
// read as a GitHub annotation on every run, passing or not. Piping instead
// of inheriting keeps the capture this file asserts on without leaking it.
function runVerifySw(dist) {
  try {
    const stdout = execFileSync("node", [scriptPath, dist], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
    return { status: 0, stdout, stderr: "" };
  } catch (err) {
    return { status: err.status, stdout: err.stdout ?? "", stderr: err.stderr ?? "" };
  }
}

test("passes and reports headroom when precache is under the ceiling", () => {
  const dist = buildFixture(1024); // 1 KiB, far under the ceiling
  try {
    const { status, stdout } = runVerifySw(dist);
    assert.equal(status, 0);
    assert.match(stdout, /\[precache budget] 1\.00 KiB \/ \d+ KiB ceiling/);
  } finally {
    rmSync(dist, { recursive: true, force: true });
  }
});

test("fails closed with an actionable message when precache exceeds the ceiling", () => {
  // 2,000 KiB is over every ceiling this repo would plausibly set; the test
  // asserts the guard fires and names both figures, not a specific number.
  const dist = buildFixture(2000 * 1024);
  try {
    const { status, stderr } = runVerifySw(dist);
    assert.equal(status, 1);
    assert.match(stderr, /precache is 2000\.00 KiB, over the \d+ KiB ceiling \(#825\)/);
    assert.match(stderr, /does not make this check pass/);
  } finally {
    rmSync(dist, { recursive: true, force: true });
  }
});

// #948 review round 2, P2 — fixture cases proving check 2's new function-vs-
// RegExp rule, against the REAL script (not a reimplementation of it).
test("rejects a RegExp runtime-caching route (#948 round 1 regression)", () => {
  const dist = buildFixture(1024, RUNTIME_ROUTE_REGEX_ROUND_1);
  try {
    const { status, stderr } = runVerifySw(dist);
    assert.equal(status, 1);
    assert.match(stderr, /runtime caching route is a RegExp, not a match-callback function/);
  } finally {
    rmSync(dist, { recursive: true, force: true });
  }
});

test("accepts the real match-callback function route", () => {
  const dist = buildFixture(1024, RUNTIME_ROUTE_FN);
  try {
    const { status } = runVerifySw(dist);
    assert.equal(status, 0);
  } finally {
    rmSync(dist, { recursive: true, force: true });
  }
});

// #948 review round 2, P2 — the match-callback itself, called directly (a
// real function import, never eval'd text), against the exact case the
// review named: a font-suffixed API path must be REJECTED, and the real
// asset path accepted.
test("matchesInterExtendedSubsetFont", async (t) => {
  const font = { destination: "font" };
  const notFont = { destination: "" };

  await t.test("rejects a font-suffixed API path (#948 round 1's gap)", () => {
    const url = new URL("https://cluckwork.example/api/v1/x/inter-greek-opsz-normal-deadbeef.woff2");
    assert.equal(matchesInterExtendedSubsetFont({ url, request: font, sameOrigin: true }), false);
  });

  await t.test("accepts the real asset path", () => {
    const url = new URL("https://cluckwork.example/assets/inter-greek-opsz-normal-deadbeef.woff2");
    assert.equal(matchesInterExtendedSubsetFont({ url, request: font, sameOrigin: true }), true);
  });

  await t.test("rejects a cross-origin request", () => {
    const url = new URL("https://evil.example/assets/inter-greek-opsz-normal-deadbeef.woff2");
    assert.equal(matchesInterExtendedSubsetFont({ url, request: font, sameOrigin: false }), false);
  });

  await t.test("rejects a non-font destination", () => {
    const url = new URL("https://cluckwork.example/assets/inter-greek-opsz-normal-deadbeef.woff2");
    assert.equal(matchesInterExtendedSubsetFont({ url, request: notFont, sameOrigin: true }), false);
  });
});
