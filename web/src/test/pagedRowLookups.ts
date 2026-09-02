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
// scanned for a literal `100`, which was already wrong when it was written.
// Counted rather than recalled (a first draft of this comment claimed seven
// screens and named two that do not page through this idiom at all): nine route
// files hand a `pageSize` to `usePagedList` — FIVE at 50 (Feed, Flocks,
// History, Sales, Water) and four at 100 (Audit, Customers, Expenses,
// Inventory). The lint was blind on all five of the 50s while reading as a
// guard over every one of them. The size now comes from the same constant the
// screen passes to `usePagedList`, so a screen re-aims the lint by changing its
// page size.
//
// Two things this walk structurally CANNOT see, named so nobody reads its green
// as wider than it is:
//   * `src/components/` — `NamedEntityPicker` pages at 50 through its own code
//     and renders `role="option"`, so it is neither scanned nor matched. Extend
//     the walk if a component ever renders a paged TABLE.
//   * pagination that is not `usePagedList` — `DailyEntryPage`'s usage-drain
//     loop pages with a bare `limit:`, so it contributes no size at all, not
//     even an unresolved one. The coverage test below catches a `pageSize:` it
//     cannot resolve; it cannot catch a screen that never writes one.
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

// Comments and string/template literals, blanked out — same length, so every
// index still lines up with the original text.
//
// Everything below reads SOURCE, and source contains prose that looks exactly
// like code: `usePagedList({ // pageSize: 50\n pageSize: 100 })` handed the
// scanner the commented-out number, and a `"pageSize: 50"` literal did the
// same, silently aiming the lint at half the screen's real threshold
// (CodeRabbit review of #647). A commented-out `const PAGE = 999;` poisoned
// constant resolution the same way. Blanking the two states that can hold
// look-alike text kills the class rather than the two spellings reported.
//
// This also removes the caveat `callArguments` used to carry: a stray
// parenthesis inside a string can no longer end an options window early.
function maskProseAndLiterals(source: string): string {
  const out = source.split("");
  let state: "code" | "line" | "block" | '"' | "'" | "`" = "code";
  for (let i = 0; i < source.length; i += 1) {
    const char = source[i];
    const next = source[i + 1];
    if (state === "code") {
      if (char === "/" && next === "/") { state = "line"; out[i] = " "; }
      else if (char === "/" && next === "*") { state = "block"; out[i] = " "; }
      else if (char === '"' || char === "'" || char === "`") state = char;
      continue;
    }
    if (state === "line") {
      if (char === "\n") state = "code";
      else out[i] = " ";
      continue;
    }
    if (state === "block") {
      if (char === "*" && next === "/") { out[i] = " "; out[i + 1] = " "; i += 1; state = "code"; }
      else if (char !== "\n") out[i] = " ";
      continue;
    }
    // Inside a string or template literal.
    if (char === "\\") { out[i] = " "; out[i + 1] = " "; i += 1; continue; }
    if (char === state) state = "code";
    else if (char !== "\n") out[i] = " ";
  }
  return out.join("");
}

/** `const NAME = 123;` — the only constant form this resolves, on purpose. */
const INT_CONST = /(?:^|\n)\s*const\s+([A-Za-z_$][\w$]*)\s*=\s*(\d+)\s*;/g;

export // A name declared twice is AMBIGUOUS, and ambiguous must not silently become
// the last one seen. This scan has no notion of scope — `DailyEntryPage`
// already declares a `const PAGE` inside a function body — so a same-named
// constant elsewhere in the file would otherwise resolve a `pageSize:` to a
// number that is simply wrong. Wrong is worse than unresolved here: unresolved
// fails the coverage test loudly, wrong just re-aims the lint at a threshold
// nobody chose. Dropping the name makes it unresolvable, which is the honest
// answer a scope-blind reader can give (sonnet review of #647).
function intConstants(source: string): Map<string, number> {
  const found = new Map<string, number>();
  const ambiguous = new Set<string>();
  for (const [, name, value] of source.matchAll(INT_CONST)) {
    if (found.has(name) && found.get(name) !== Number(value)) ambiguous.add(name);
    found.set(name, Number(value));
  }
  for (const name of ambiguous) found.delete(name);
  return found;
}

// The text between a call's own parentheses, found by BALANCING them rather
// than by looking for a closing line. `usePagedList({ fetchPage: useCallback(
// async () => { … }) , pageSize: PAGE })` nests three calls deep before it
// names its page size, and a window that stopped at the first `})` ended before
// reaching it — silently, which the coverage test then reported as five
// unresolved screens. Strings and comments are not parsed: a stray unbalanced
// parenthesis inside one would end the window early, and the failure is a
// screen reported as unresolved rather than one silently mis-read.
function callArguments(source: string, at: number): string | null {
  const open = source.indexOf("(", at);
  if (open === -1) return null;
  // The identifier has to BE a call. `import { usePagedList } from "…"` also
  // mentions the name — twice, counting the module path — and its next `(`
  // belongs to some unrelated function further down the file, which would be
  // read as a call whose options carry no page size (CodeRabbit's finding,
  // one layer deeper than where it landed). Only a type argument may sit
  // between the name and its parenthesis.
  if (!/^\s*(?:<[\s\S]*?>)?\s*$/.test(source.slice(at, open))) return null;
  let depth = 0;
  for (let i = open; i < source.length; i += 1) {
    if (source[i] === "(") depth += 1;
    else if (source[i] === ")") {
      depth -= 1;
      if (depth === 0) return source.slice(open + 1, i);
    }
  }
  return null;
}

// A property of the OPTIONS OBJECT ITSELF, not of anything nested inside it.
// Taking the first textual `pageSize:` in the call's arguments reads
// `usePagedList({ fetchPage: () => list({ pageSize: 50 }), pageSize: 100 })` as
// a screen that pages at 50 — the request's page size, not the hook's — and
// aims the lint at a threshold half the real one (CodeRabbit review of #647,
// against the previous round's own fix).
//
// Only BRACES are counted. A first draft also counted `()` and `[]`, on the
// reasoning that an option's value can be an arrow function or an array — but
// every such value opens a brace before it can contain a `key:` property, so
// the extra counting changed no outcome and a mutation removing it stayed
// green. Prose asserting a safeguard nothing enforces is the thing this file
// exists to object to, so the claim went with the code.
function topLevelProperty(options: string, key: string): string | null {
  const open = options.indexOf("{");
  if (open === -1) return null;
  const property = new RegExp(`^${key}:\\s*([A-Za-z_$][\\w$]*|\\d+)`);
  let depth = 0;
  for (let i = open; i < options.length; i += 1) {
    const char = options[i];
    if (char === "{") depth += 1;
    else if (char === "}") {
      depth -= 1;
      if (depth === 0) return null;
    } else if (depth === 1 && !/[\w$]/.test(options[i - 1] ?? "")) {
      const found = property.exec(options.slice(i));
      if (found !== null) return found[1];
    }
  }
  return null;
}

// The sizes a screen's fixtures are measured against: the resolved ones only.
// An unresolved `pageSize` must NOT be quietly treated as "no page size" — that
// is what the coverage test is for — but neither can it be compared against, so
// it is dropped here and reported there. Lived in the lint file until the
// sonnet review of #647 pointed out that nothing pinned it.
export function comparableSizes(sizes: (number | null)[]): number[] {
  return sizes.filter((size): size is number => size !== null);
}

// Every `pageSize:` a screen hands to `usePagedList`, resolved through that
// file's own constants. A screen with two paged lists contributes both sizes.
// A `pageSize` this cannot resolve — an import, an expression, a prop — is
// deliberately NOT silently dropped: `pageSizes` returns it as `null` and the
// coverage test below fails on it, so the lint cannot go quiet by failing to
// understand a screen.
export function pageSizes(rawSource: string): (number | null)[] {
  const source = maskProseAndLiterals(rawSource);
  const constants = intConstants(source);
  // Scoped to the CALL, not to the file. A bare scan for `pageSize:` anywhere
  // in the source would let an unrelated `{ pageSize: 50 }` — a prop, a request
  // body, a config object — lower the threshold for a screen whose list
  // actually pages at 100, or fail the unresolved-size assertion for a property
  // this lint has no business reading (CodeRabbit review of #647).
  //
  // The window runs from the call to the line that closes it. A call whose
  // options this cannot read yields `null` rather than nothing: a screen that
  // pages MUST contribute a size, and "found no pageSize inside usePagedList("
  // is exactly the unresolved case the coverage test exists to shout about.
  return [...source.matchAll(/\busePagedList\b/g)]
    .map((call) => callArguments(source, call.index + call[0].length))
    .filter((options): options is string => options !== null)
    .map((options) => {
      const token = topLevelProperty(options, "pageSize");
      if (token === null) return null;
      return /^\d+$/.test(token) ? Number(token) : constants.get(token) ?? null;
    });
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
//   * `Array.from({ length: N })` / `Array(N)` — building N elements is what
//     makes a fixture expensive, and it is the one thing every fixture must do.
//     The `length:` must sit inside `Array.from(`: a bare `length:` matched any
//     object property, so `mockResult({ length: 50 })` counted as fifty rows
//     (CodeRabbit review of #647);
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
  String.raw`\bArray\.from\s*\(\s*\{\s*length:\s*([A-Za-z_$][\w$]*|\d+)`,
  String.raw`\bArray\s*\(\s*([A-Za-z_$][\w$]*|\d+)\s*\)`,
  String.raw`\b[A-Za-z_$][\w$]*(?:Page|Rows|Items|Entries)\s*\(\s*([A-Za-z_$][\w$]*|\d+)\s*\)`,
].join("|"), "g");

export function mountsAPageSizeFixture(body: string, sizes: number[], constants: Map<string, number>): boolean {
  if (sizes.length === 0) return false;
  const smallestPage = Math.min(...sizes);
  // SUMMED, not compared one at a time. What costs the accessible-name walk is
  // how many rows end up in the document, and a fixture assembled from pieces —
  // `[...batch(25), ...batch(25)]` on a screen that pages at 50 — mounts
  // exactly as many as one written whole. Checking each match alone missed that
  // entirely (sonnet review of #647). Summing errs toward flagging, which is
  // the right direction for a ratchet: the cost of a false positive is a
  // reader's half minute, the cost of a false negative is a returned flake.
  const total = [...body.matchAll(FIXTURE_LENGTH)]
    .map((match) => match.slice(1).find((group) => group !== undefined))
    .map((token) => (token === undefined ? null : /^\d+$/.test(token) ? Number(token) : constants.get(token) ?? null))
    .reduce((sum: number, value) => sum + (value ?? 0), 0);
  return total >= smallestPage;
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
