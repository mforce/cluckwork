#!/usr/bin/env node
// Dependabot lock-file classifier for CI (#203).
//
// The dependabot-lockfix workflow regenerates every project's
// packages.lock.json with `dotnet restore --force-evaluate` and must decide
// whether the result is safe to commit back to the PR branch. This is that
// decision, kept out of the YAML so it can be unit-tested (mirrors
// vuln-gate.mjs).
//
// It reads `git status --porcelain -z` on stdin — NUL-delimited, so a path with
// a space or newline is a single field, not several — and answers with an exit
// code:
//   0  every changed path is one of the known lock files -> commit
//   2  a path OUTSIDE that set is dirty -> abort, do not commit (fail closed)
//   3  nothing changed -> no-op
// Any other exit (a crash) the caller also treats as abort: a classifier that
// cannot tell must never green-light a commit.
//
// Usage: git status --porcelain -z | node .github/scripts/lockfix.mjs

import { pathToFileURL } from "node:url";

// The exact, closed set of lock files in the solution's reference chain. An
// EXACT allowlist, not a `**/packages.lock.json` glob: if restore ever writes a
// lock somewhere unexpected (a new project, a path typo, a planted file) that
// must surface as "foreign" and abort, not be waved through.
export const LOCK_FILES = Object.freeze([
  "src/Cluckwork.Domain/packages.lock.json",
  "src/Cluckwork.Application/packages.lock.json",
  "src/Cluckwork.Infrastructure/packages.lock.json",
  "src/Cluckwork.Api/packages.lock.json",
  "tests/Cluckwork.Domain.Tests/packages.lock.json",
  "tests/Cluckwork.Application.Tests/packages.lock.json",
  "tests/Cluckwork.Api.IntegrationTests/packages.lock.json",
]);

const ALLOW = new Set(LOCK_FILES);

// Parse `git status --porcelain -z`. Records are `XY <path>`; a rename/copy
// (X or Y is 'R'/'C') is followed by the source path as its own NUL field. We
// take EVERY path a record names — target and any source — so a rename whose
// target is a lock file can't smuggle a foreign source past the check.
export function changedPaths(porcelainZ) {
  const parts = String(porcelainZ).split("\0");
  const paths = [];
  for (let i = 0; i < parts.length; i++) {
    const rec = parts[i];
    if (!rec) continue; // trailing empty field after the final NUL
    const xy = rec.slice(0, 2);
    paths.push(rec.slice(3)); // drop the 2-char status + separating space
    if (xy[0] === "R" || xy[0] === "C" || xy[1] === "R" || xy[1] === "C") {
      const source = parts[++i]; // the rename/copy source is the next field
      if (source) paths.push(source);
    }
  }
  return paths;
}

// commit | abort | noop, with the paths behind the verdict for the log.
export function classify(porcelainZ) {
  const paths = changedPaths(porcelainZ);
  if (paths.length === 0) return { action: "noop", foreign: [], locks: [] };
  const foreign = paths.filter((p) => !ALLOW.has(p));
  if (foreign.length > 0) {
    return { action: "abort", foreign, locks: paths.filter((p) => ALLOW.has(p)) };
  }
  return { action: "commit", foreign: [], locks: paths };
}

const EXIT = { commit: 0, abort: 2, noop: 3 };

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
}

async function main() {
  const { action, foreign, locks } = classify(await readStdin());
  if (action === "commit") {
    console.log(`lockfix: ${locks.length} lock file(s) to commit:\n  ${locks.join("\n  ")}`);
  } else if (action === "noop") {
    console.log("lockfix: no changes after restore — nothing to commit.");
  } else {
    console.error(
      `::error::lockfix: refusing to commit — ${foreign.length} non-lock path(s) changed:\n  ${foreign.join("\n  ")}`,
    );
  }
  process.exitCode = EXIT[action];
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
