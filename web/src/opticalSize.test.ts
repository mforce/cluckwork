import { describe, it, expect } from "vitest";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";

// #835 — display text gets an optical size only if the face the app LOADS
// carries Inter's `opsz` axis, and `@fontsource-variable/inter` ships two sets
// of faces under one family name. The default entry's seven `url()`s are all
// `-wght-` faces, which pin every size to the opsz 14 text cut; the `opsz.css`
// entry's seven are the variable ones.
//
// Nothing else catches a revert. The wrong face renders the same characters at
// the same weight in the same family, so every screen test, every snapshot and
// every contrast check stays green — the only difference is ~6% of advance
// width at display sizes (measured in Chromium: "Recorded 1,248 eggs" at 32px
// is 331px wide at opsz 14 and 310px at opsz 32; on the wght face it is 331px
// at both). That is exactly the shape of defect the issue's 2026-09-14
// amendment describes, after the epic asserted the opposite for a year.
//
// This reads the REAL package off disk rather than pinning a filename list, so
// a dependency bump that renames or drops a face fails here instead of
// shipping a silent 404.

const webRoot = process.cwd();
const PACKAGE = "@fontsource-variable/inter";
const packageRoot = resolve(webRoot, "node_modules", PACKAGE);

const source = (relative: string) => readFileSync(resolve(webRoot, relative), "utf8");

/** The fontsource entry `main.tsx` actually imports, as a path on disk. */
function loadedFontEntry(): { specifier: string; path: string } {
  const specifier = /import "(@fontsource-variable\/inter[^"]*)";/.exec(source("src/main.tsx"))?.[1];
  if (specifier === undefined) throw new Error("main.tsx imports no @fontsource-variable/inter entry");
  const withinPackage = specifier.slice(PACKAGE.length).replace(/^\//, "") || "index.css";
  const path = join(packageRoot, withinPackage.endsWith(".css") ? withinPackage : `${withinPackage}.css`);
  return { specifier, path };
}

const entry = loadedFontEntry();
const entryCss = readFileSync(entry.path, "utf8");
const faceFiles = [...entryCss.matchAll(/src:\s*url\(\.\/files\/([^)]+)\)/g)].map(([, file]) => file);

describe("#835 display optical size", () => {
  it("loads Inter faces that carry the opsz axis, and no wght-only face", () => {
    expect(faceFiles.length, `no @font-face src in ${entry.specifier}`).toBeGreaterThan(0);
    expect(faceFiles.filter((file) => !file.includes("-opsz-")), entry.specifier).toEqual([]);
  });

  it("covers the subsets the app's own locales need", () => {
    // en/es/tl live in latin and latin-ext. A future entry that drops either
    // would pass the axis check above while unloading the app's typeface.
    for (const subset of ["latin", "latin-ext"])
      expect(faceFiles.some((file) => file.startsWith(`inter-${subset}-`)), subset).toBe(true);
  });

  it("names font files that exist", () => {
    for (const file of faceFiles)
      expect(existsSync(join(packageRoot, "files", file)), file).toBe(true);
  });

  it("uses a font whose opsz axis reaches a display size", () => {
    const { variable } = JSON.parse(readFileSync(join(packageRoot, "metadata.json"), "utf8"));
    expect(variable.opsz, "the loaded package declares no opsz axis").toBeDefined();
    expect(Number(variable.opsz.min)).toBeLessThanOrEqual(14);
    expect(Number(variable.opsz.max)).toBeGreaterThanOrEqual(32);
  });
});

// `font-optical-sizing: auto` is the CSS initial value, so the face swap above
// is the whole behaviour: a 32px figure resolves to the display cut and a 14px
// row keeps the text cut, with no declaration anywhere. Two things would undo
// that silently, and neither is visible in a screen test — so the check walks
// every source file rather than the handful a reader would think of.
describe("#835 nothing flattens display and body onto one cut", () => {
  const walk = (dir: string): string[] =>
    readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
      e.isDirectory() ? walk(join(dir, e.name))
        : /\.(css|ts|tsx)$/.test(e.name) ? [join(dir, e.name)] : []);

  const files = walk(resolve(webRoot, "src"))
    .filter((path) => path !== resolve(webRoot, "src/opticalSize.test.ts"));

  it("walks the whole source tree", () => {
    expect(files.length).toBeGreaterThan(100);
  });

  it("never turns optical sizing off", () => {
    const offenders = files.filter((path) =>
      /font-optical-sizing:\s*none|fontOpticalSizing:\s*["']none["']/.test(readFileSync(path, "utf8")));
    expect(offenders.map((p) => p.slice(webRoot.length + 1))).toEqual([]);
  });

  it("never pins opsz to one value for every size", () => {
    const offenders = files.filter((path) =>
      /font-?[vV]ariation-?[sS]ettings[^;\n]*opsz/.test(readFileSync(path, "utf8")));
    expect(offenders.map((p) => p.slice(webRoot.length + 1))).toEqual([]);
  });
});

// The precache is where the cost of this axis lands (#825), and dropping the
// Latin faces would "save" 71 KiB by unloading the app's own typeface offline.
// verify-sw.mjs checks the built manifest; this checks the input that produces
// it, so a pattern edit fails in the unit suite rather than only after a build.
describe("#835 precache keeps the faces the app renders", () => {
  const config = source("vite.config.ts");
  const ignores = /globIgnores:\s*\[([^\]]*)\]/.exec(config)?.[1] ?? "";

  it("drops only subsets outside the app's locales", () => {
    expect(ignores, "vite.config.ts declares no globIgnores").not.toBe("");
    expect(ignores).not.toMatch(/\blatin\b/);
    for (const subset of ["cyrillic", "greek", "vietnamese"]) expect(ignores).toContain(subset);
  });
});
