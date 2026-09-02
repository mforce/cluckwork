import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import {
  comparableSizes, intConstants, mountsAPageSizeFixture, pageSizes, rowByAccessibleName,
  sharedPrelude, testBlocks,
} from "./pagedRowLookups";

// The lint itself. Its rules, and the reasoning behind each one, live in
// ./pagedRowLookups.ts beside the code that implements them; the detector's own
// behaviour is pinned in ./pagedRowLookups.detector.test.ts against synthetic
// sources, because this file cannot pin it — it runs against the real tree, and
// a real tree with no offenders left exercises none of the interesting paths.
// That was measured: before those unit tests existed, four of five mutations to
// the detector (hardcoding the threshold back to 100, dropping constant
// resolution, dropping prelude attribution, swallowing an unresolvable
// pageSize) left this file green.

const routesDir = resolve(process.cwd(), "src/routes");

const screenSources = readdirSync(routesDir)
  .filter((file) => file.endsWith(".tsx") && !file.endsWith(".test.tsx"))
  .map((file) => ({ file, source: readFileSync(resolve(routesDir, file), "utf8") }));

const pagedScreens = screenSources
  .map(({ file, source }) => ({ file, sizes: pageSizes(source) }))
  .filter(({ sizes }) => sizes.length > 0);

const offenders = pagedScreens.flatMap(({ file, sizes }) => {
  const testFile = file.replace(/\.tsx$/, ".test.tsx");
  let testSource: string;
  try {
    testSource = readFileSync(resolve(routesDir, testFile), "utf8");
  } catch {
    return [];
  }
  const resolved = comparableSizes(sizes);
  const constants = intConstants(testSource);
  const blocks = testBlocks(testSource);
  const prelude = sharedPrelude(testSource, blocks);
  const preludeMounts = mountsAPageSizeFixture(prelude, resolved, constants);

  return blocks
    .filter((block) =>
      rowByAccessibleName.test(block.body)
      && (preludeMounts || mountsAPageSizeFixture(block.body, resolved, constants)))
    .map((block) => `${testFile} › ${block.title}`);
});

describe("paged-list row lookups (#557)", () => {
  it("resolves rows by cell text in every test that mounts a page-size fixture", () => {
    expect(offenders).toEqual([]);
  });

  // The lint aims itself at each screen's own page size, so a `pageSize:` it
  // cannot resolve is a screen it silently stops covering. That failure is
  // invisible in the assertion above — it just finds fewer offenders — so it
  // gets its own red instead (#637).
  it("resolves the page size of every screen that pages", () => {
    const unresolved = screenSources
      .filter(({ source }) => pageSizes(source).some((size) => size === null))
      .map(({ file }) => file);
    expect(unresolved).toEqual([]);
  });

  // A lint over an empty set passes forever. This is the boring guard on the
  // guard: the walk must still be FINDING the paged screens it is written for,
  // and must still be seeing the 50-row page sizes the literal-100 version was
  // blind to.
  it("still finds the paged screens it lints, at both page sizes in use", () => {
    expect(pagedScreens.length).toBeGreaterThanOrEqual(8);
    expect(pagedScreens.flatMap(({ sizes }) => sizes)).toContain(50);
    expect(pagedScreens.flatMap(({ sizes }) => sizes)).toContain(100);
  });
});
