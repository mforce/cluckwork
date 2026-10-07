// Release-line decisions for release-please.yml (#351, hotfix lines 2026-10-07).
// The workflow gathers facts from the GitHub API; this file decides. Run as
//
//   node release-line.mjs branch <tag>
//       prints the maintenance branch a tag belongs to (v0.1.6 -> release/v0.1.x)
//   node release-line.mjs source-ref <tag> <main-compare> <branch-compare> <branch-rules>
//       prints the ref promotion must pass to `gh attestation verify --source-ref`.
//       The compare arguments are the compare API's `.status` for
//       `<branch>...<release sha>`, or `missing` on a 404. <branch-rules> is the
//       comma-joined rule types `rules/branches/<branch>` returns.
//   node release-line.mjs proposal <branch> <proposed version>   (taken tags on stdin)
//       exits non-zero when a hotfix line proposes a version outside its line,
//       or when the version's tag or release already exists
//
// Every refusal exits 1 with a `::error::` line on stderr, so a step capturing
// stdout under `set -e` fails closed and still shows why.

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const VERSION = /^(\d+)\.(\d+)\.(\d+)$/;
const VERSION_TAG = /^v(\d+)\.(\d+)\.(\d+)$/;
const LINE_BRANCH = /^release\/v(\d+)\.(\d+)\.x$/;

// The rules that make a ref mean "reviewed and merged": no direct pushes, no
// rewritten history, and no new branch of this name pushed with unreviewed
// commits. `protected: true` from the branches API is not enough, because any
// matching rule sets it.
const REQUIRED_RULES = ["creation", "non_fast_forward", "pull_request"];

export function maintenanceBranch(tag) {
  const match = VERSION_TAG.exec(tag);
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
export function expectedSourceRef({ tag, mainCompare, branchCompare, branchRules }) {
  if (reachable(mainCompare)) return { ref: "refs/heads/main" };
  const branch = maintenanceBranch(tag);
  if (!branch) return { error: `'${tag}' is not a vX.Y.Z tag, so it names no maintenance branch, and its commit is not on main` };
  if (!reachable(branchCompare)) return { error: `the commit released as ${tag} is on neither main nor ${branch}` };
  // The ruleset is the premise for trusting this ref at all: ci.yml on an
  // unruled branch is editable by anyone with push access, and an attestation
  // pinned to that ref proves nothing about review.
  const active = new Set(branchRules);
  const missing = REQUIRED_RULES.filter((rule) => !active.has(rule));
  if (missing.length > 0) return { error: `${branch} lacks the active ruleset rules ${missing.join(", ")}, so an attestation naming it proves nothing; add the release ruleset, then dispatch Release with ${tag}` };
  return { ref: `refs/heads/${branch}` };
}

// The version a branch should force, above every taken one. A hotfix line
// bumps its patch; any other branch (main) bumps the minor.
function nextVersion(line, major, takenTags) {
  const taken = takenTags.map((tag) => VERSION_TAG.exec(tag)).filter(Boolean).map((m) => m.slice(1).map(Number));
  if (line) {
    const patches = taken.filter(([a, b]) => a === line[0] && b === line[1]).map(([, , c]) => c);
    return `${line[0]}.${line[1]}.${Math.max(-1, ...patches) + 1}`;
  }
  const minors = taken.filter(([a]) => a === major).map(([, b]) => b);
  return `${major}.${Math.max(-1, ...minors) + 1}.0`;
}

// Squash commits here carry the PR title and an empty body, so Release-As
// survives only when added at merge time.
function forceAdvice(version) {
  return `squash-merge the next PR into this branch with 'gh pr merge <N> --squash --body "Release-As: ${version}"' (or put that line in the squash dialog's extended description; a footer in a branch commit or the PR body is dropped). With nothing waiting, merge a PR holding one empty chore: commit that way.`;
}

// Run on the release PR's proposal, before anyone merges it. A hotfix line
// releases only X.Y.<patch>; promotion would refuse anything else later,
// because the branch derived from the tag would not hold the commit.
export function checkProposal({ branch, proposed, takenTags }) {
  const match = VERSION.exec(proposed);
  if (!match) return { error: `'${proposed}' is not a version release-please could have proposed` };
  const major = Number(match[1]);
  const lineMatch = LINE_BRANCH.exec(branch);
  const line = lineMatch && [Number(lineMatch[1]), Number(lineMatch[2])];
  if (line && (major !== line[0] || Number(match[2]) !== line[1])) {
    return {
      error: `${branch} proposes ${proposed}, outside its ${line[0]}.${line[1]}.x line. Set "versioning": "always-bump-patch" in this branch's release-please-config.json; if a Release-As reached the branch, ${forceAdvice(nextVersion(line, major, takenTags))}`,
    };
  }
  if (!takenTags.includes(`v${proposed}`)) return null;
  return {
    error: `v${proposed} is already tagged or released, so a Release-As named a taken version or this branch's versioning is wrong. To release, ${forceAdvice(nextVersion(line, major, takenTags))} Then merge the release PR it proposes.`,
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
    const [tag, mainCompare, branchCompare, rules = ""] = args;
    const result = expectedSourceRef({ tag, mainCompare, branchCompare, branchRules: rules.split(",").filter(Boolean) });
    if (result.error) refuse(result.error);
    console.log(result.ref);
  } else if (command === "proposal") {
    const [branch, proposed] = args;
    const takenTags = readFileSync(0, "utf8").split("\n").map((line) => line.trim()).filter(Boolean);
    const result = checkProposal({ branch, proposed, takenTags });
    if (result) refuse(result.error);
    console.log(`${branch} may release v${proposed}`);
  } else {
    refuse(`unknown command '${command}'`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main(process.argv.slice(2));
