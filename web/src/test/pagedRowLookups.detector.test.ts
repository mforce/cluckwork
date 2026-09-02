import { describe, expect, it } from "vitest";
import {
  comparableSizes, intConstants, mountsAPageSizeFixture, pageSizes, rowByAccessibleName,
  sharedPrelude, testBlocks,
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

  it("ignores a pageSize that is not usePagedList's", () => {
    // An unrelated `{ pageSize: 50 }` — a prop, a request body, a config —
    // would otherwise lower the threshold for a list that pages at 100, or
    // fail the unresolved assertion for a property this lint has no business
    // reading (CodeRabbit review of #647).
    expect(pageSizes(`
      const PAGE = 100;
      fetchThings({ pageSize: 50 });
      const rows = usePagedList({ fetchPage, pageSize: PAGE });
    `)).toEqual([100]);
    expect(pageSizes("fetchThings({ pageSize: whateverTheCallerPassed });")).toEqual([]);
  });

  it("reads a page size through nested calls in the options object", () => {
    // The real shape on ExpensesPage: the options object nests three calls deep
    // before naming its page size, so a window that ends at the first `})`
    // stops before reaching it.
    expect(pageSizes(`
      const PAGE = 100;
      const rows = usePagedList<Expense, Meta>({
        fetchPage: useCallback(async (offset, limit) => {
          const list = await listExpenses({ from, to, limit, offset });
          return { items: list.items, meta: list.meta };
        }, [month]),
        pageSize: PAGE,
      });
    `)).toEqual([100]);
  });

  it("takes the hook's own pageSize, not a nested request's", () => {
    // The first textual `pageSize:` inside the call is not necessarily the
    // hook's: an option's value can be a call with its own paging argument, and
    // reading that one aims the lint at half the real threshold (CodeRabbit
    // review of #647, against the previous round's fix).
    expect(pageSizes(`
      usePagedList({
        fetchPage: (offset, limit) => listThings({ pageSize: 50, offset, limit }),
        pageSize: 100,
      });
    `)).toEqual([100]);
  });

  it("does not read a nested pageSize when the hook names none", () => {
    // Unresolved, not "50": a screen that pages must contribute a size, and
    // guessing one from a nested call is how the wrong threshold gets in.
    expect(pageSizes("usePagedList({ fetchPage: () => listThings({ pageSize: 50 }) });"))
      .toEqual([null]);
  });

  it("does not read the import statement as a call site", () => {
    expect(pageSizes(`
      import { usePagedList } from "../components/usePagedList";
      export function StaticPage() { return null; }
    `)).toEqual([]);
  });

  it("finds nothing on a screen that does not page", () => {
    expect(pageSizes("export function StaticPage() { return null; }")).toEqual([]);
  });
});

describe("intConstants", () => {
  it("refuses a name declared twice with different values, rather than taking the last", () => {
    // This scan has no notion of scope, and `DailyEntryPage` already declares a
    // `const PAGE` inside a function body. Taking the last one seen would
    // resolve a `pageSize:` to a number nobody chose — silently wrong, which is
    // worse than unresolved, because unresolved fails the coverage test loudly.
    // Shaped like the real thing: `DailyEntryPage` declares its inner `PAGE` on
    // its own indented line, which is exactly what this line-anchored scan sees.
    // (An inline `{ const PAGE = 999; }` is invisible to it — a narrower reach
    // than the finding assumed, and worth knowing rather than papering over.)
    const found = intConstants("const PAGE = 50;\nfunction inner() {\n    const PAGE = 999;\n}");
    expect(found.has("PAGE")).toBe(false);
    expect(pageSizes("const PAGE = 50;\nusePagedList({ pageSize: PAGE });\nfunction f() {\n  const PAGE = 999;\n}"))
      .toEqual([null]);
  });

  it("keeps a name repeated with the SAME value", () => {
    expect(intConstants("const PAGE = 50;\nconst PAGE = 50;").get("PAGE")).toBe(50);
  });

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
    // The whole point of #637: the literal-100 scan was blind to this, and five
    // of the nine paged screens page at 50.
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

  it("ignores a length property outside an Array.from fixture", () => {
    // `mockResult({ length: 50 })` describes a response, not fifty rows, and
    // counting it made an unrelated test with a row query fail the lint
    // (CodeRabbit review of #647). Summing made this worse, not better.
    expect(mountsAPageSizeFixture("mockResult({ length: 50 })", [50], noConstants)).toBe(false);
    expect(mountsAPageSizeFixture("expect(page).toMatchObject({ length: 50 })", [50], noConstants))
      .toBe(false);
  });

  it("counts a fixture assembled from pieces", () => {
    // `[...batch(25), ...batch(25)]` mounts exactly as many rows as one
    // 50-row builder call, and costs the accessible-name walk exactly as much.
    expect(mountsAPageSizeFixture(
      "const rows = [...Array.from({ length: 25 }, mk), ...Array.from({ length: 25 }, mk)];",
      [50], noConstants,
    )).toBe(true);
  });

  it("measures against the SMALLEST page on a screen with two paged lists", () => {
    // A screen paging a 50-row list beside a 100-row one still mounts an
    // expensive table at 50; measuring against the larger would suppress it.
    expect(mountsAPageSizeFixture("lotPage(50)", [50, 100], noConstants)).toBe(true);
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

describe("comparableSizes — what a fixture can be measured against", () => {
  it("drops the sizes it could not resolve and keeps the rest", () => {
    // Unresolved is reported by the lint's coverage test, not silently treated
    // as "this screen does not page" — but it cannot be compared against.
    expect(comparableSizes([50, null, 100])).toEqual([50, 100]);
  });

  it("leaves nothing to measure when NO size resolved", () => {
    expect(comparableSizes([null, null])).toEqual([]);
    expect(mountsAPageSizeFixture("customerPage(100)", comparableSizes([null]), new Map()))
      .toBe(false);
  });
});
