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

// Same variable-not-literal trick as THEME_INIT_REL above — Vite's
// import-analysis plugin rewrites a literal first argument to new URL().
const FARM_CODE_CACHE_REL = "../auth/farmCodeCache.ts";

const SRC = readFileSync(fileURLToPath(new URL(THEME_INIT_REL, import.meta.url)), "utf8");

const run = () => new Function(SRC)();

// jsdom updates location.search from replaceState without attempting a real
// navigation, which is the only reliable way to drive the pre-paint script's
// ?farm= branch under this harness.
const setSearch = (search: string) => {
  window.history.replaceState({}, "", search === "" ? "/" : "/" + search);
};

beforeEach(() => {
  document.documentElement.removeAttribute("data-theme");
  document.documentElement.removeAttribute("data-brand");
  localStorage.clear();
  setSearch("");
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

  // --- #586: which FARM's palette (the theme axis above is unchanged) ---

  it("branch 1: ?farm= paints that farm's palette", () => {
    setSearch("?farm=sunny-acres");
    localStorage.setItem("cluckwork.brand:sunny-acres", "forest");
    run();
    expect(document.documentElement.dataset.brand).toBe("forest");
  });

  it("branch 1 has NO legacy fallback: an unknown ?farm= takes the default", () => {
    // A URL naming a farm this device has no record of must not inherit
    // whichever farm last painted here.
    setSearch("?farm=other-farm");
    localStorage.setItem("cluckwork.brand", "terracotta");
    localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["sunny-acres"]));
    localStorage.setItem("cluckwork.brand:sunny-acres", "forest");
    run();
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("an INVALID ?farm= falls through to the roster, exactly like the login prefill", () => {
    // Mirrors Login.tsx:96-105, which documents this precedence.
    for (const bad of ["?farm=-leading", "?farm=" + "a".repeat(33), "?farm=", "?farm=Has%20Space"]) {
      document.documentElement.removeAttribute("data-brand");
      setSearch(bad);
      localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["sunny-acres"]));
      localStorage.setItem("cluckwork.brand:sunny-acres", "forest");
      run();
      expect(document.documentElement.dataset.brand).toBe("forest");
    }
  });

  it("branch 2: exactly one remembered farm paints its palette", () => {
    localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["sunny-acres"]));
    localStorage.setItem("cluckwork.brand:sunny-acres", "slate");
    run();
    expect(document.documentElement.dataset.brand).toBe("slate");
  });

  it("branch 2 falls back to the legacy key on a miss — the upgrade-day path", () => {
    // No build before #586 wrote a per-slug key, so on the first cold start
    // after upgrade this is the ONLY source. Without it the whole device
    // flashes the default, which is the #149 regression this issue avoids.
    localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["sunny-acres"]));
    localStorage.setItem("cluckwork.brand", "terracotta");
    run();
    expect(document.documentElement.dataset.brand).toBe("terracotta");
  });

  it("branch 4: two remembered farms and no ?farm= assert NOTHING", () => {
    // At /login the app does not know which farm, so it must not claim one.
    localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["sunny-acres", "other-farm"]));
    localStorage.setItem("cluckwork.brand:sunny-acres", "forest");
    localStorage.setItem("cluckwork.brand", "terracotta");
    run();
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("an EMPTIED roster never reaches the legacy key", () => {
    // THE leak test. removeFarmCode writes "[]" (farmCodeCache.ts:201-203), so
    // "Forget every farm" on a multi-farm device leaves an empty roster beside
    // a legacy key holding the LAST farm's colour. Absent means "no login since
    // #535"; "[]" means "this device curated its list" and proves nothing.
    localStorage.setItem("cluckwork.farmCodes", "[]");
    localStorage.setItem("cluckwork.brand", "terracotta");
    run();
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("a MALFORMED roster never reaches the legacy key either", () => {
    // A mangled roster could be a mangled MULTI-farm roster.
    // "" is load-bearing: it is FALSY but not null, and it is the ONLY input that
    // separates `rawRoster === null` from a truthy test. Without it that guard
    // can be weakened with the whole suite still green.
    for (const raw of ["", "not json", JSON.stringify({ codes: ["sunny-acres"] }), '"sunny-acres"']) {
      document.documentElement.removeAttribute("data-brand");
      localStorage.clear();
      localStorage.setItem("cluckwork.farmCodes", raw);
      localStorage.setItem("cluckwork.brand", "terracotta");
      run();
      expect(document.documentElement.dataset.brand).toBeUndefined();
    }
  });

  it("a roster of one UNPARSEABLE entry asserts nothing", () => {
    localStorage.setItem("cluckwork.farmCodes", JSON.stringify([""]));
    localStorage.setItem("cluckwork.brand", "terracotta");
    run();
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("the slug pattern is IDENTICAL to farmCodeCache's, character for character", () => {
    // An independent copy in a different file that cannot import. This is a
    // DRIFT ALARM, not a guard: it cannot fail closed, so it must fail loudly.
    const cacheSrc = readFileSync(
      fileURLToPath(new URL(FARM_CODE_CACHE_REL, import.meta.url)),
      "utf8",
    );
    const extract = (src: string) => {
      const m = src.match(/\/\^\[a-z0-9\]\[a-z0-9-\]\{1,30\}\[a-z0-9\]\$\//);
      return m === null ? null : m[0];
    };
    const inCache = extract(cacheSrc);
    const inScript = extract(SRC);
    expect(inCache).not.toBeNull();
    expect(inScript).not.toBeNull();
    expect(inScript).toBe(inCache);
  });
});
