/// <reference types="node" />
// The node reference is explicit: this test reads the pre-paint script from disk
// (node:fs / node:url) but sits under the DOM-only tsconfig.app, and TypeScript 7
// no longer auto-resolves the `node:` builtins here without it (TS2591).
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// The pre-paint script is a plain script (not a module) so the CSP can stay
// `script-src 'self'` with no hash to maintain (#144). Evaluating its source is
// the only way to test it, and it is worth testing: it is the single place that
// decides what the very first paint looks like on BOTH axes.
//
// The relative path is held in a variable rather than passed as an inline
// string literal: Vite's import-analysis plugin statically pattern-matches
// `new URL("literal", import.meta.url)` and rewrites it into a dev-server
// asset URL (e.g. "http://localhost:3000/theme-init.js") under the jsdom test
// environment, which fileURLToPath() then rejects as "not scheme file". A
// variable first argument isn't literal text, so the transform doesn't match
// and the real WHATWG URL resolution (against the real file:// module URL)
// runs instead. Same fix as src/test/cssTokens.ts.
const THEME_INIT_REL = "../../public/theme-init.js";
const SRC = readFileSync(fileURLToPath(new URL(THEME_INIT_REL, import.meta.url)), "utf8");

const run = () => new Function(SRC)();

beforeEach(() => {
  document.documentElement.removeAttribute("data-theme");
  document.documentElement.removeAttribute("data-brand");
  localStorage.clear();
});
afterEach(() => vi.unstubAllGlobals());

describe("theme-init pre-paint script", () => {
  it("always writes a concrete data-theme, even with nothing stored", () => {
    // The whole point: CSS needs exactly one dark construct, which is only
    // sound if the attribute is never absent.
    run();
    expect(document.documentElement.dataset.theme).toBe("light");
  });

  it("seeds dark from the OS preference when no choice is stored", () => {
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    run();
    expect(document.documentElement.dataset.theme).toBe("dark");
  });

  it("does NOT write the storage key when seeding from the OS", () => {
    // Seeding writes the attribute, never the key — that is what keeps a user
    // who has never toggled following their OS on every later visit.
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    run();
    expect(localStorage.getItem("cluckwork.theme")).toBeNull();
  });

  it("prefers an explicit stored choice over the OS preference", () => {
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    localStorage.setItem("cluckwork.theme", "light");
    run();
    expect(document.documentElement.dataset.theme).toBe("light");
  });

  it("still resolves a concrete theme when the storage read throws", () => {
    const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    // The matchMedia fallback sits OUTSIDE the try, so a throwing read must not
    // cost the OS seed.
    expect(() => run()).not.toThrow();
    expect(document.documentElement.dataset.theme).toBe("dark");
    spy.mockRestore();
  });

  it("applies a cached non-default brand", () => {
    localStorage.setItem("cluckwork.brand", "forest");
    run();
    expect(document.documentElement.dataset.brand).toBe("forest");
  });

  it("leaves data-brand off for the default palette", () => {
    localStorage.setItem("cluckwork.brand", "aubergine");
    run();
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("leaves data-brand off when nothing is cached", () => {
    run();
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("applies an unknown cached id verbatim rather than filtering it", () => {
    // Deliberate: no allowlist here. An unknown id matches no CSS rule and
    // renders aubergine anyway, whereas a duplicated list would mean a newly
    // added palette silently loses its pre-paint cache and flashes the default.
    localStorage.setItem("cluckwork.brand", "chartreuse");
    run();
    expect(document.documentElement.dataset.brand).toBe("chartreuse");
  });

  it("resolves the theme even if the brand read throws", () => {
    let calls = 0;
    const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation((k) => {
      calls += 1;
      if (k === "cluckwork.brand") throw new Error("storage denied");
      return null;
    });
    expect(() => run()).not.toThrow();
    expect(document.documentElement.dataset.theme).toBe("light");
    expect(calls).toBeGreaterThan(0);
    spy.mockRestore();
  });
});
