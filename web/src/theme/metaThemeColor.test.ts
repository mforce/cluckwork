import { describe, expect, it } from "vitest";
import { BRANDS } from "../lib/brand";
import { syncThemeColorMeta, themeColorFrom } from "./metaThemeColor";
import { tokensFor } from "./farmTokens.test";

describe("themeColorFrom (#974)", () => {
  it("reads --lavender, the token MuiDrawer's shell paper already paints with", () => {
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        const tokens = tokensFor(brand, mode);
        expect(themeColorFrom(tokens)).toBe(tokens["--lavender"]);
      }
    }
  });

  // Proves the choice is neither --brand (identical in light/dark) nor
  // --surface (identical across farm palettes) — #974 asks for both axes.
  it("varies by both mode and farm palette", () => {
    const seen = new Set<string>();
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        seen.add(themeColorFrom(tokensFor(brand, mode)));
      }
    }
    expect(seen.size).toBe(BRANDS.length * 2);
  });
});

describe("syncThemeColorMeta (#974)", () => {
  function metaTag(doc: Document, media: string): HTMLMetaElement {
    const meta = doc.createElement("meta");
    meta.name = "theme-color";
    meta.media = media;
    meta.content = "#000000";
    return meta;
  }

  it("overwrites every meta[name=theme-color] with the same colour, whichever media query the browser is honouring", () => {
    const doc = document.implementation.createHTMLDocument("");
    const light = metaTag(doc, "(prefers-color-scheme: light)");
    const dark = metaTag(doc, "(prefers-color-scheme: dark)");
    doc.head.append(light, dark);

    const expected = tokensFor("forest", "dark")["--lavender"];
    syncThemeColorMeta(tokensFor("forest", "dark"), doc);

    expect(light.content).toBe(expected);
    expect(dark.content).toBe(expected);
  });
});
