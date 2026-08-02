#!/usr/bin/env node
// Release-version calculator for CI (#351).
//
// Every merge into main publishes the container image CI just built and
// scanned, under a fresh version tag. This decides what that version is, kept
// out of the YAML so it can be unit-tested (mirrors vuln-gate.mjs / lockfix.mjs).
//
// The bump is driven by LABELS on the merged PR, not by its title: an inferred
// bump is silently wrong when a prefix is typo'd, whereas a label is a
// deliberate act with a safe default. `release:major` and `release:minor` are
// the only two that mean anything; everything else — including no labels at
// all, which is the common case — is a patch.
//
// Existing tags come in on stdin (one per line, e.g. from `git tag --list`),
// NOT from a `git describe`: describe walks the ancestry of HEAD, so a tag on a
// sibling branch would be invisible and we would reissue a version that already
// exists. The whole tag list is the only safe input.
//
// Usage:
//   git tag --list 'v*' | node .github/scripts/next-version.mjs --labels "release:minor,bug"
//
// Prints the next version (`v1.4.0`) to stdout and exits 0. A refusal — an
// unreadable label argument — exits non-zero with a message on stderr and
// prints nothing, so a caller that forgets to check the exit code gets an empty
// tag rather than a wrong one.
//
// It does NOT guard against issuing a version that already exists, because it
// cannot: it counts up from the highest tag it was given, so the result is
// always strictly greater than every existing release tag. What it cannot see
// is a tag created after it read the list — two merges racing would compute the
// same version from the same input. That is handled where it is actually
// visible: the release job is serialised by a concurrency group, and
// `git tag` refuses to overwrite an existing tag, so the loser fails the run
// rather than moving a version an already-published image sits on.

import { pathToFileURL } from "node:url";

// The first version ever cut, used only when no release tag exists yet. Pre-1.0
// on purpose: the project is mid-Phase-1.1 and has not reached go-live.
export const INITIAL_VERSION = "v0.1.0";

export const MAJOR_LABEL = "release:major";
export const MINOR_LABEL = "release:minor";

// Strict `vMAJOR.MINOR.PATCH`, anchored, no pre-release or build metadata and no
// leading zeros. Deliberately narrow: anything else in the tag list (a `spec/…`
// marker, a hand-made `v2-old`, a future `v1.0.0-rc1`) is not a release version
// this tool issued, and must not become the base it counts from.
const RELEASE_TAG = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

/**
 * Parse a release tag into its numeric parts, or null when it is not one.
 */
export function parseVersion(tag) {
  const m = RELEASE_TAG.exec(String(tag).trim());
  if (m === null) return null;
  return { major: Number(m[1]), minor: Number(m[2]), patch: Number(m[3]) };
}

export function formatVersion({ major, minor, patch }) {
  return `v${major}.${minor}.${patch}`;
}

/**
 * Highest release version among `tags`, or null when there is none.
 *
 * Compared numerically field by field — NOT lexically, which would rank v0.9.0
 * above v0.10.0 and silently reissue a version already published.
 */
export function latestVersion(tags) {
  let best = null;
  for (const tag of tags) {
    const v = parseVersion(tag);
    if (v === null) continue;
    if (best === null || compare(v, best) > 0) best = v;
  }
  return best;
}

function compare(a, b) {
  return a.major - b.major || a.minor - b.minor || a.patch - b.patch;
}

/**
 * Which part of the version the merged PR's labels ask to bump.
 *
 * Both labels present -> major. A PR that is labelled both breaking and
 * feature-adding IS breaking; taking the larger bump is the safe direction,
 * since an over-bump costs a version number and an under-bump lies to every
 * consumer pinning a range.
 */
export function bumpFor(labels) {
  const set = new Set((labels ?? []).map((l) => String(l).trim().toLowerCase()));
  if (set.has(MAJOR_LABEL)) return "major";
  if (set.has(MINOR_LABEL)) return "minor";
  return "patch";
}

export function applyBump(version, bump) {
  switch (bump) {
    case "major":
      return { major: version.major + 1, minor: 0, patch: 0 };
    case "minor":
      return { major: version.major, minor: version.minor + 1, patch: 0 };
    case "patch":
      return { major: version.major, minor: version.minor, patch: version.patch + 1 };
    default:
      throw new Error(`unknown bump: ${bump}`);
  }
}

/**
 * The whole decision: existing tags + the merged PR's labels -> next version.
 *
 * Returns `{ version, bump, previous }`. `previous` is null on the very first
 * release, where the bump is not applied at all — INITIAL_VERSION is issued
 * as-is, so a `release:minor` on the first merge does not skip to v0.2.0.
 */
export function nextVersion({ tags = [], labels = [] } = {}) {
  const previous = latestVersion(tags);
  const bump = bumpFor(labels);

  const version =
    previous === null ? INITIAL_VERSION : formatVersion(applyBump(previous, bump));

  return { version, bump, previous: previous === null ? null : formatVersion(previous) };
}

/**
 * Labels from the CLI: a single comma-separated `--labels` value. Empty and
 * absent both mean "no labels", which is the ordinary patch path.
 */
export function parseArgs(argv) {
  const labels = [];
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] !== "--labels") continue;
    const value = argv[i + 1];
    if (value === undefined) throw new Error("--labels requires a value");
    labels.push(...value.split(",").map((l) => l.trim()).filter(Boolean));
    i++;
  }
  return { labels };
}

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  try {
    const { labels } = parseArgs(process.argv.slice(2));
    const tags = (await readStdin()).split("\n").map((t) => t.trim()).filter(Boolean);
    const { version, bump, previous } = nextVersion({ tags, labels });
    process.stderr.write(`bump=${bump} previous=${previous ?? "(none)"}\n`);
    process.stdout.write(version);
  } catch (err) {
    process.stderr.write(`next-version: ${err.message}\n`);
    process.exit(1);
  }
}
