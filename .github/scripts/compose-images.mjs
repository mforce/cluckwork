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
// `assertNonEmpty` cannot catch it — the set is not empty, just short. So every
// input is given as `<source>=<rendered>`: the `image:` declarations in the
// SOURCE are counted, and the render must account for every one of them. The
// resolved JSON stays authoritative for what each image IS; the source is
// consulted only for how many there should be, which is the one question a
// dropped service changes and a render cannot answer about itself. Pairing them
// in a single argument means the check cannot be omitted by forgetting a flag.
//
// Usage:
//   docker compose --profile '*' -f deploy/docker-compose.yml config --format json > c.json
//   node .github/scripts/compose-images.mjs deploy/docker-compose.yml=c.json
//   node .github/scripts/compose-images.mjs --format matrix a.yml=a.json b.yml=b.json
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

/**
 * Services carrying an `image` key across the resolved documents — build-only
 * services excluded, build-with-image services INCLUDED. This is the render's
 * side of the cross-check, so it must count declarations, not scan targets.
 */
export function countRenderedImageDeclarations(documents) {
  let n = 0;
  for (const { config } of documents) {
    for (const spec of Object.values(config?.services ?? {})) {
      if (spec && typeof spec === 'object' && String(spec.image ?? '').trim() !== '') n++;
    }
  }
  return n;
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
 * `image:` declarations in a RAW compose file.
 *
 * Deliberately the crudest possible read of the source — this is a counting
 * cross-check, not a parser, and it must not grow into one. Requiring `image:`
 * to sit at the start of an indented line skips `# image:` in a comment and
 * anything nested inside a value.
 */
export function countRawImageDeclarations(text) {
  return text.split(/\r?\n/).filter((line) => /^\s+image:\s*\S/.test(line)).length;
}

/**
 * Every `image:` in the source must be accounted for in the render.
 *
 * Compose emits `image` exactly where it is declared — it does NOT synthesise
 * one for a build-only service — so the two counts are directly comparable.
 * `rendered` therefore counts services carrying an image INCLUDING build ones,
 * which are excluded from scanning but still declared in the file.
 */
export function assertRenderIsComplete(rawCount, renderedCount) {
  if (rawCount === renderedCount) return;
  if (renderedCount < rawCount) {
    throw new Error(
      `the resolved compose document declares ${renderedCount} image(s) but the source ` +
        `file(s) declare ${rawCount} — ${rawCount - renderedCount} service(s) were dropped ` +
        "by the render. The usual cause is a missing --profile '*': a service behind " +
        '`profiles:` is omitted silently, and its pin would go unscanned while this ' +
        'check reported success.',
    );
  }
  throw new Error(
    `the resolved compose document declares ${renderedCount} image(s) but the source ` +
      `file(s) declare only ${rawCount} — the cross-check is reading the wrong files, ` +
      'or an override adds services the counted sources do not. Pass every raw compose ' +
      'file that contributed to the render via --cross-check.',
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

export function run(argv, { readFile }) {
  const pairs = [];
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
      // Each input is `<source compose file>=<its rendered JSON>`.
      //
      // One argument rather than two lists, for two reasons. The source path
      // becomes the LABEL a failing scan prints as "pinned at", so a reader is
      // told `deploy/docker-compose.yml:traefik` instead of the temp file the
      // render happened to land in. And the completeness cross-check can no
      // longer be forgotten: there is no way to name a rendered document
      // without also naming the source it must account for.
      const split = arg.indexOf('=');
      if (split <= 0 || split === arg.length - 1) {
        throw new Error(
          `expected <source-compose.yml>=<rendered.json>, got "${arg}". Naming the ` +
            'source is not optional: it labels the finding and it is what the ' +
            'render is checked against for silently dropped services.',
        );
      }
      pairs.push({ source: arg.slice(0, split), rendered: arg.slice(split + 1) });
    }
  }

  if (pairs.length === 0) {
    throw new Error(
      'usage: compose-images.mjs [--format list|matrix] ' +
        '<source-compose.yml>=<rendered.json>...',
    );
  }

  const read = (path) => {
    try {
      return readFile(path);
    } catch (cause) {
      throw new Error(`${path}: cannot read (${cause.message})`);
    }
  };

  const documents = pairs.map(({ source, rendered }) => {
    const raw = read(rendered);
    try {
      // Labelled by SOURCE, not by the rendered temp file.
      return { path: source, config: JSON.parse(raw) };
    } catch (cause) {
      throw new Error(`${rendered}: not valid JSON (${cause.message})`);
    }
  });

  const rawCount = pairs.reduce((n, p) => n + countRawImageDeclarations(read(p.source)), 0);
  assertRenderIsComplete(rawCount, countRenderedImageDeclarations(documents));

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
