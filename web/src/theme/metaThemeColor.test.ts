import { afterEach, describe, expect, it, vi } from "vitest";
import { BRANDS } from "../lib/brand";
import { stubMatchMedia } from "../test/matchMedia";
import { resyncThemeColorMeta, syncThemeColorMeta, themeColorFrom } from "./metaThemeColor";
import { tokensFor } from "./farmTokens.test";

afterEach(() => vi.unstubAllGlobals());

describe("themeColorFrom (#976)", () => {
  it("reads --lavender on desktop, the token MuiDrawer's shell paper paints with", () => {
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        const tokens = tokensFor(brand, mode);
        expect(themeColorFrom(tokens, true, false)).toBe(tokens["--lavender"]);
      }
    }
  });

  it("reads --canvas on phone, the colour measured behind the transparent main content", () => {
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        const tokens = tokensFor(brand, mode);
        expect(themeColorFrom(tokens, false, false)).toBe(tokens["--canvas"]);
      }
    }
  });

  it("desktop's --lavender varies by both mode and farm palette", () => {
    const seen = new Set<string>();
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        seen.add(themeColorFrom(tokensFor(brand, mode), true, false));
      }
    }
    expect(seen.size).toBe(BRANDS.length * 2);
  });

  // #976 round 2 — the login screen and post-login splash fill the whole
  // viewport in --surface-2/--canvas with no sidebar at all, at ANY layout
  // width, so this overrides the layout branch rather than adding a third one.
  it("reads --canvas when an auth surface is showing, even at desktop layout", () => {
    for (const brand of BRANDS) {
      for (const mode of ["light", "dark"] as const) {
        const tokens = tokensFor(brand, mode);
        expect(themeColorFrom(tokens, true, true)).toBe(tokens["--canvas"]);
      }
    }
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

  it("picks --canvas at desktop layout when AuthShell's marker is present", () => {
    stubMatchMedia(true); // desktop layout — would otherwise pick --lavender
    const doc = document.implementation.createHTMLDocument("");
    doc.head.append(metaTag(doc, ""));
    doc.body.innerHTML = '<main data-meta-surface="auth"></main>';

    const tokens = tokensFor("aubergine", "dark");
    syncThemeColorMeta(tokens, doc);

    expect(doc.querySelector('meta[name="theme-color"]')?.getAttribute("content")).toBe(tokens["--canvas"]);
  });

  it("picks --canvas at desktop layout when the splash backdrop is present", () => {
    stubMatchMedia(true);
    const doc = document.implementation.createHTMLDocument("");
    doc.head.append(metaTag(doc, ""));
    doc.body.innerHTML = '<div class="brand-splash-backdrop"></div>';

    const tokens = tokensFor("aubergine", "dark");
    syncThemeColorMeta(tokens, doc);

    expect(doc.querySelector('meta[name="theme-color"]')?.getAttribute("content")).toBe(tokens["--canvas"]);
  });
});

describe("resyncThemeColorMeta (#976 round 2)", () => {
  it("reads live tokens off the real document and writes the meta tag", () => {
    stubMatchMedia(true);
    const meta = document.createElement("meta");
    meta.name = "theme-color";
    document.head.appendChild(meta);
    try {
      resyncThemeColorMeta();
      // jsdom has no real stylesheet here, so this only proves the call reads
      // SOME tokens and writes SOME colour — themeColorFrom's own tests prove
      // which one against the real stylesheet.
      expect(meta.content.length).toBeGreaterThan(0);
    } finally {
      meta.remove();
    }
  });
});
