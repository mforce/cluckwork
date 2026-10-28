// Self-tests for the Dependabot lock-file classifier (#203) and its applicator
// (#1373). Run with
// `node --test .github/scripts/lockfix.test.mjs`.
//
// The load-bearing cases are the ones that MUST refuse to commit: any non-lock
// path in the diff, a NUL-delimited path with a space, and a rename that drags
// in a foreign source. Each asserts the fail-closed verdict, not just absence.

import test, { after } from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { chmodSync, copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { changedPaths, classify, LOCK_FILES } from "./lockfix.mjs";

// Build a `git status --porcelain -z` stream: each record is `XY <path>` and
// the stream is NUL-terminated per record.
const z = (...records) => records.map((r) => r + "\0").join("");

const scratch = mkdtempSync(join(tmpdir(), "lockfix-test-"));
after(() => rmSync(scratch, { recursive: true, force: true }));

test("the allowlist is exactly the 10 solution lock files", () => {
  assert.equal(LOCK_FILES.length, 10);
  assert.ok(LOCK_FILES.every((p) => p.endsWith("/packages.lock.json")));
});

// Walk the solution, exclude nothing: every project Cluckwork.slnx references
// restores a lock, so every one of them must be in the allowlist. The 7-entry
// list shipped with #203 was written by hand and silently missed the two
// AppHost projects added in #565 — a Dependabot bump touching an Aspire package
// would have left their locks stale and the PR red. A recalled list is exactly
// what this allowlist exists to replace.
test("the allowlist covers every project in Cluckwork.slnx", () => {
  const slnx = readFileSync(new URL("../../Cluckwork.slnx", import.meta.url), "utf8")
    // Keep a separator so removing a comment cannot assemble a project tag.
    .replace(/<!--[\s\S]*?-->/g, " ");
  const projects = [...slnx.matchAll(/<Project\b[^>]*\bPath\s*=\s*["']([^"']+)["'][^>]*>/g)]
    .map((m) => m[1].replaceAll("\\", "/").replace(/[^/]+\.csproj$/, "packages.lock.json"))
    .sort();
  assert.ok(projects.length >= 10, `parsed only ${projects.length} projects from the slnx`);
  assert.deepEqual([...LOCK_FILES].sort(), projects);
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

test("the CLI classifies when invoked through a symlink", () => {
  const link = join(mkdtempSync(join(scratch, "link-")), "lockfix.mjs");
  symlinkSync(fileURLToPath(new URL("./lockfix.mjs", import.meta.url)), link);
  const run = spawnSync(process.execPath, [link], { input: z(" M nuget.config"), encoding: "utf8" });
  assert.equal(run.status, 2, run.stdout + run.stderr);
});

// Two directories deep, like every lock, so TO_APPLY_SH climbs out of pr/.
const LOCK = LOCK_FILES[0];

// Runs lockfix-apply.sh in a scratch workspace laid out like the commit job:
// trusted/ scripts, a pr/ checkout of a pushed branch, and an artifact of new
// locks. `tamper(pr, commit, scripts)` shapes the workspace before the run.
function runApply(tamper = () => {}) {
  const dir = mkdtempSync(join(scratch, "apply-"));
  const git = (cwd, ...args) => execFileSync("git", ["-C", cwd, ...args], { encoding: "utf8" });
  const scripts = join(dir, "trusted/.github/scripts");
  mkdirSync(scripts, { recursive: true });
  for (const f of ["lockfix.mjs", "lockfix-apply.sh"]) {
    copyFileSync(new URL(`./${f}`, import.meta.url), join(scripts, f));
  }
  const pr = join(dir, "pr");
  git(dir, "init", "-q", "--bare", "remote.git");
  git(dir, "init", "-q", "-b", "main", "pr");
  for (const lock of LOCK_FILES) {
    for (const [root, body] of [[pr, "{}\n"], [join(dir, "art"), '{"new":1}\n']]) {
      mkdirSync(dirname(join(root, lock)), { recursive: true });
      writeFileSync(join(root, lock), body);
    }
  }
  const commit = (msg) => {
    git(pr, "add", "-A");
    git(pr, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", msg);
  };
  commit("base");
  git(pr, "remote", "add", "origin", join(dir, "remote.git"));
  git(pr, "push", "-q", "origin", "main");
  tamper(pr, commit, scripts);
  const run = spawnSync("bash", [join(scripts, "lockfix-apply.sh")], {
    cwd: dir,
    encoding: "utf8",
    env: { ...process.env, ARTIFACT_DIR: join(dir, "art"), HEAD_BRANCH: "pr" },
  });
  const pushed = spawnSync("git", ["-C", join(dir, "remote.git"), "show", `pr:${LOCK}`], { encoding: "utf8" });
  return { run, pushed: pushed.stdout, applyScript: readFileSync(join(scripts, "lockfix-apply.sh"), "utf8") };
}

const APPLY_SH = readFileSync(new URL("./lockfix-apply.sh", import.meta.url), "utf8");
const TO_APPLY_SH = "../../../trusted/.github/scripts/lockfix-apply.sh";

test("apply: regular tracked locks -> commit and push", () => {
  const { run, pushed } = runApply();
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.equal(pushed, '{"new":1}\n');
});

test("apply: a lock committed as a symlink -> abort without writing through it", () => {
  const { run, pushed, applyScript } = runApply((pr, commit) => {
    rmSync(join(pr, LOCK));
    symlinkSync(TO_APPLY_SH, join(pr, LOCK));
    commit("symlink");
  });
  assert.notEqual(run.status, 0);
  assert.equal(applyScript, APPLY_SH);
  assert.equal(pushed, "");
});

test("apply: a symlink in the working tree over a 100644 entry -> abort without writing through it", () => {
  const { run, pushed, applyScript } = runApply((pr) => {
    rmSync(join(pr, LOCK));
    symlinkSync(TO_APPLY_SH, join(pr, LOCK));
  });
  assert.notEqual(run.status, 0);
  assert.match(run.stdout, /goes through a symlink/);
  assert.equal(applyScript, APPLY_SH);
  assert.equal(pushed, "");
});

test("apply: a lock tracked as 100755 -> abort", () => {
  const { run, pushed } = runApply((pr, commit) => {
    chmodSync(join(pr, LOCK), 0o755);
    commit("exec");
  });
  assert.notEqual(run.status, 0);
  assert.match(run.stdout, /not a tracked regular file/);
  assert.equal(pushed, "");
});

test("apply: a directory committed at a lock path -> abort", () => {
  const { run, pushed } = runApply((pr, commit) => {
    rmSync(join(pr, LOCK));
    mkdirSync(join(pr, LOCK));
    writeFileSync(join(pr, LOCK, "packages.lock.json"), "{}\n");
    commit("dir");
  });
  assert.notEqual(run.status, 0);
  assert.match(run.stdout, /not a tracked regular file/);
  assert.equal(pushed, "");
});

test("apply: a classifier that exits 0 without classifying -> refuse", () => {
  const { run, pushed } = runApply((_pr, _commit, scripts) => {
    writeFileSync(join(scripts, "lockfix.mjs"), "");
  });
  assert.notEqual(run.status, 0);
  assert.match(run.stdout, /Classifier refused \(exit 0\)/);
  assert.equal(pushed, "");
});
