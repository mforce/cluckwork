import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// Every custom property the app reads must be declared in styles.css. An
// undeclared `var(--x)` fails silently: the whole declaration becomes invalid
// at computed-value time, so a border, colour or outline disappears with no
// error anywhere. #883 shipped the Dashboard's ruled list reading `--rule`
// and `--rule-strong` that nothing declared, and `.named-picker-trigger`'s
// focus ring read `--accent` for even longer, resolving to its literal
// fallback instead of the farm's focus token. Test helpers under src/test
// name fake properties on purpose and are excluded.

const SRC = join(__dirname);
const css = readFileSync(join(SRC, "styles.css"), "utf8");
const declared = new Set([...css.matchAll(/^\s*(--[a-z0-9-]+)\s*:/gm)].map((m) => m[1]));

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) {
      if (entry !== "test") walk(p, out);
    } else if (/\.(tsx?|css)$/.test(entry) && !/\.test\.tsx?$/.test(entry)) {
      out.push(p);
    }
  }
  return out;
}

describe("custom properties the app reads are declared in styles.css", () => {
  it("references only declared tokens", () => {
    expect(declared.size).toBeGreaterThan(40);
    const undeclared = new Map<string, Set<string>>();
    for (const file of walk(SRC)) {
      const text = readFileSync(file, "utf8");
      for (const m of text.matchAll(/var\((--[a-z0-9-]+)/g)) {
        if (!declared.has(m[1])) {
          undeclared.set(m[1], (undeclared.get(m[1]) ?? new Set()).add(file.slice(SRC.length + 1)));
        }
      }
    }
    const report = [...undeclared].map(([name, files]) => `${name} in ${[...files].join(", ")}`).join("\n");
    expect(report, `undeclared custom properties:\n${report}`).toBe("");
  });
});
