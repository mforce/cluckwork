import { describe, expect, it } from "vitest";
import {
  intConstants, mountsAPageSizeFixture, pageSizes, rowByAccessibleName, sharedPrelude, testBlocks,
} from "./pagedRowLookups";

// #637 — the lint's own behaviour, pinned against synthetic sources.
//
// The lint in ./pagedRowLookups.test.ts runs over the real tree, and once its
// offenders are fixed that tree exercises none of the paths that make the lint
// worth having: nothing there has an unresolvable page size, a fixture behind a
// constant, or a prelude-built fixture. Measured rather than assumed — with
// only that file, four of five mutations to the detector stayed green:
// hardcoding the threshold back to 100, dropping constant resolution, dropping
// prelude attribution, and swallowing an unresolvable `pageSize`. A lint whose
// rules are unpinned is the same "reads as coverage" failure #637 was filed
// about, one level up.
//
// So each case below is one CLAIM the lint's comment makes, written so that
// removing the rule turns exactly this test red.

const PAGE_50_SCREEN = `
const PAGE = 50;
export function FeedPage() {
  const rows = usePagedList({ fetchPage, pageSize: PAGE });
}`;

describe("pageSizes — the threshold comes from the screen", () => {
  it("resolves a page size given as a file-local constant", () => {
    expect(pageSizes(PAGE_50_SCREEN)).toEqual([50]);
  });

  it("resolves a page size written as a literal", () => {
    expect(pageSizes("usePagedList({ pageSize: 100 })")).toEqual([100]);
  });

  it("reports every paged list on a screen that has more than one", () => {
    expect(pageSizes(`
      const LOT_PAGE = 50;
      const LEDGER_PAGE = 100;
      usePagedList({ pageSize: LOT_PAGE });
      usePagedList({ pageSize: LEDGER_PAGE });
    `)).toEqual([50, 100]);
  });

  it("reports a page size it cannot resolve as null rather than dropping it", () => {
    // The failure mode this exists to prevent: a screen the lint quietly stops
    // covering because it did not understand the expression.
    expect(pageSizes("usePagedList({ pageSize: props.pageSize })")).toEqual([null]);
    expect(pageSizes("usePagedList({ pageSize: IMPORTED_PAGE })")).toEqual([null]);
  });

  it("finds nothing on a screen that does not page", () => {
    expect(pageSizes("export function StaticPage() { return null; }")).toEqual([]);
  });
});

describe("intConstants", () => {
  it("reads integer constants and ignores everything else", () => {
    const found = intConstants(`
      const PAGE = 50;
      const NAME = "fifty";
      const DERIVED = PAGE * 2;
    `);
    expect(found.get("PAGE")).toBe(50);
    expect(found.has("NAME")).toBe(false);
    expect(found.has("DERIVED")).toBe(false);
  });
});

describe("mountsAPageSizeFixture — a fixture as long as the screen's page", () => {
  const noConstants = new Map<string, number>();

  it("catches a 50-row fixture on a screen that pages at 50", () => {
    // The whole point of #637: the literal-100 scan was blind to this, and
    // seven screens page at 50.
    expect(mountsAPageSizeFixture("Array.from({ length: 50 }, mk)", [50], noConstants)).toBe(true);
  });

  it("does not charge a 50-row fixture to a screen that pages at 100", () => {
    expect(mountsAPageSizeFixture("Array.from({ length: 50 }, mk)", [100], noConstants)).toBe(false);
  });

  it("counts a fixture LONGER than the page", () => {
    expect(mountsAPageSizeFixture("customerPage(101)", [100], noConstants)).toBe(true);
  });

  it("resolves a fixture length given as a constant", () => {
    // `customerPage(PAGE_SIZE)` — #637's first named blind spot.
    expect(mountsAPageSizeFixture("customerPage(PAGE_SIZE)", [100], new Map([["PAGE_SIZE", 100]])))
      .toBe(true);
  });

  it("recognises the builder spellings this suite actually uses", () => {
    expect(mountsAPageSizeFixture("movementPage(50)", [50], noConstants)).toBe(true);
    expect(mountsAPageSizeFixture("auditRows(50)", [50], noConstants)).toBe(true);
    expect(mountsAPageSizeFixture("Array(50).fill(row)", [50], noConstants)).toBe(true);
  });

  it("ignores the numeric spellings that can never be a fixture", () => {
    // The reason the shape had to narrow when the threshold widened: at a
    // threshold of 50 these are all "big enough", and treating them as
    // fixtures charged one stray number to 17 unrelated tests.
    expect(mountsAPageSizeFixture("expect(rows).toHaveLength(50)", [50], noConstants)).toBe(false);
    expect(mountsAPageSizeFixture("vi.advanceTimersByTime(90)", [50], noConstants)).toBe(false);
    expect(mountsAPageSizeFixture("formatMoney(9900)", [50], noConstants)).toBe(false);
    expect(mountsAPageSizeFixture("await waitFor(cb, { timeout: 3000 })", [50], noConstants)).toBe(false);
  });

  it("finds nothing when the screen does not page", () => {
    expect(mountsAPageSizeFixture("Array.from({ length: 500 }, mk)", [], noConstants)).toBe(false);
  });
});

describe("rowByAccessibleName — the expensive query", () => {
  it.each([
    ['screen.getByRole("row", { name: /x/ })'],
    ["screen.findByRole('row', { name: 'x' })"],
    ['screen.queryAllByRole("row", { exact: true, name: /x/ })'],
    ['within(t).getByRole("row", { name })'],
  ])("matches %s", (query) => {
    expect(rowByAccessibleName.test(query)).toBe(true);
  });

  it.each([
    ['screen.getByRole("row")'],
    ['screen.getByRole("button", { name: /x/ })'],
    ['screen.getByRole("row", { nameish: 1 })'],
    ['screen.getByRole("row", { description: "name" })'],
  ])("leaves %s alone", (query) => {
    expect(rowByAccessibleName.test(query)).toBe(false);
  });
});

describe("testBlocks and sharedPrelude — where a fixture is charged", () => {
  const FILE = `
const shared = customerPage(100);
describe("group", () => {
  beforeEach(() => { mockList.mockResolvedValue(customerPage(100)); });
  it("first", () => { screen.getByRole("row", { name: /a/ }); });
  it("second", () => { expect(1).toBe(1); });
});`;

  it("names each test and does not run one block into the next", () => {
    expect(testBlocks(FILE).map((block) => block.title)).toEqual(["first", "second"]);
    expect(testBlocks(FILE)[1].body).not.toContain("getByRole");
  });

  it("titles an it.each block from the string after the table, not the first case", () => {
    const blocks = testBlocks(`
      it.each([["alpha"], ["beta"]])("handles %s", (x) => { expect(x).toBeTruthy(); });
    `);
    expect(blocks.map((block) => block.title)).toEqual(["handles %s"]);
  });

  it("leaves the module scope and the beforeEach in the prelude", () => {
    // #637's second named blind spot: a fixture built here is mounted by every
    // test in the file, and charging it to none of them is how the lint went
    // quiet on a `beforeEach`.
    const prelude = sharedPrelude(FILE, testBlocks(FILE));
    expect(prelude).toContain("const shared = customerPage(100)");
    expect(prelude).toContain("beforeEach");
    expect(prelude).not.toContain('it("first"');
    expect(mountsAPageSizeFixture(prelude, [100], new Map())).toBe(true);
  });
});
