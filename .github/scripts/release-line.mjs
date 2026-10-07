// Release-line decisions for release-please.yml (#351, hotfix lines 2026-10-07).
// The workflow gathers facts from the GitHub API; this file decides. Run as
//
//   node release-line.mjs branch <tag>
//       prints the maintenance branch a tag belongs to (v0.1.6 -> release/v0.1.x)
//   node release-line.mjs source-ref <tag> <main-compare> <branch-compare> <branch-protected>
//       prints the ref promotion must pass to `gh attestation verify --source-ref`.
//       The compare arguments are the compare API's `.status` for
//       `<branch>...<release sha>`, or `missing` on a 404.
//   node release-line.mjs collision <proposed version>   (taken tag names on stdin)
//       exits non-zero when that version's tag or release already exists
//
// Every refusal exits 1 with a `::error::` line on stderr, so a step capturing
// stdout under `set -e` fails closed and still shows why.

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const VERSION = /^(\d+)\.(\d+)\.(\d+)$/;

export function maintenanceBranch(tag) {
  const match = VERSION.exec(typeof tag === "string" && tag.startsWith("v") ? tag.slice(1) : "");
  return match ? `release/v${match[1]}.${match[2]}.x` : null;
}

// The compare API answers `identical` or `behind` only when the sha is an
// ancestor of (or equal to) the branch tip. `ahead` and `diverged` mean it is not
// in that branch's history.
function reachable(compareStatus) {
  return compareStatus === "identical" || compareStatus === "behind";
}

// Main first: every commit before the cut is on both lines, and its image was
// built by main's CI. Only a commit that exists solely on the maintenance branch
// was built there. Never a wildcard: the branch is derived from the tag, so a
// v0.1.6 image built on release/v0.2.x does not verify.
export function expectedSourceRef({ tag, mainCompare, branchCompare, branchProtected }) {
  if (reachable(mainCompare)) return { ref: "refs/heads/main" };
  const branch = maintenanceBranch(tag);
  if (!branch) return { error: `'${tag}' is not a vX.Y.Z tag, so it names no maintenance branch, and its commit is not on main` };
  if (!reachable(branchCompare)) return { error: `the commit released as ${tag} is on neither main nor ${branch}` };
  // Branch protection is the premise for trusting this ref at all: ci.yml on an
  // unprotected branch is editable by anyone with push access, and an
  // attestation pinned to that ref proves nothing about review.
  if (branchProtected !== true) return { error: `${branch} is not a protected branch, so an attestation naming it proves nothing; protect it like main, then dispatch Release with ${tag}` };
  return { ref: `refs/heads/${branch}` };
}

// Below 1.0.0 main and a hotfix line both propose the next patch, so whichever
// releases second proposes a version that is already taken.
export function versionCollision(proposed, takenTags) {
  const match = VERSION.exec(typeof proposed === "string" ? proposed : "");
  if (!match) return { error: `'${proposed}' is not a version release-please could have proposed` };
  const taken = new Set(takenTags);
  if (!taken.has(`v${proposed}`)) return null;
  let patch = Number(match[3]) + 1;
  while (taken.has(`v${match[1]}.${match[2]}.${patch}`)) patch += 1;
  const next = `${match[1]}.${match[2]}.${patch}`;
  return {
    error: `v${proposed} is already tagged or released on another line. Add a 'Release-As: ${next}' footer to the next commit merged into this branch, then merge the release PR it proposes.`,
  };
}

function refuse(message) {
  console.error(`::error::${message}`);
  process.exit(1);
}

function main([command, ...args]) {
  if (command === "branch") {
    const branch = maintenanceBranch(args[0]);
    if (!branch) refuse(`'${args[0]}' is not a vX.Y.Z tag`);
    console.log(branch);
  } else if (command === "source-ref") {
    const [tag, mainCompare, branchCompare, protectedFlag] = args;
    const result = expectedSourceRef({ tag, mainCompare, branchCompare, branchProtected: protectedFlag === "true" });
    if (result.error) refuse(result.error);
    console.log(result.ref);
  } else if (command === "collision") {
    const taken = readFileSync(0, "utf8").split("\n").map((line) => line.trim()).filter(Boolean);
    const result = versionCollision(args[0], taken);
    if (result) refuse(result.error);
    console.log(`v${args[0]} is not taken`);
  } else {
    refuse(`unknown command '${command}'`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main(process.argv.slice(2));
