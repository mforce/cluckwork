import { describe, it, expect, vi, beforeEach } from "vitest";
import { BRANDS, DEFAULT_BRAND, applyBrand, brandKeyFor, forgetBrandFor, initialBrand, isBrand } from "./brand";
import { bindAccount, bindFarm, clearBoundAccount, getBoundFarmCode } from "../auth/tokenStore";

beforeEach(() => {
  document.documentElement.removeAttribute("data-brand");
  localStorage.clear();
  // #586 — applyBrand caches ONLY under a slug the current login proved, so
  // every cache assertion below needs a bound tab. The unbound case, which is a
  // fresh tab restored from the refresh cookie, has its own tests further down.
  bindAccount("acct-A");
  bindFarm("sunny-acres");
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
    applyBrand("forest", getBoundFarmCode());
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBe("forest");
  });

  it("applyBrand REMOVES the attribute for the default palette", () => {
    // The default carries no attribute at all — CSS falls back to :root, which
    // is what makes an unknown id degrade to aubergine with no CSS-side check.
    applyBrand("forest", getBoundFarmCode());
    applyBrand("aubergine", getBoundFarmCode());
    expect(document.documentElement.dataset.brand).toBeUndefined();
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBe("aubergine");
  });

  it("applyBrand treats an unknown id as the default", () => {
    applyBrand("forest", getBoundFarmCode());
    applyBrand("chartreuse", getBoundFarmCode());
    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("applyBrand still applies the attribute when localStorage is unavailable", () => {
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyBrand("slate", getBoundFarmCode())).not.toThrow();
    expect(document.documentElement.dataset.brand).toBe("slate"); // cache is only a pre-paint hint
    spy.mockRestore();
  });

  it("applyBrand drops a stale cache when the write fails but reads still work", () => {
    applyBrand("forest", getBoundFarmCode());
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    applyBrand("slate", getBoundFarmCode());
    spy.mockRestore();
    // A stale "forest" would pre-paint the WRONG farm colour on next load.
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBeNull();
    expect(document.documentElement.dataset.brand).toBe("slate");
  });

  it("applyBrand survives removeItem also throwing", () => {
    const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    const remove = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyBrand("slate", getBoundFarmCode())).not.toThrow();
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

  it("brandKeyFor namespaces by slug", () => {
    expect(brandKeyFor("sunny-acres")).toBe("cluckwork.brand:sunny-acres");
  });

  it("writes NOTHING when the tab has no proven farm", () => {
    // A fresh tab restored from the refresh cookie. The attribute still applies
    // — the palette must show — but caching here is the only way farm A's
    // colour could ever reach farm B's key.
    clearBoundAccount();
    applyBrand("forest", getBoundFarmCode());
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.length).toBe(0);
  });

  it("writes NOTHING when the account binding no longer matches the slug", () => {
    // The leak test. Farm A's login proved "sunny-acres"; the tab is now bound
    // to farm B, so B's palette must not land under A's key.
    bindAccount("acct-B");
    applyBrand("forest", getBoundFarmCode());
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBeNull();
    expect(localStorage.length).toBe(0);
  });

  it("never reads or writes the pre-#586 un-namespaced key", () => {
    // Ownership: accountStorage's purge deletes it once at startup. applyBrand
    // must not resurrect it, and must not depend on it being gone either.
    localStorage.setItem("cluckwork.brand", "terracotta");
    applyBrand("forest", getBoundFarmCode());
    expect(localStorage.getItem("cluckwork.brand")).toBe("terracotta");
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBe("forest");
  });

  it("an UNBOUND tab leaves the pre-#586 un-namespaced key untouched", () => {
    // The bound case is covered above. This is the branch that had no coverage:
    // applyBrand returns early when no farm is proven, and must not take the
    // un-namespaced key with it on the way out — purging that key is startup's
    // job (lib/accountStorage.ts), not applyBrand's.
    clearBoundAccount();
    localStorage.setItem("cluckwork.brand", "terracotta");
    applyBrand("forest", getBoundFarmCode());
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.getItem("cluckwork.brand")).toBe("terracotta");
  });

  it("forgetBrandFor removes only that farm's palette", () => {
    localStorage.setItem("cluckwork.brand:farm-b", "slate");
    localStorage.setItem("cluckwork.brand:farm-a", "forest");
    localStorage.setItem("cluckwork.brand", "terracotta");

    forgetBrandFor("farm-b");

    expect(localStorage.getItem("cluckwork.brand:farm-b")).toBeNull();
    expect(localStorage.getItem("cluckwork.brand:farm-a")).toBe("forest");
    // The un-namespaced key is the startup purge's business, not forget's.
    expect(localStorage.getItem("cluckwork.brand")).toBe("terracotta");
  });

  it("forgetBrandFor never throws when storage is unavailable", () => {
    const remove = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => forgetBrandFor("farm-b")).not.toThrow();
    remove.mockRestore();
  });
});

describe("applyBrand superseded-response guard", () => {
  it("a response that outlived its farm applies NOTHING", () => {
    // Farm A's /account resolves after the operator has signed into farm B.
    // Neither the attribute nor the cache may take farm A's colour.
    const boundAt = getBoundFarmCode();          // "sunny-acres", from beforeEach
    bindAccount("acct-B");
    bindFarm("other-farm");
    applyBrand("forest", boundAt);
    expect(document.documentElement.dataset.brand).toBeUndefined();
    expect(localStorage.getItem("cluckwork.brand:other-farm")).toBeNull();
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBeNull();
  });

  it("an unbound tab still applies, because capture and check agree", () => {
    // Both null: nothing was superseded, so a cold restore still paints.
    clearBoundAccount();
    applyBrand("forest", null);
    expect(document.documentElement.dataset.brand).toBe("forest");
  });
});
