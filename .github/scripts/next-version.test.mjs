// Self-tests for the release-version calculator (#351). Run with
// `node --test .github/scripts/next-version.test.mjs`.
//
// The load-bearing cases are the ones where a plausible implementation is
// silently wrong: lexical tag sorting (v0.9.0 ranked above v0.10.0), a bump
// applied to the very first release, and a computed version that collides with
// a hand-cut tag. Each asserts the correct verdict, not merely that a value
// came back.

import test from "node:test";
import assert from "node:assert/strict";
import {
  INITIAL_VERSION,
  MAJOR_LABEL,
  MINOR_LABEL,
  applyBump,
  bumpFor,
  formatVersion,
  latestVersion,
  nextVersion,
  parseArgs,
  parseVersion,
} from "./next-version.mjs";

test("parseVersion accepts a strict vX.Y.Z and nothing else", () => {
  assert.deepEqual(parseVersion("v1.2.3"), { major: 1, minor: 2, patch: 3 });
  assert.deepEqual(parseVersion("  v0.1.0  "), { major: 0, minor: 1, patch: 0 });

  // Each of these must be REFUSED, not coerced: treating any of them as a
  // release version would make it the base the next bump counts from.
  for (const bad of [
    "1.2.3", // no v prefix
    "v1.2", // not three parts
    "v1.2.3.4",
    "v1.2.3-rc1", // pre-release: not a version this tool issues
    "v1.2.3+build",
    "v01.2.3", // leading zero
    "v1.02.3",
    "spec/v1.2.3",
    "v1.2.3-old",
    "",
  ]) {
    assert.equal(parseVersion(bad), null, `${bad} must not parse as a release tag`);
  }
});

test("latestVersion compares numerically, not lexically", () => {
  // The whole point: sorted as strings, "v0.9.0" > "v0.10.0", and CI would
  // reissue v0.10.0 on top of a published image.
  assert.deepEqual(latestVersion(["v0.9.0", "v0.10.0"]), { major: 0, minor: 10, patch: 0 });
  assert.deepEqual(latestVersion(["v0.10.0", "v0.9.0"]), { major: 0, minor: 10, patch: 0 });
  assert.deepEqual(latestVersion(["v1.2.9", "v1.2.10"]), { major: 1, minor: 2, patch: 10 });
  assert.deepEqual(latestVersion(["v2.0.0", "v10.0.0"]), { major: 10, minor: 0, patch: 0 });
});

test("latestVersion ignores tags that are not release versions", () => {
  assert.deepEqual(latestVersion(["v1.0.0", "v2.0.0-rc1", "nightly", "spec/v9.9.9"]), {
    major: 1,
    minor: 0,
    patch: 0,
  });
  assert.equal(latestVersion(["nightly", "v2.0.0-rc1"]), null);
  assert.equal(latestVersion([]), null);
});

test("bumpFor defaults to patch and is case-insensitive", () => {
  assert.equal(bumpFor([]), "patch");
  assert.equal(bumpFor(undefined), "patch");
  assert.equal(bumpFor(["bug", "area:api"]), "patch");
  assert.equal(bumpFor([MINOR_LABEL]), "minor");
  assert.equal(bumpFor([MAJOR_LABEL]), "major");
  assert.equal(bumpFor(["Release:Minor"]), "minor");
  assert.equal(bumpFor([" release:major "]), "major");
});

test("bumpFor takes the LARGER bump when both labels are present", () => {
  assert.equal(bumpFor([MINOR_LABEL, MAJOR_LABEL]), "major");
  assert.equal(bumpFor([MAJOR_LABEL, MINOR_LABEL]), "major");
});

test("a label that merely contains a release word does not bump", () => {
  // Guards against a substring match: these are not the labels.
  assert.equal(bumpFor(["release"]), "patch");
  assert.equal(bumpFor(["release:majority"]), "patch");
  assert.equal(bumpFor(["prerelease:major"]), "patch");
});

test("applyBump zeroes the lower fields", () => {
  const v = { major: 1, minor: 4, patch: 7 };
  assert.equal(formatVersion(applyBump(v, "major")), "v2.0.0");
  assert.equal(formatVersion(applyBump(v, "minor")), "v1.5.0");
  assert.equal(formatVersion(applyBump(v, "patch")), "v1.4.8");
  assert.throws(() => applyBump(v, "nonsense"), /unknown bump/);
});

test("the first release is INITIAL_VERSION regardless of labels", () => {
  // A release:minor on the very first merge must NOT skip to v0.2.0 — there is
  // no previous version to bump from.
  for (const labels of [[], [MINOR_LABEL], [MAJOR_LABEL]]) {
    const r = nextVersion({ tags: [], labels });
    assert.equal(r.version, INITIAL_VERSION);
    assert.equal(r.previous, null);
  }
});

test("subsequent releases bump from the highest existing version", () => {
  const tags = ["v0.1.0", "v0.2.0", "v0.10.0", "v0.9.5", "nightly"];

  assert.deepEqual(nextVersion({ tags, labels: [] }), {
    version: "v0.10.1",
    bump: "patch",
    previous: "v0.10.0",
  });
  assert.deepEqual(nextVersion({ tags, labels: [MINOR_LABEL] }), {
    version: "v0.11.0",
    bump: "minor",
    previous: "v0.10.0",
  });
  assert.deepEqual(nextVersion({ tags, labels: [MAJOR_LABEL] }), {
    version: "v1.0.0",
    bump: "major",
    previous: "v0.10.0",
  });
});

test("the issued version is strictly greater than every existing release tag", () => {
  // The property that makes a same-run collision impossible: counting up from
  // the HIGHEST tag can never land on one that already exists. A version cut by
  // hand out of band (v0.2.0 below, not the highest) must therefore still be
  // stepped over, not reissued.
  const tags = ["v0.1.0", "v0.2.0", "v0.10.0", "v0.9.5"];
  const existing = tags.map(parseVersion);

  for (const labels of [[], [MINOR_LABEL], [MAJOR_LABEL]]) {
    const { version } = nextVersion({ tags, labels });
    assert.ok(!tags.includes(version), `${version} collides with an existing tag`);

    const issued = parseVersion(version);
    assert.notEqual(issued, null, `${version} must itself be a valid release tag`);
    for (const prior of existing) {
      const greater =
        issued.major > prior.major ||
        (issued.major === prior.major && issued.minor > prior.minor) ||
        (issued.major === prior.major &&
          issued.minor === prior.minor &&
          issued.patch > prior.patch);
      assert.ok(greater, `${version} must outrank ${formatVersion(prior)}`);
    }
  }
});

test("parseArgs reads a comma-separated --labels value", () => {
  assert.deepEqual(parseArgs([]), { labels: [] });
  assert.deepEqual(parseArgs(["--labels", ""]), { labels: [] });
  assert.deepEqual(parseArgs(["--labels", "release:minor"]), { labels: ["release:minor"] });
  assert.deepEqual(parseArgs(["--labels", "bug, release:major ,slice"]), {
    labels: ["bug", "release:major", "slice"],
  });
  assert.throws(() => parseArgs(["--labels"]), /requires a value/);
});
