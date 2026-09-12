// Self-tests for the changed-path classifier (#782). Run with
// `node --test .github/scripts/changed-paths.test.mjs`.
//
// The cases that matter are the ones where the answer MUST be `false`: a mixed
// pull request, an empty diff, a path that only looks like a doc path, and a
// path git could not hand over verbatim. Each asserts the fail-closed verdict
// rather than an absence, because `false` is what keeps the image build running.

import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, openSync, rmSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { isDocumentation, isDocsOnly, parsePaths } from "./changed-paths.mjs";

const CLI = fileURLToPath(new URL("./changed-paths.mjs", import.meta.url));

function runCli(stdin) {
  const result = spawnSync(process.execPath, [CLI], { input: stdin, encoding: "utf8" });
  assert.equal(result.status, 0, `the CLI must always exit 0 so the caller can append its output: ${result.stderr}`);
  return result.stdout.trim();
}

test("a documentation-only change is documentation only", () => {
  assert.equal(
    isDocsOnly([
      "docs/decisions/782-ci-job-gating.md",
      "specs/product/GLOSSARY.md",
      "AGENTS.md",
      "LICENSE",
      ".github/ISSUE_TEMPLATE/bug.yml",
      ".github/PULL_REQUEST_TEMPLATE.md",
    ]),
    true,
  );
});

test("each documentation form is documentation on its own", () => {
  assert.equal(isDocumentation("docs/architecture.md"), true);
  assert.equal(isDocumentation("specs/product/specs.md"), true);
  assert.equal(isDocumentation("README.md"), true);
  assert.equal(isDocumentation("LICENSE"), true);
  assert.equal(isDocumentation(".github/ISSUE_TEMPLATE/config.yml"), true);
  assert.equal(isDocumentation(".github/PULL_REQUEST_TEMPLATE.md"), true);
});

// The easy bug: asking "are any docs changed" instead of "are no code files
// changed". Every path below contains documentation, and every one must be false.
test("a mixed change is not documentation only", () => {
  assert.equal(isDocsOnly(["docs/architecture.md", "src/Cluckwork.Domain/Flock.cs"]), false);
  assert.equal(isDocsOnly(["README.md", "web/src/App.tsx"]), false);
  assert.equal(isDocsOnly(["docs/releasing.md", ".github/workflows/ci.yml"]), false);
  assert.equal(isDocsOnly(["specs/product/GLOSSARY.md", "Directory.Packages.props"]), false);
  assert.equal(isDocsOnly(["docs/a.md", "docs/b.md", "docs/c.md", "src/Cluckwork.Api/Program.cs"]), false);
});

test("an empty change is not documentation only", () => {
  assert.equal(isDocsOnly([]), false);
  assert.equal(isDocsOnly([""]), false);
  assert.equal(isDocsOnly(["docs/a.md", ""]), false);
});

test("a path that is not a list of strings is not documentation only", () => {
  assert.equal(isDocsOnly(undefined), false);
  assert.equal(isDocsOnly(null), false);
  assert.equal(isDocsOnly("docs/a.md"), false);
  assert.equal(isDocsOnly([null]), false);
  assert.equal(isDocsOnly([{ path: "docs/a.md" }]), false);
});

// A prefix rule that forgot its trailing slash would call all four of these
// documentation.
test("a path that merely starts with a documentation prefix is code", () => {
  assert.equal(isDocumentation("docsomething/x.cs"), false);
  assert.equal(isDocumentation("specsfile.cs"), false);
  assert.equal(isDocumentation("docs.cs"), false);
  assert.equal(isDocumentation(".github/ISSUE_TEMPLATEish/x.yml"), false);
});

test("only a root markdown file is documentation", () => {
  assert.equal(isDocumentation("web/README.md"), false);
  assert.equal(isDocumentation("src/Cluckwork.Api/NOTES.md"), false);
  assert.equal(isDocumentation("tools/simulation/README.md"), false);
});

test("generated graph output is code, deliberately", () => {
  assert.equal(isDocumentation("graphify-out/GRAPH_REPORT.md"), false);
  assert.equal(isDocsOnly(["docs/a.md", "graphify-out/graph.json"]), false);
});

test("a path git could not hand over verbatim is code", () => {
  assert.equal(isDocumentation('"docs/r\\303\\251sum\\303\\251.md"'), false);
  assert.equal(isDocumentation("/docs/a.md"), false);
  assert.equal(isDocumentation("docs\\a.md"), false);
  assert.equal(isDocumentation("docs/../src/Flock.cs"), false);
  assert.equal(isDocumentation(" docs/a.md"), false);
  assert.equal(isDocumentation("docs/a.md "), false);
});

test("git's trailing newline is not a path", () => {
  assert.deepEqual(parsePaths("docs/a.md\nAGENTS.md\n"), ["docs/a.md", "AGENTS.md"]);
  assert.deepEqual(parsePaths("docs/a.md\r\n"), ["docs/a.md"]);
  assert.deepEqual(parsePaths(""), []);
  assert.deepEqual(parsePaths(undefined), []);
});

test("the CLI writes a GITHUB_OUTPUT line and always exits 0", () => {
  assert.equal(runCli("docs/a.md\nAGENTS.md\n"), "docs_only=true");
  assert.equal(runCli("docs/a.md\nsrc/Flock.cs\n"), "docs_only=false");
  assert.equal(runCli(""), "docs_only=false");
  assert.equal(runCli("\n\n"), "docs_only=false");
});

// A directory fd on stdin makes the read throw EISDIR, which is the only way to
// reach the CLI's catch. An unreadable stdin is not "nothing changed".
test("an unreadable stdin reports code, not documentation", () => {
  const fd = openSync(tmpdir(), "r");
  const result = spawnSync(process.execPath, [CLI], { stdio: [fd, "pipe", "pipe"], encoding: "utf8" });
  assert.equal(result.status, 0);
  assert.equal(result.stdout.trim(), "docs_only=false");
  assert.match(result.stderr, /could not read the changed paths/);
});

// End to end over real git output, which is how the workflow calls it: a change
// that only DELETES a code file still reaches the classifier as that file's path.
test("a deleted code file still counts as code", () => {
  const repo = mkdtempSync(join(tmpdir(), "changed-paths-"));
  try {
    const git = (...args) => execFileSync("git", ["-C", repo, ...args], { encoding: "utf8" });
    git("init", "-q", "-b", "main");
    git("config", "user.email", "test@example.com");
    git("config", "user.name", "test");
    mkdirSync(join(repo, "docs"));
    mkdirSync(join(repo, "src"));
    writeFileSync(join(repo, "docs", "a.md"), "before\n");
    writeFileSync(join(repo, "src", "Gone.cs"), "class Gone {}\n");
    git("add", "-A");
    git("commit", "-qm", "base");
    const base = git("rev-parse", "HEAD").trim();

    rmSync(join(repo, "src", "Gone.cs"));
    writeFileSync(join(repo, "docs", "a.md"), "after\n");
    git("add", "-A");
    git("commit", "-qm", "delete the code file, edit the doc");

    const changed = git("diff", "--name-only", `${base}...HEAD`);
    assert.deepEqual(parsePaths(changed), ["docs/a.md", "src/Gone.cs"]);
    assert.equal(runCli(changed), "docs_only=false");
  } finally {
    rmSync(repo, { recursive: true, force: true });
  }
});
