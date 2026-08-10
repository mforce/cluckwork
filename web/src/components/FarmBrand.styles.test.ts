/// <reference types="node" />
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// #179 piece 1 — the sidebar branding slot's GEOMETRY.
//
// WHAT THIS PROVES, AND WHAT IT DOES NOT. jsdom does not lay out, so no test in
// this suite can assert a wordmark actually renders at its natural aspect —
// getBoundingClientRect is all zeros here. This reads the stylesheet and pins
// the DECLARATIONS instead. It catches exactly one regression: someone
// re-pinning .brand-logo to a fixed square, which is the #123 shape #179 was
// filed to undo (a wide wordmark kept its aspect and lost its height, so it
// rendered as a sliver). It does not verify rendering, and must not be
// described as if it does. The real-browser check is manual.
//
// The relative path is held in a variable, not an inline literal, for the same
// reason cssTokens.ts does it — see the comment there (Vite rewrites a literal
// `new URL("...", import.meta.url)` into a dev-server asset URL under jsdom).
const CSS_REL = "../styles.css";
const CSS_PATH = fileURLToPath(new URL(CSS_REL, import.meta.url));

// Reads one top-level rule's declarations. Deliberately tiny and literal
// rather than a general CSS parser: cssTokens.ts already owns the real cascade
// resolution for design tokens, and this needs one flat block.
function declarationsOf(selector: string): Map<string, string> {
  const css = readFileSync(CSS_PATH, "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
  const start = css.indexOf(`\n${selector} {`);
  if (start === -1) throw new Error(`No top-level rule for '${selector}' in styles.css`);
  const open = css.indexOf("{", start);
  const close = css.indexOf("}", open);
  if (close === -1) throw new Error(`Unterminated rule for '${selector}'`);

  const decls = new Map<string, string>();
  for (const part of css.slice(open + 1, close).split(";")) {
    const colon = part.indexOf(":");
    if (colon === -1) continue;
    decls.set(part.slice(0, colon).trim(), part.slice(colon + 1).trim());
  }
  return decls;
}

describe(".brand-logo geometry (#179)", () => {
  const decls = declarationsOf(".brand-logo");

  it("does not pin the logo to a fixed width", () => {
    // The #123 shape this replaces was `width: 26px; height: 26px`. A fixed
    // width is the defect: with object-fit: contain a wide wordmark keeps its
    // aspect ratio and collapses vertically to fit the square.
    expect(decls.get("width")).toBe("auto");
  });

  it("bounds how much of the sidebar a wide logo may take", () => {
    // Unbounded, a wordmark would push .brand-name out of the 244px sidebar
    // entirely. This only binds for WIDE logos — a square mark stays at the
    // height below and leaves the name its full slot.
    const maxWidth = decls.get("max-width");
    expect(maxWidth).toMatch(/^\d+px$/);
    expect(Number.parseInt(maxWidth!, 10)).toBeLessThan(196); // sidebar minus .brand's padding
  });

  it("drives the size from the height, so any aspect ratio fits the row", () => {
    expect(decls.get("height")).toMatch(/^\d+px$/);
  });

  it("still letterboxes rather than cropping", () => {
    // Independent of the above: `contain` is what stops a non-matching aspect
    // being cut off. #179's point was that contain ALONE was not enough.
    expect(decls.get("object-fit")).toBe("contain");
  });
});
