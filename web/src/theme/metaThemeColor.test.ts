import { afterEach, describe, expect, it, vi } from "vitest";
import { BRANDS } from "../lib/brand";
import { stubMatchMedia } from "../test/matchMedia";
import { syncThemeColorMeta, themeColorFrom } from "./metaThemeColor";
import { tokensFor } from "./farmTokens.test";

afterEach(() => vi.unstubAllGlobals());

describe("themeColorFrom (#976 round 1)", () => {
  it("reads --lavender on desktop, the token MuiDrawer's shell paper paints with", () => {
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        const tokens = tokensFor(brand, mode);
        expect(themeColorFrom(tokens, true)).toBe(tokens["--lavender"]);
      }
    }
  });

  it("reads --canvas on phone, the colour measured behind the transparent main content", () => {
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        const tokens = tokensFor(brand, mode);
        expect(themeColorFrom(tokens, false)).toBe(tokens["--canvas"]);
      }
    }
  });

  it("desktop's --lavender varies by both mode and farm palette", () => {
    const seen = new Set<string>();
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        seen.add(themeColorFrom(tokensFor(brand, mode), true));
      }
    }
    expect(seen.size).toBe(BRANDS.length * 2);
  });
});

describe("syncThemeColorMeta (#974/#976)", () => {
  function metaTag(doc: Document, media: string): HTMLMetaElement {
    const meta = doc.createElement("meta");
    meta.name = "theme-color";
    meta.media = media;
    meta.content = "#000000";
    return meta;
  }

  it("overwrites every meta[name=theme-color] with the same colour, whichever media query the browser is honouring", () => {
    stubMatchMedia(true); // desktop layout
    const doc = document.implementation.createHTMLDocument("");
    const light = metaTag(doc, "(prefers-color-scheme: light)");
    const dark = metaTag(doc, "(prefers-color-scheme: dark)");
    doc.head.append(light, dark);

    const expected = tokensFor("forest", "dark")["--lavender"];
    syncThemeColorMeta(tokensFor("forest", "dark"), doc);

    expect(light.content).toBe(expected);
    expect(dark.content).toBe(expected);
  });

  it("picks the desktop colour when the layout matches MD_UP_QUERY", () => {
    const media = stubMatchMedia(true);
    const doc = document.implementation.createHTMLDocument("");
    doc.head.append(metaTag(doc, ""));

    const tokens = tokensFor("aubergine", "dark");
    syncThemeColorMeta(tokens, doc);

    expect(doc.querySelector('meta[name="theme-color"]')?.getAttribute("content")).toBe(tokens["--lavender"]);
    expect(media.matchMedia).toHaveBeenCalledWith(expect.stringContaining("900px"));
  });

  it("picks the phone colour when the layout does not match MD_UP_QUERY", () => {
    stubMatchMedia(false);
    const doc = document.implementation.createHTMLDocument("");
    doc.head.append(metaTag(doc, ""));

    const tokens = tokensFor("aubergine", "dark");
    syncThemeColorMeta(tokens, doc);

    expect(doc.querySelector('meta[name="theme-color"]')?.getAttribute("content")).toBe(tokens["--canvas"]);
  });
});
