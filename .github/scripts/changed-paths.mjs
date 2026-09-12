// Classify a pull request's changed paths as documentation-only (#782). Run with
// `git diff --name-only origin/main...HEAD | node .github/scripts/changed-paths.mjs`,
// which writes `docs_only=true|false` in $GITHUB_OUTPUT format on stdout.
//
// The question is deliberately inverted. It is NOT "which jobs does this change
// need", which is a hand-maintained list of what someone thought of, and is the
// shape `AGENTS.md`'s guard rules tell you to avoid. It is "does this pull
// request contain literally nothing but documentation", so a path nobody has
// classified is code by default and the full suite runs.
//
// Everything here fails closed, meaning `false`, meaning "run everything". There
// is no input — empty, quoted, absolute, unreadable, malformed — that answers
// `true` by accident. A wrong `false` costs four minutes of runner time; a wrong
// `true` skips the image build and the Trivy scan on a change that needed them.

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

// Documentation is a closed table, not a chain of conditions. A prefix rule ends
// in `/` so `docsomething/x.cs` cannot match `docs/`.
const DOCUMENTATION_PREFIXES = ["docs/", "specs/", ".github/ISSUE_TEMPLATE/"];
const DOCUMENTATION_FILES = new Set(["LICENSE", ".github/PULL_REQUEST_TEMPLATE.md"]);

// Only a ROOT `*.md` is documentation. `web/README.md` sits next to the code it
// describes and nothing proves a change there cannot matter to the web job.
function isRootMarkdown(path) {
  return !path.includes("/") && path.endsWith(".md");
}

// A path git could not hand over verbatim is not classifiable here. git quotes a
// path containing a non-ASCII or control character (`"docs/r\303\251sum\303\251.md"`)
// under the default core.quotePath, and this classifier does not unquote it: an
// unclassifiable path is code.
function isSuspicious(path) {
  return (
    path !== path.trim() ||
    path.startsWith('"') ||
    path.startsWith("/") ||
    path.includes("\\") ||
    path.split("/").includes("..")
  );
}

export function isDocumentation(path) {
  if (typeof path !== "string" || path === "" || isSuspicious(path)) return false;
  if (DOCUMENTATION_FILES.has(path)) return true;
  if (DOCUMENTATION_PREFIXES.some((prefix) => path.startsWith(prefix))) return true;
  return isRootMarkdown(path);
}

// `every` over an empty array is `true`, which is the wrong answer: no paths means
// the diff could not be computed, so the explicit empty check is load-bearing.
export function isDocsOnly(paths) {
  if (!Array.isArray(paths) || paths.length === 0) return false;
  return paths.every(isDocumentation);
}

export function parsePaths(stdin) {
  if (typeof stdin !== "string") return [];
  return stdin.split("\n").map((line) => line.replace(/\r$/, "")).filter((line) => line !== "");
}

function main() {
  let docsOnly = false;
  try {
    // readFileSync over fd 0 rather than an async iterator over process.stdin:
    // the synchronous read surfaces an unreadable stdin as a throw (EISDIR on a
    // directory fd, EAGAIN on some TTYs), which is the failure the catch below
    // exists for and which the test suite can therefore actually provoke.
    docsOnly = isDocsOnly(parsePaths(readFileSync(0, "utf8")));
  } catch (err) {
    console.error(`changed-paths: could not read the changed paths, treating the change as code — ${err.message}`);
    docsOnly = false;
  }
  console.error(docsOnly ? "changed-paths: documentation only" : "changed-paths: contains code");
  console.log(`docs_only=${docsOnly}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main();
