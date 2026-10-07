// Self-tests for the release-line decisions. Run with
// `node --test .github/scripts/release-line.test.mjs`.

import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { expectedSourceRef, maintenanceBranch, versionCollision } from "./release-line.mjs";

const CLI = fileURLToPath(new URL("./release-line.mjs", import.meta.url));

function runCli(args, stdin = "") {
  return spawnSync(process.execPath, [CLI, ...args], { input: stdin, encoding: "utf8" });
}

test("a version tag names its minor line's maintenance branch", () => {
  assert.equal(maintenanceBranch("v0.1.6"), "release/v0.1.x");
  assert.equal(maintenanceBranch("v12.30.0"), "release/v12.30.x");
});

test("anything but a plain vX.Y.Z tag names no branch", () => {
  for (const tag of ["0.1.6", "v0.1", "v0.1.6-rc.1", "v0.1.6 ", "release/v0.1.x", "", undefined]) {
    assert.equal(maintenanceBranch(tag), null, String(tag));
  }
});

test("a commit on main verifies against main, whatever the branch says", () => {
  for (const mainCompare of ["identical", "behind"]) {
    assert.deepEqual(
      expectedSourceRef({ tag: "v0.1.6", mainCompare, branchCompare: "behind", branchProtected: false }),
      { ref: "refs/heads/main" },
    );
  }
});

test("a commit only on the protected maintenance branch verifies against that branch", () => {
  for (const branchCompare of ["identical", "behind"]) {
    assert.deepEqual(
      expectedSourceRef({ tag: "v0.1.6", mainCompare: "diverged", branchCompare, branchProtected: true }),
      { ref: "refs/heads/release/v0.1.x" },
    );
  }
});

test("an unprotected maintenance branch is refused", () => {
  for (const branchProtected of [false, undefined, "true"]) {
    assert.deepEqual(
      expectedSourceRef({ tag: "v0.1.6", mainCompare: "diverged", branchCompare: "behind", branchProtected }),
      {
        error:
          "release/v0.1.x is not a protected branch, so an attestation naming it proves nothing; protect it like main, then dispatch Release with v0.1.6",
      },
      String(branchProtected),
    );
  }
});

test("a commit on neither line is refused", () => {
  for (const [mainCompare, branchCompare] of [
    ["diverged", "diverged"],
    ["ahead", "ahead"],
    ["diverged", "missing"],
    ["missing", "missing"],
  ]) {
    assert.deepEqual(
      expectedSourceRef({ tag: "v0.1.6", mainCompare, branchCompare, branchProtected: true }),
      { error: "the commit released as v0.1.6 is on neither main nor release/v0.1.x" },
    );
  }
});

test("a commit off main with a non-version tag is refused", () => {
  assert.deepEqual(
    expectedSourceRef({ tag: "hotfix", mainCompare: "diverged", branchCompare: "behind", branchProtected: true }),
    { error: "'hotfix' is not a vX.Y.Z tag, so it names no maintenance branch, and its commit is not on main" },
  );
});

test("a free version does not collide", () => {
  assert.equal(versionCollision("0.1.6", ["v0.1.5", "v0.1.4", "v0.0.6"]), null);
});

test("a taken version collides and names the next free patch", () => {
  assert.deepEqual(versionCollision("0.1.6", ["v0.1.5", "v0.1.6"]), {
    error:
      "v0.1.6 is already tagged or released on another line. Add a 'Release-As: 0.1.7' footer to the next commit merged into this branch, then merge the release PR it proposes.",
  });
  assert.match(versionCollision("0.1.6", ["v0.1.6", "v0.1.7", "v0.1.9"]).error, /'Release-As: 0\.1\.8'/);
});

test("a proposal that is not a version is refused rather than passed", () => {
  for (const proposed of ["", "null", "v0.1.6", undefined]) {
    assert.deepEqual(versionCollision(proposed, []), {
      error: `'${proposed}' is not a version release-please could have proposed`,
    });
  }
});

test("the CLI prints the branch and the source ref on stdout", () => {
  const branch = runCli(["branch", "v0.1.6"]);
  assert.equal(branch.status, 0);
  assert.equal(branch.stdout, "release/v0.1.x\n");

  const ref = runCli(["source-ref", "v0.1.6", "diverged", "behind", "true"]);
  assert.equal(ref.status, 0);
  assert.equal(ref.stdout, "refs/heads/release/v0.1.x\n");
});

test("the CLI treats only the literal `true` as protected", () => {
  for (const flag of ["false", "", "True", "1"]) {
    const result = runCli(["source-ref", "v0.1.6", "diverged", "behind", flag]);
    assert.equal(result.status, 1, flag);
    assert.equal(result.stdout, "");
  }
});

test("every CLI refusal exits 1 with an annotation on stderr and nothing on stdout", () => {
  for (const [args, stdin] of [
    [["branch", "latest"], ""],
    [["source-ref", "v0.1.6", "diverged", "missing", "true"], ""],
    [["collision", "0.1.6"], "v0.1.5\nv0.1.6\n"],
    [["collision", "null"], ""],
    [["nonsense"], ""],
  ]) {
    const result = runCli(args, stdin);
    assert.equal(result.status, 1, args.join(" "));
    assert.equal(result.stdout, "", args.join(" "));
    assert.match(result.stderr, /^::error::/, args.join(" "));
  }
});

test("the CLI reads taken tags from stdin, one per line", () => {
  const free = runCli(["collision", "0.1.7"], "v0.1.5\r\nv0.1.6\n\n");
  assert.equal(free.status, 0);
  assert.equal(free.stdout, "v0.1.7 is not taken\n");

  const taken = runCli(["collision", "0.1.6"], "v0.1.5\r\nv0.1.6\r\n");
  assert.equal(taken.status, 1);
  assert.match(taken.stderr, /'Release-As: 0\.1\.7'/);
});
