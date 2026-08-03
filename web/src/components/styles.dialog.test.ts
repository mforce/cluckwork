import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// The `wide` dialog variant is the one place in this stylesheet where a
// modifier has to be undone inside a media query, and the reason is a cascade
// rule that reads backwards: `@media` contributes NOTHING to specificity, so
// the ≤900px `.dialog { max-width: none }` sheet reset (0,1,0) does not undo
// `.dialog.wide { max-width: 52rem }` (0,2,0). Without an explicit override the
// phone sheet keeps a 52rem cap, and a landscape phone between 832px and 900px
// renders bottom-sheet chrome with the backdrop showing down one side.
//
// jsdom computes no layout and evaluates no media query, so the guarantee is
// pinned against the stylesheet TEXT. That is narrow on purpose: it does not
// claim the dialog renders at any width, only that the override exists in the
// block that needs it.
// Resolved from the project root, not `import.meta.url` — under jsdom that is
// an http:// URL and readFileSync refuses it.
//
// COMMENTS ARE STRIPPED FIRST, and that is load-bearing rather than tidiness:
// this stylesheet documents its own selectors in prose, so a comment is the one
// place a decoy `.dialog.wide { … }` can sit. Reviewed against text that keeps
// them, a comment carrying the right declaration made a genuinely broken rule
// below it pass all three assertions. Comment braces would also unbalance the
// block scanner. (Strings can hold braces too, but this stylesheet has none
// containing one — `content:` is unused here.)
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8")
  .replace(/\/\*[\s\S]*?\*\//g, "");

// Balanced block starting at `from`'s first "{".
function blockAt(from: number): { text: string; end: number } {
  let depth = 0;
  for (let i = css.indexOf("{", from); i < css.length; i++) {
    if (css[i] === "{") depth++;
    else if (css[i] === "}" && --depth === 0) return { text: css.slice(from, i + 1), end: i + 1 };
  }
  throw new Error("unterminated block");
}

// The stylesheet carries SEVERAL ≤900px blocks (the phone rules are grouped by
// area, not in one place), so they are all collected — a rule that moved
// between them is still in force.
function mobileCss(): string {
  const parts: string[] = [];
  for (let at = css.indexOf("@media (max-width: 900px)"); at !== -1;) {
    const { text, end } = blockAt(at);
    parts.push(text);
    at = css.indexOf("@media (max-width: 900px)", end);
  }
  expect(parts.length, "≤900px media blocks").toBeGreaterThan(0);
  return parts.join("\n");
}

// Everything NOT inside a media query — where the desktop cap has to live.
function desktopCss(): string {
  let out = "";
  for (let i = 0; i < css.length;) {
    const at = css.indexOf("@media", i);
    if (at === -1) { out += css.slice(i); break; }
    out += css.slice(i, at);
    i = blockAt(at).end;
  }
  return out;
}

// Whitespace-tolerant: `.dialog.wide {`, `.dialog.wide{` and a brace on the
// next line are the same rule, and a matcher that only knew the first spelling
// would fail on a correct stylesheet after a reformat. Anchored at a boundary
// so `.dialog` cannot match inside `.dialog.wide`.
const ruleFor = (block: string, selector: string): string | null => {
  const pattern = new RegExp(
    `(^|[\\s}])${selector.replace(/[.]/g, "\\.")}\\s*\\{([^}]*)\\}`,
  );
  return pattern.exec(block)?.[2] ?? null;
};

describe("dialog width rules", () => {
  it("caps the wide variant on desktop", () => {
    expect(ruleFor(desktopCss(), ".dialog.wide")).toMatch(/max-width:\s*52rem/);
  });

  it("releases the wide cap inside the phone-sheet media query", () => {
    const wideOnMobile = ruleFor(mobileCss(), ".dialog.wide");
    expect(wideOnMobile, ".dialog.wide override at ≤900px").not.toBeNull();
    expect(wideOnMobile).toMatch(/max-width:\s*none/);
  });

  it("still resets the base dialog there, so the two agree", () => {
    expect(ruleFor(mobileCss(), ".dialog")).toMatch(/max-width:\s*none/);
  });
});
