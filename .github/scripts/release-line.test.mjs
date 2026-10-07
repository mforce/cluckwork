// Self-tests for the release-line decisions. Run with
// `node --test .github/scripts/release-line.test.mjs`.

import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { checkProposal, expectedSourceRef, maintenanceBranch } from "./release-line.mjs";

const RULESET = ["deletion", "creation", "non_fast_forward", "pull_request"];

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
      expectedSourceRef({ tag: "v0.1.6", mainCompare, branchCompare: "behind", branchRules: [] }),
      { ref: "refs/heads/main" },
    );
  }
});

test("a commit only on the ruled maintenance branch verifies against that branch", () => {
  for (const branchCompare of ["identical", "behind"]) {
    assert.deepEqual(
      expectedSourceRef({ tag: "v0.1.6", mainCompare: "diverged", branchCompare, branchRules: RULESET }),
      { ref: "refs/heads/release/v0.1.x" },
    );
  }
});

test("a maintenance branch missing any required rule is refused, naming what is missing", () => {
  for (const [branchRules, missing] of [
    [["deletion", "non_fast_forward", "pull_request"], "creation"],
    [["deletion", "creation", "pull_request"], "non_fast_forward"],
    [["deletion", "creation", "non_fast_forward"], "pull_request"],
    [["deletion"], "creation, non_fast_forward, pull_request"],
    [[], "creation, non_fast_forward, pull_request"],
    [undefined, "creation, non_fast_forward, pull_request"],
    ["creation,non_fast_forward,pull_request", "creation, non_fast_forward, pull_request"],
  ]) {
    assert.deepEqual(
      expectedSourceRef({ tag: "v0.1.6", mainCompare: "diverged", branchCompare: "behind", branchRules }),
      {
        error: `release/v0.1.x lacks the active ruleset rules ${missing}, so an attestation naming it proves nothing; add the release ruleset, then dispatch Release with v0.1.6`,
      },
      String(branchRules),
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
      expectedSourceRef({ tag: "v0.1.6", mainCompare, branchCompare, branchRules: RULESET }),
      { error: "the commit released as v0.1.6 is on neither main nor release/v0.1.x" },
    );
  }
});

test("a commit off main with a non-version tag is refused", () => {
  assert.deepEqual(
    expectedSourceRef({ tag: "hotfix", mainCompare: "diverged", branchCompare: "behind", branchRules: RULESET }),
    { error: "'hotfix' is not a vX.Y.Z tag, so it names no maintenance branch, and its commit is not on main" },
  );
});

test("each line may release its own next version", () => {
  assert.equal(checkProposal({ branch: "main", proposed: "1.0.0", takenTags: ["v0.1.5", "v0.1.4"] }), null);
  assert.equal(checkProposal({ branch: "main", proposed: "1.2.0", takenTags: ["v1.0.0", "v1.1.0", "v1.1.1"] }), null);
  assert.equal(checkProposal({ branch: "release/v0.1.x", proposed: "0.1.6", takenTags: ["v0.1.5", "v1.0.0"] }), null);
  assert.equal(checkProposal({ branch: "release/v1.1.x", proposed: "1.1.1", takenTags: ["v1.1.0", "v1.2.0"] }), null);
});

test("a hotfix line proposing a version outside its line is refused with its next patch", () => {
  assert.deepEqual(
    checkProposal({ branch: "release/v0.1.x", proposed: "1.0.0", takenTags: ["v0.1.4", "v0.1.5", "v0.0.9", "v1.0.0"] }),
    {
      error:
        "release/v0.1.x proposes 1.0.0, outside its 0.1.x line. Set \"versioning\": \"always-bump-patch\" in this branch's release-please-config.json; if a Release-As reached the branch, squash-merge the next PR into this branch with 'gh pr merge <N> --squash --body \"Release-As: 0.1.6\"' (or put that line in the squash dialog's extended description; a footer in a branch commit or the PR body is dropped). With nothing waiting, merge a PR holding one empty chore: commit that way.",
    },
  );
  for (const proposed of ["1.2.0", "0.2.0", "2.1.3"]) {
    assert.match(
      checkProposal({ branch: "release/v1.1.x", proposed, takenTags: ["v1.1.0", "v1.1.1"] }).error,
      /^release\/v1\.1\.x proposes .* outside its 1\.1\.x line\. .*"Release-As: 1\.1\.2"/,
      proposed,
    );
  }
  assert.match(
    checkProposal({ branch: "release/v2.0.x", proposed: "3.0.0", takenTags: [] }).error,
    /"Release-As: 2\.0\.0"/,
  );
});

test("a branch that is not a vX.Y.x line is not held to one", () => {
  for (const branch of ["main", "release/foo", "release/v1.1.x/x", "feature/release/v1.1.x"]) {
    assert.equal(checkProposal({ branch, proposed: "3.0.0", takenTags: ["v1.1.0"] }), null, branch);
  }
});

test("a taken version is refused with the next version for that branch", () => {
  assert.deepEqual(checkProposal({ branch: "main", proposed: "1.1.0", takenTags: ["v1.0.0", "v1.1.0", "v1.3.0", "v2.0.0", "v0.9.0"] }), {
    error:
      "v1.1.0 is already tagged or released, so a Release-As named a taken version or this branch's versioning is wrong. To release, squash-merge the next PR into this branch with 'gh pr merge <N> --squash --body \"Release-As: 1.4.0\"' (or put that line in the squash dialog's extended description; a footer in a branch commit or the PR body is dropped). With nothing waiting, merge a PR holding one empty chore: commit that way. Then merge the release PR it proposes.",
  });
  assert.match(
    checkProposal({ branch: "release/v0.1.x", proposed: "0.1.5", takenTags: ["v0.1.5", "v0.1.7", "v0.2.0"] }).error,
    /"Release-As: 0\.1\.8"/,
  );
});

test("a proposal that is not a version is refused rather than passed", () => {
  for (const proposed of ["", "null", "v0.1.6", undefined]) {
    assert.deepEqual(checkProposal({ branch: "main", proposed, takenTags: [] }), {
      error: `'${proposed}' is not a version release-please could have proposed`,
    });
  }
});

test("the CLI prints the branch and the source ref on stdout", () => {
  const branch = runCli(["branch", "v0.1.6"]);
  assert.equal(branch.status, 0);
  assert.equal(branch.stdout, "release/v0.1.x\n");

  const ref = runCli(["source-ref", "v0.1.6", "diverged", "behind", "pull_request,creation,non_fast_forward"]);
  assert.equal(ref.status, 0);
  assert.equal(ref.stdout, "refs/heads/release/v0.1.x\n");
});

test("the CLI refuses a rule list missing a required type, including none at all", () => {
  for (const rules of ["creation,non_fast_forward", "true", "", "missing", "pull_request non_fast_forward creation"]) {
    const result = runCli(["source-ref", "v0.1.6", "diverged", "behind", rules]);
    assert.equal(result.status, 1, rules);
    assert.equal(result.stdout, "");
  }
  assert.equal(runCli(["source-ref", "v0.1.6", "diverged", "behind"]).status, 1);
});

test("every CLI refusal exits 1 with an annotation on stderr and nothing on stdout", () => {
  for (const [args, stdin] of [
    [["branch", "latest"], ""],
    [["source-ref", "v0.1.6", "diverged", "missing", "creation,non_fast_forward,pull_request"], ""],
    [["proposal", "main", "0.1.6"], "v0.1.5\nv0.1.6\n"],
    [["proposal", "release/v0.1.x", "1.0.0"], ""],
    [["proposal", "main", "null"], ""],
    [["nonsense"], ""],
  ]) {
    const result = runCli(args, stdin);
    assert.equal(result.status, 1, args.join(" "));
    assert.equal(result.stdout, "", args.join(" "));
    assert.match(result.stderr, /^::error::/, args.join(" "));
  }
});

test("the CLI reads taken tags from stdin, one per line", () => {
  const free = runCli(["proposal", "release/v0.1.x", "0.1.7"], "v0.1.5\r\nv0.1.6\n\n");
  assert.equal(free.status, 0);
  assert.equal(free.stdout, "release/v0.1.x may release v0.1.7\n");

  const taken = runCli(["proposal", "release/v0.1.x", "0.1.6"], "v0.1.5\r\nv0.1.6\r\n");
  assert.equal(taken.status, 1);
  assert.match(taken.stderr, /--body "Release-As: 0\.1\.7"/);
});
