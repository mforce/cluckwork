import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// Every class the Playwright harness selects by must still exist in the app,
// either as a rule in styles.css or as a className token in a non-test .tsx.
// The canary and the capture specs are dispatch-only, so a class retired by a
// screen conversion leaves them waiting for markup that no longer renders and
// nothing on a pull request notices: #883 retired `.capture-grid` and
// `.capture-tile` and the canary failed weeks of pull requests later, on the
// release branch's full run (workflow run 35136083182). This walks the harness
// the way styles.declared-tokens.test.ts walks the app, and it runs in the
// unit suite, so the deletion and the stale selector meet on the same PR.
//
// Scope, stated so nobody trusts it for more: it reads selector STRING
// LITERALS passed to locator(), querySelector(), querySelectorAll(), $() and
// $$(). A selector built from a variable or by concatenation is invisible to
// it. MUI's own `.Mui*` classes are framework-owned and skipped.

const WEB = __dirname;
const HARNESS = join(WEB, "..", "..", "tools", "simulation", "ui");

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    if (entry === "node_modules" || entry === "test-results" || entry.startsWith("out")) continue;
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (/\.tsx?$/.test(entry)) out.push(p);
  }
  return out;
}

const rawCss = readFileSync(join(WEB, "styles.css"), "utf8");
// Comments come out first: a rule family's obituary ("ul.dash-list is gone")
// would otherwise keep the class alive for the harness. The stripper is a
// plain regex, so a `/*` inside a quoted CSS string would swallow real rules;
// the stylesheet has none, and this pins that.
expect(rawCss, "styles.css carries a /* inside a quoted string; the comment stripper below cannot see quotes")
  .not.toMatch(/["'][^"'\n]*\/\*/);
const css = rawCss.replace(/\/\*[\s\S]*?\*\//g, "");
const declared = new Set([...css.matchAll(/\.([A-Za-z_][\w-]*)/g)].map((m) => m[1]));

// Every quoted string inside a className attribute counts, whichever
// expression carries it: a plain string, a template literal, a ternary or a
// clsx() call. Tokens are split on whitespace and template holes dropped.
const markupHooks = new Set<string>();
for (const file of walk(WEB)) {
  if (/\.test\.tsx?$/.test(file) || file.startsWith(join(WEB, "test"))) continue;
  const text = readFileSync(file, "utf8");
  for (const attr of text.matchAll(/className=(?:"([^"]*)"|\{([\s\S]*?)\}(?=\s|\/?>))/g)) {
    const strings = attr[1] !== undefined ? [attr[1]] : [...attr[2].matchAll(/["'`]([^"'`]*)["'`]/g)].map((m) => m[1]);
    for (const s of strings) {
      for (const token of s.replace(/\$\{[^}]*\}/g, " ").split(/\s+/)) if (token) markupHooks.add(token);
    }
  }
}

describe("the Playwright harness selects only classes the app still renders", () => {
  it("every .class inside a selector string literal is a stylesheet rule or a markup hook", () => {
    expect(declared.size).toBeGreaterThan(100);
    expect(markupHooks.size).toBeGreaterThan(20);
    const stale = new Map<string, Set<string>>();
    for (const file of walk(HARNESS)) {
      const text = readFileSync(file, "utf8");
      for (const call of text.matchAll(/(?:locator|querySelectorAll|querySelector|\$\$|\$)\((["'`])([^"'`]+)\1/g)) {
        for (const cls of call[2].matchAll(/\.([A-Za-z_][\w-]*)/g)) {
          const name = cls[1];
          if (name.startsWith("Mui") || declared.has(name) || markupHooks.has(name)) continue;
          stale.set(name, (stale.get(name) ?? new Set()).add(file.slice(HARNESS.length + 1)));
        }
      }
    }
    const report = [...stale].map(([name, files]) => `.${name} in ${[...files].join(", ")}`).join("\n");
    expect(report, `harness selectors the app no longer renders:\n${report}`).toBe("");
  });
});
