#!/usr/bin/env node
// #369 — enumerate the THIRD-PARTY container images this repo pins, so CI can
// scan every one of them and fail on a pin that is not immutable.
//
// Why this exists at all: `deploy/docker-compose.yml` pinned Postgres by digest
// and Traefik by `v3.5` — a mutable tag on a release branch upstream had stopped
// updating. Nothing noticed for eight months, because nothing scanned it: the
// Trivy step in ci.yml passes `image-ref: cluckwork-api:ci`, which is the
// application image only. The deploy repo then read its production Traefik
// version out of that file, so a stale reference pin became a production digest
// carrying 57 fixable HIGH/CRITICAL findings.
//
// ================== WHY IT READS RESOLVED JSON, NOT YAML ==================
//
// Input is the output of `docker compose config --format json`, never the raw
// compose file. Hand-parsing YAML here would be the same class of mistake as
// grepping a compose file for an environment key (tools/simulation/verify-harness.sh
// documents that one at length): it asserts a proxy for the thing rather than
// the thing. Compose is the authority on what a service resolves to, including
// `extends`, merged override files, and interpolation.
//
// The caller MUST render that JSON with `--profile '*'`. `traefik` sits behind
// `profiles: ["prod"]`, and without it Compose omits the service ENTIRELY — the
// script then sees a well-formed document with the stale pin simply absent and
// exits 0. That is not theoretical: rendering deploy/docker-compose.yml without
// the flag prints Postgres alone and succeeds, with Traefik unscanned. It is the
// same false green the whole issue is about, one layer up.
//
// `assertNonEmpty` cannot catch it — the set is not empty, just short. So each
// input bundles the render with the source's DECLARED PROFILE LIST, and
// `assertProfilesFullyRendered` requires every declared profile to be
// represented by at least one rendered service. Omit the flag and `prod` is
// declared while no rendered service carries it, so the render is refused.
//
// That check is exact, and deliberately replaces an earlier one that compared
// `image:` COUNTS between the source text and the render (PR #379 review). Two
// defects, one fatal: a count cannot distinguish "one service dropped" from
// "one dropped and one gained", and the line regex that produced the source
// count missed an inline mapping (`db: {image: postgres:…}`), so an undercount
// on one side could CANCEL a dropped service on the other and the guard would
// pass. The lesson is the same one this file already states about YAML: do not
// hand-parse a format when the tool that owns it will answer the question.
// `docker compose config --profiles` reports every profile declared in a file
// regardless of which are enabled, so both sides now come from compose.
//
// Input is one JSON bundle per compose file:
//   { "source": "deploy/docker-compose.yml", "declaredProfiles": ["prod"],
//     "config": <docker compose config --format json output> }
//
// Usage:
//   node .github/scripts/compose-images.mjs [--format list|matrix] <bundle.json>...
//
// Exit 0 with the images on stdout, or exit 1 with the reason on stderr.

// A ref must carry BOTH a human-readable tag and an immutable digest:
//   traefik:v3.7.10@sha256:617c...
// The digest is what makes the pull reproducible and what ties the running
// bytes to the bytes CI scanned. The tag is what makes the file reviewable —
// `@sha256:617c...` alone tells a reader nothing about how stale it is, which
// is precisely the failure mode this whole change is about. Requiring both is
// how `db` was already written, so this codifies the existing good pin rather
// than inventing a new convention.
const PINNED_REF = /^(?<name>[^\s:@]+(?::\d+)?(?:\/[^\s:@]+)*):(?<tag>[\w][\w.-]*)@sha256:(?<digest>[0-9a-f]{64})$/;

/**
 * Third-party images from one or more resolved compose documents.
 *
 * A service with a `build:` section is FIRST-PARTY — its bytes come from this
 * repo's Dockerfile, and ci.yml already builds and scans them. Compose lets a
 * service carry `image:` alongside `build:` (it names the build output), so the
 * discriminator is the presence of `build`, never the presence of `image`.
 */
export function collectThirdPartyImages(documents) {
  const bySource = new Map();

  for (const { path, config } of documents) {
    const services = config?.services;
    if (services === undefined || services === null) {
      throw new Error(`${path}: resolved compose document has no "services" key`);
    }
    if (typeof services !== 'object' || Array.isArray(services)) {
      throw new Error(`${path}: "services" is not an object`);
    }

    for (const [service, spec] of Object.entries(services)) {
      // A service name travels into a GitHub Actions matrix and out into a log
      // line, and on a fork pull_request the compose file is the contributor's
      // content. Compose's own grammar for a service name is [A-Za-z0-9._-]+,
      // so this rejects nothing legitimate — it just refuses to be the thing
      // that carries a shell metacharacter downstream.
      if (!/^[A-Za-z0-9._-]+$/.test(service)) {
        throw new Error(
          `${path}: service name ${JSON.stringify(service)} is outside compose's ` +
            'own [A-Za-z0-9._-]+ grammar — refusing to pass it downstream',
        );
      }
      if (spec === null || typeof spec !== 'object') {
        throw new Error(`${path}: service "${service}" is not an object`);
      }
      if (spec.build) continue; // built here, scanned by ci.yml's image job
      const image = spec.image;
      if (image === undefined || image === null || String(image).trim() === '') {
        throw new Error(
          `${path}: service "${service}" has neither "build" nor "image" — ` +
            'cannot tell whether it needs scanning',
        );
      }
      const ref = String(image).trim();
      // Dedupe across files by ref: deploy/docker-compose.yml and
      // docker-compose.dev.yml pin the same Postgres digest, and scanning
      // identical bytes twice is pure runner time.
      if (!bySource.has(ref)) bySource.set(ref, []);
      bySource.get(ref).push(`${path}:${service}`);
    }
  }

  return [...bySource.entries()]
    .map(([ref, usedBy]) => ({ ref, usedBy }))
    .sort((a, b) => a.ref.localeCompare(b.ref));
}

/** Every ref must be tag + immutable digest. Reports ALL offenders, not the first. */
export function assertAllPinnedByDigest(images) {
  const bad = images.filter((i) => !PINNED_REF.test(i.ref));
  if (bad.length === 0) return;
  const lines = bad.map(
    (i) => `  ${i.ref}  (${i.usedBy.join(', ')})`,
  );
  throw new Error(
    'third-party image(s) are not pinned to an immutable digest:\n' +
      `${lines.join('\n')}\n` +
      'Pin as name:tag@sha256:<64 hex>, e.g.\n' +
      '  image: traefik:v3.7.10@sha256:617c253c5d960e257ec153d582cc62f191461baba6863bd6b498ae062d3d5c19\n' +
      'Resolve the digest with: docker buildx imagetools inspect <name>:<tag>',
  );
}

/**
 * Refuse to report "all clear" over an empty set.
 *
 * A scan job that iterates zero images is GREEN, and that green is the most
 * dangerous output this script can produce: it reads as "every pin is clean"
 * when nothing was examined. Reachable by a compose file that stops resolving,
 * a rendered document missing its services, or every service gaining a `build:`.
 */
export function assertNonEmpty(images) {
  if (images.length === 0) {
    throw new Error(
      'no third-party images found in the resolved compose document(s). ' +
        'Either the render is wrong (did the caller pass --profile \'*\'?) or ' +
        'this check is now scanning nothing while reporting success — fix it, ' +
        'do not delete it.',
    );
  }
}

/**
 * Every profile the source DECLARES must be represented in the render.
 *
 * `docker compose config --profiles` lists the profiles a file declares
 * regardless of which are enabled, and the resolved document records each
 * service's own `profiles`. So a profile that is declared but carried by no
 * rendered service means services behind it were filtered out — which is
 * exactly the "render silently dropped `traefik`" failure, detected without
 * reading a byte of YAML ourselves.
 *
 * Sound in both directions that matter: a service listing several profiles is
 * included when ANY of them is enabled, and it still reports all of them, so
 * enabling one does not falsely fail the others.
 */
export function assertProfilesFullyRendered(bundles) {
  const problems = [];

  for (const { source, declaredProfiles, config } of bundles) {
    const rendered = new Set();
    for (const spec of Object.values(config?.services ?? {})) {
      for (const p of (spec && typeof spec === 'object' && spec.profiles) || []) {
        rendered.add(String(p));
      }
    }
    const missing = declaredProfiles.filter((p) => !rendered.has(p));
    if (missing.length > 0) {
      problems.push(
        `${source}: declares profile(s) ${missing.map((p) => `"${p}"`).join(', ')} that no ` +
          'rendered service carries, so services behind them were filtered out of the render',
      );
    }
  }

  if (problems.length === 0) return;
  throw new Error(
    `${problems.join('\n  ')}\n` +
      "Render with --profile '*'. A profile-gated service is omitted SILENTLY, and its " +
      'pin would go unscanned while this check reported success.',
  );
}

export function formatMatrix(images) {
  // One matrix entry per image. `name` is what shows in the Actions UI, so it
  // has to identify the image without the 71-character digest.
  return JSON.stringify(
    images.map((i) => ({
      ref: i.ref,
      name: i.ref.replace(/@sha256:[0-9a-f]{64}$/, ''),
      usedBy: i.usedBy.join(', '),
    })),
  );
}

/**
 * Validate one bundle's shape before trusting anything in it.
 *
 * Strict, because every field is load-bearing: `source` labels findings,
 * `declaredProfiles` is half of the completeness check, and `config` is the
 * scan set. A missing or wrong-typed field must refuse, never default — a
 * `declaredProfiles` that quietly became `[]` turns the profile check into a
 * no-op that still reports success.
 */
export function parseBundle(path, raw) {
  let bundle;
  try {
    bundle = JSON.parse(raw);
  } catch (cause) {
    throw new Error(`${path}: not valid JSON (${cause.message})`);
  }
  if (bundle === null || typeof bundle !== 'object' || Array.isArray(bundle)) {
    throw new Error(`${path}: bundle is not an object`);
  }
  if (typeof bundle.source !== 'string' || bundle.source.trim() === '') {
    throw new Error(`${path}: bundle has no "source" compose-file path`);
  }
  if (!Array.isArray(bundle.declaredProfiles)) {
    throw new Error(
      `${path}: bundle has no "declaredProfiles" array. It comes from ` +
        '`docker compose config --profiles` and is what detects a render that ' +
        'silently dropped profile-gated services — an absent one is not an empty one.',
    );
  }
  if (bundle.declaredProfiles.some((p) => typeof p !== 'string')) {
    throw new Error(`${path}: "declaredProfiles" contains a non-string entry`);
  }
  if (bundle.config === null || typeof bundle.config !== 'object') {
    throw new Error(`${path}: bundle has no "config" object`);
  }
  return {
    source: bundle.source,
    declaredProfiles: bundle.declaredProfiles,
    config: bundle.config,
  };
}

export function run(argv, { readFile }) {
  const paths = [];
  let format = 'list';

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--format') {
      format = argv[++i];
      if (format !== 'list' && format !== 'matrix') {
        throw new Error(`unknown --format "${format}" (expected list|matrix)`);
      }
    } else if (arg.startsWith('--')) {
      throw new Error(`unknown option "${arg}"`);
    } else {
      paths.push(arg);
    }
  }

  if (paths.length === 0) {
    throw new Error('usage: compose-images.mjs [--format list|matrix] <bundle.json>...');
  }

  const bundles = paths.map((path) => {
    let raw;
    try {
      raw = readFile(path);
    } catch (cause) {
      throw new Error(`${path}: cannot read (${cause.message})`);
    }
    return parseBundle(path, raw);
  });

  assertProfilesFullyRendered(bundles);

  // Labelled by the SOURCE compose file, so a failing scan tells a reader
  // `deploy/docker-compose.yml:traefik` rather than the temp file the render
  // happened to land in.
  const documents = bundles.map((b) => ({ path: b.source, config: b.config }));
  const images = collectThirdPartyImages(documents);
  assertNonEmpty(images);
  assertAllPinnedByDigest(images);

  return format === 'matrix'
    ? formatMatrix(images)
    : images.map((i) => i.ref).join('\n');
}

// Only run when invoked directly, so the test file can import the pieces.
if (process.argv[1] && import.meta.url === `file://${process.argv[1]}`) {
  const { readFileSync } = await import('node:fs');
  try {
    console.log(run(process.argv.slice(2), { readFile: (p) => readFileSync(p, 'utf8') }));
  } catch (error) {
    console.error(`compose-images: ${error.message}`);
    process.exit(1);
  }
}
