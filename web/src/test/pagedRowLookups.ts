// #557 — a lint over the screen tests, guarding the one combination that made
// the suite flaky on CI: a PAGE-SIZE fixture (as many rows as a screen's pager
// needs before it offers "load more") queried through `ByRole("row", { name })`.
//
// That query computes the accessible name of every row in the document, and
// `findBy*` re-runs it on every DOM mutation, so a full page of rows spends
// seconds walking the tree and intermittently overran the 5000ms default on a
// loaded runner — three CI failures across two files before anyone read it as
// one bug. `findRowByCellText`/`getRowByCellText` in ./rows resolve the same row
// from a single cell's text instead.
//
// #637 — the page size is READ FROM THE SCREEN, not assumed. The first version
// scanned for a literal `100`, which was already wrong when it was written:
// seven screens page at 50 (History, Feed, Sales, Water, the Flocks and Stock
// ledgers, the entity picker), so the lint was silently blind on every one of
// them. A lint that reads as coverage while covering nothing is worse than no
// lint, so the size now comes from the same constant the screen passes to
// `usePagedList`, and a screen that changes its page size re-aims the lint by
// doing so.
//
// Scope, stated honestly and no wider than it is: this is still SOURCE
// ANALYSIS, not an evaluation of what a test mounts at runtime. It resolves
// integer literals and file-local integer constants; it does not evaluate
// expressions, follow imports, or know what a helper returns. What it now
// covers that it did not: any page size (not just 100), a fixture length given
// as a file-local constant, and a fixture built outside the `it(...)` body —
// in a `beforeEach`, a `describe` prelude, or the module scope. It remains a
// ratchet on a known-expensive pattern rather than a proof that no expensive
// lookup exists. Outside a page-size fixture the role query is the RIGHT
// spelling and is deliberately left alone.

/** `const NAME = 123;` — the only constant form this resolves, on purpose. */
const INT_CONST = /(?:^|\n)\s*const\s+([A-Za-z_$][\w$]*)\s*=\s*(\d+)\s*;/g;

export function intConstants(source: string): Map<string, number> {
  const found = new Map<string, number>();
  for (const [, name, value] of source.matchAll(INT_CONST)) found.set(name, Number(value));
  return found;
}

// Every `pageSize:` a screen hands to `usePagedList`, resolved through that
// file's own constants. A screen with two paged lists contributes both sizes.
// A `pageSize` this cannot resolve — an import, an expression, a prop — is
// deliberately NOT silently dropped: `pageSizes` returns it as `null` and the
// coverage test below fails on it, so the lint cannot go quiet by failing to
// understand a screen.
export function pageSizes(source: string): (number | null)[] {
  const constants = intConstants(source);
  return [...source.matchAll(/pageSize:\s*([A-Za-z_$][\w$]*|\d+)/g)].map(([, token]) =>
    /^\d+$/.test(token) ? Number(token) : constants.get(token) ?? null,
  );
}

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
export const rowByAccessibleName = /ByRole\(\s*["']row["']\s*,\s*\{[^}]*\bname\s*[:,}]/;

// A fixture as long as the screen's own page. The SIZE is now whatever the
// screen pages at, and the constant indirection `customerPage(PAGE_SIZE)` is
// resolved — those were #637's first blind spots. In exchange the SHAPE had to
// narrow, and that trade is the whole design of this function:
//
// The first version matched any call taking a bare `100`. That was survivable
// only because 100 is a rare magic number in a test. Once the threshold is each
// screen's real page size — 50 on seven of them — "any call taking a number
// this big" matches dates, quantities, money and timeouts. Measured, not
// feared: broadened that way, one stray `(90)` in HistoryPage's prelude charged
// a page-size fixture to all 17 of its row-name tests. A lint that cries wolf
// gets deleted, so the shapes are now explicit:
//
//   * `Array.from({ length: N }` / `Array(N)` / `.fill(…)` with a length —
//     building N elements is what makes a fixture expensive, and it is the one
//     thing every fixture must do;
//   * `somethingPage(N)` / `someRows(N)` — this suite's own builder convention
//     (`customerPage`, `invMovementPage`, `auditRows`).
//
// The builder-name suffix is the one heuristic here, and it is the piece that
// goes quiet if someone names a builder outside the convention — the honest
// residue, narrower than what it replaces and disclosed rather than implied.
//
// `>=` rather than `===`: a fixture LONGER than a page is at least as expensive
// as one exactly a page long, and pinning equality would let `customerPage(101)`
// through.
const FIXTURE_LENGTH = new RegExp([
  String.raw`length:\s*([A-Za-z_$][\w$]*|\d+)`,
  String.raw`\bArray\s*\(\s*([A-Za-z_$][\w$]*|\d+)\s*\)`,
  String.raw`\b[A-Za-z_$][\w$]*(?:Page|Rows|Items|Entries)\s*\(\s*([A-Za-z_$][\w$]*|\d+)\s*\)`,
].join("|"), "g");

export function mountsAPageSizeFixture(body: string, sizes: number[], constants: Map<string, number>): boolean {
  if (sizes.length === 0) return false;
  const smallestPage = Math.min(...sizes);
  return [...body.matchAll(FIXTURE_LENGTH)]
    .map((match) => match.slice(1).find((group) => group !== undefined))
    .map((token) => (token === undefined ? null : /^\d+$/.test(token) ? Number(token) : constants.get(token) ?? null))
    .some((value) => value !== null && value >= smallestPage);
}

// Slice a test file into one entry per test, each ending where the next test or
// describe begins, so a fixture declared between blocks is not charged to the
// test above it. `it.each([...])` counts as one block: its cases share a body,
// and so share the fixture and the lookups.
export function testBlocks(source: string): { title: string; body: string }[] {
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

// Everything that is NOT inside a test body: module scope, `describe` preludes,
// `beforeEach`. A fixture built there is mounted by every test in the file, and
// charging it to none of them was #637's second named blind spot. Taken as one
// slab rather than scoped per `describe` — the cheap over-approximation, and
// the honest direction to err in for a ratchet.
export function sharedPrelude(source: string, blocks: { body: string }[]): string {
  return blocks.reduce((rest, block) => rest.replace(block.body, ""), source);
}
