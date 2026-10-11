import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// #1212 — `--brand` and MUI's `primary.main` are a fill colour in both themes,
// so as TEXT they fall to ~1.3:1 on the dark surface. `--stat-accent` is the
// accent text token: it equals `--brand` in light and is the palette's light
// accent at night (contrast pinned in styles.test.ts). styles.css rules are out
// of scope here because each brand-text rule there carries a dark override.

const SRC = join(__dirname);
const BRAND_TEXT = /\bcolor:\s*["'`](?:var\(--brand(?:-press|-tint)?\)|primary\.(?:main|dark))/g;

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) out.push(p);
  }
  return out;
}

describe("brand fill is never used as text colour in components", () => {
  it("uses --stat-accent for brand-coloured text", () => {
    const files = walk(SRC);
    expect(files.length).toBeGreaterThan(100);
    const hits = files.flatMap((file) =>
      readFileSync(file, "utf8").split("\n").flatMap((line, i) =>
        line.match(BRAND_TEXT) ? [`${file.slice(SRC.length + 1)}:${i + 1}`] : []));
    expect(hits, `use var(--stat-accent) instead:\n${hits.join("\n")}`).toEqual([]);
  });
});
