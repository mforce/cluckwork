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

// Comments come out first: a rule family's obituary ("ul.dash-list is gone")
// would otherwise keep the class alive for the harness.
const css = readFileSync(join(WEB, "styles.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
const declared = new Set([...css.matchAll(/\.([A-Za-z_][\w-]*)/g)].map((m) => m[1]));

const markupHooks = new Set<string>();
for (const file of walk(WEB)) {
  if (/\.test\.tsx?$/.test(file) || file.includes(`${join(WEB, "test")}`)) continue;
  const text = readFileSync(file, "utf8");
  for (const m of text.matchAll(/className=(?:"([^"]*)"|\{`([^`]*)`\}|\{"([^"]*)"\})/g)) {
    for (const token of (m[1] ?? m[2] ?? m[3] ?? "").split(/\s+/)) {
      const bare = token.replace(/\$\{[^}]*\}/g, "").trim();
      if (bare) markupHooks.add(bare);
    }
  }
}

describe("the Playwright harness selects only classes the app still renders", () => {
  it("every .class inside a locator() string is a stylesheet rule or a markup hook", () => {
    expect(declared.size).toBeGreaterThan(100);
    const stale = new Map<string, Set<string>>();
    for (const file of walk(HARNESS)) {
      const text = readFileSync(file, "utf8");
      for (const call of text.matchAll(/locator\((["'`])([^"'`]+)\1/g)) {
        for (const cls of call[2].matchAll(/\.([A-Za-z_][\w-]*)/g)) {
          const name = cls[1];
          if (declared.has(name) || markupHooks.has(name)) continue;
          stale.set(name, (stale.get(name) ?? new Set()).add(file.slice(HARNESS.length + 1)));
        }
      }
    }
    const report = [...stale].map(([name, files]) => `.${name} in ${[...files].join(", ")}`).join("\n");
    expect(report, `harness selectors the app no longer renders:\n${report}`).toBe("");
  });
});
