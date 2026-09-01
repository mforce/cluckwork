import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";

// #557 — a lint over the screen tests, guarding the one combination that made
// the suite flaky on CI: a PAGE-SIZE fixture (100 rows, the length a screen's
// pager needs before it offers "load more") queried through
// `ByRole("row", { name })`.
//
// That query computes the accessible name of every row in the document, and
// `findBy*` re-runs it on every DOM mutation, so a 100-row paged load spends
// seconds walking the tree and intermittently overran the 5000ms default on a
// loaded runner — three CI failures across two files before anyone read it as
// one bug. `findRowByCellText`/`getRowByCellText` in ./rows resolve the same row
// from a single cell's text instead.
//
// Scope, stated honestly: this is a TEXT SCAN of test sources, not an
// evaluation. It catches the shape that actually failed — a literal 100-row
// fixture and a row-role-with-name query inside the same test — and it will not
// catch a fixture built behind a helper in a `beforeEach`, or a page size that
// stops being 100. It is a ratchet on a known-expensive pattern, not a proof
// that no expensive lookup exists. Outside a page-size fixture the role query is
// the RIGHT spelling and is deliberately left alone.

const routesDir = resolve(process.cwd(), "src/routes");

// A fixture long enough to make a screen's pager appear: `customerPage(100)`,
// `invMovementPage(100)`, `Array.from({ length: 100 }, …)`.
//
// Deliberately broad, minus two spellings that can never be a fixture:
// `toHaveLength(100)` and `advanceTimersByTime(100)`, which are an assertion
// and a clock nudge. Anything else taking a bare 100 is treated as a possible
// page-size fixture. The bias is on purpose — a false positive here costs a
// reader half a minute, a false negative costs a returned flake — so this does
// NOT narrow to a list of known fixture-builder names, which would go quiet the
// day someone adds one under a new name.
const pageSizeFixture = /(?<!Length)(?<!Time)\(\s*100\s*\)|length:\s*100\b/;

// `getByRole("row", { name: … })` and every relative of it — get/find/query,
// singular or All. No leading \b: the character before `ByRole` is always a
// word character (`find`, `get`), so a word boundary there never matches.
//
// `name` is matched ANYWHERE in the options object, not just as its first key:
// `{ exact: true, name: /…/ }` costs exactly as much as `{ name: /…/ }` and an
// option-order-sensitive pattern would wave it through. Both quote styles, for
// the same reason. `[^}]*` cannot run past the object's own closing brace, so a
// `name:` belonging to some later expression is not picked up.
//
// The trailing `[:,}]` also accepts the SHORTHAND `{ name }`, which is not
// hypothetical — six call sites in this suite already pass an accessible name
// that way for `button`, `dialog` and `option` roles, so a row query written in
// the house style would otherwise slip straight through. `{ nameish: 1 }` is
// still not matched: the character after `name` has to be one of `:`, `,` or `}`.
//
// `name` is NOT allowed to be quoted here. Accepting a `{ "name": … }` key —
// a spelling this codebase never uses — also made `{ description: "name" }`
// match, because the closing quote of the VALUE then satisfied the key's
// optional quote. The narrower pattern is the correct one: it costs a spelling
// nobody writes and buys back a false positive.
const rowByAccessibleName = /ByRole\(\s*["']row["']\s*,\s*\{[^}]*\bname\s*[:,}]/;

// Slice a test file into one entry per test, each ending where the next test or
// describe begins, so a fixture declared between blocks is not charged to the
// test above it. `it.each([...])` counts as one block: its cases share a body,
// and so share the fixture and the lookups.
function testBlocks(source: string): { title: string; body: string }[] {
  const opener = /^[ \t]*(?:it|test)(?:\.\w+)*\(/gm;
  const boundary = /^[ \t]*(?:it|test|describe)(?:\.\w+)*\(/gm;
  const blocks: { title: string; body: string }[] = [];

  for (const match of source.matchAll(opener)) {
    const start = match.index;
    boundary.lastIndex = start + match[0].length;
    const next = boundary.exec(source);
    const body = source.slice(start, next?.index ?? source.length);
    // For a plain `it("…")` the title is the block's first quoted string. For
    // `it.each([…])("…")` it is NOT: the first quoted string is the first case
    // in the table, so that spelling has to skip past the array and take the
    // string after the closing `)(`. Getting this wrong is invisible until the
    // day the lint fires, and then it names the wrong test.
    const isEach = match[0].includes(".each");
    const title = (isEach
      ? /\)\s*\(\s*"((?:[^"\\]|\\.)*)"/.exec(body)?.[1]
      : /"((?:[^"\\]|\\.)*)"/.exec(body)?.[1])
      ?? `line ${source.slice(0, start).split("\n").length}`;
    blocks.push({ title, body });
  }
  return blocks;
}

const offenders = readdirSync(routesDir)
  .filter((file) => file.endsWith(".test.tsx"))
  .flatMap((file) =>
    testBlocks(readFileSync(resolve(routesDir, file), "utf8"))
      .filter((block) => pageSizeFixture.test(block.body) && rowByAccessibleName.test(block.body))
      .map((block) => `${file} › ${block.title}`),
  );

describe("paged-list row lookups (#557)", () => {
  it("resolves rows by cell text in every test that mounts a page-size fixture", () => {
    expect(offenders).toEqual([]);
  });
});
