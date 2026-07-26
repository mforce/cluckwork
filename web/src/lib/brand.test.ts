import { describe, it, expect, vi, beforeEach } from "vitest";
import { BRANDS, DEFAULT_BRAND, applyBrand, initialBrand, isBrand } from "./brand";

beforeEach(() => {
  document.documentElement.removeAttribute("data-brand");
  localStorage.clear();
});

describe("brand", () => {
  it("lists the curated palettes with aubergine as the default", () => {
    expect(BRANDS).toEqual(["aubergine", "forest", "slate", "terracotta"]);
    expect(DEFAULT_BRAND).toBe("aubergine");
    expect(BRANDS).toContain(DEFAULT_BRAND);
  });

  it("isBrand accepts curated ids and rejects anything else", () => {
    expect(isBrand("forest")).toBe(true);
    expect(isBrand("aubergine")).toBe(true);
    expect(isBrand("chartreuse")).toBe(false);
    expect(isBrand("")).toBe(false);
    expect(isBrand("Forest")).toBe(false); // ids are lowercase; the API canonicalizes
  });

  it("applyBrand sets the attribute for a non-default palette and caches it", () => {
    applyBrand("forest");
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.getItem("cluckwork.brand")).toBe("forest");
  });

  it("applyBrand REMOVES the attribute for the default palette", () => {
    // The default carries no attribute at all — CSS falls back to :root, which
    // is what makes an unknown id degrade to aubergine with no CSS-side check.
    applyBrand("forest");
    applyBrand("aubergine");
    expect(document.documentElement.dataset.brand).toBeUndefined();
    expect(localStorage.getItem("cluckwork.brand")).toBe("aubergine");
  });

  it("applyBrand treats an unknown id as the default", () => {
    applyBrand("forest");
    applyBrand("chartreuse");
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("applyBrand still applies the attribute when localStorage is unavailable", () => {
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyBrand("slate")).not.toThrow();
    expect(document.documentElement.dataset.brand).toBe("slate"); // cache is only a pre-paint hint
    spy.mockRestore();
  });

  it("applyBrand drops a stale cache when the write fails but reads still work", () => {
    applyBrand("forest");
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    applyBrand("slate");
    spy.mockRestore();
    // A stale "forest" would pre-paint the WRONG farm colour on next load.
    expect(localStorage.getItem("cluckwork.brand")).toBeNull();
    expect(document.documentElement.dataset.brand).toBe("slate");
  });

  it("applyBrand survives removeItem also throwing", () => {
    const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    const remove = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyBrand("slate")).not.toThrow();
    set.mockRestore();
    remove.mockRestore();
  });

  it("initialBrand reads the attribute, defaulting to aubergine", () => {
    expect(initialBrand()).toBe("aubergine");
    document.documentElement.dataset.brand = "slate";
    expect(initialBrand()).toBe("slate");
    document.documentElement.dataset.brand = "chartreuse";
    expect(initialBrand()).toBe("aubergine");
  });
});
