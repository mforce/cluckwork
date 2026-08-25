import { describe, it, expect, vi, beforeEach } from "vitest";
import { BRANDS, DEFAULT_BRAND, applyBrand, brandKeyFor, initialBrand, isBrand } from "./brand";
import { bindAccount, bindFarm, clearBoundAccount } from "../auth/tokenStore";

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
    applyBrand("forest");
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBe("forest");
  });

  it("applyBrand REMOVES the attribute for the default palette", () => {
    // The default carries no attribute at all — CSS falls back to :root, which
    // is what makes an unknown id degrade to aubergine with no CSS-side check.
    applyBrand("forest");
    applyBrand("aubergine");
    expect(document.documentElement.dataset.brand).toBeUndefined();
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBe("aubergine");
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

  it("brandKeyFor namespaces by slug", () => {
    expect(brandKeyFor("sunny-acres")).toBe("cluckwork.brand:sunny-acres");
  });

  it("writes NOTHING when the tab has no proven farm", () => {
    // A fresh tab restored from the refresh cookie. The attribute still applies
    // — the palette must show — but caching here is the only way farm A's
    // colour could ever reach farm B's key.
    clearBoundAccount();
    applyBrand("forest");
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.length).toBe(0);
  });

  it("writes NOTHING when the account binding no longer matches the slug", () => {
    // The leak test. Farm A's login proved "sunny-acres"; the tab is now bound
    // to farm B, so B's palette must not land under A's key.
    bindAccount("acct-B");
    applyBrand("forest");
    expect(document.documentElement.dataset.brand).toBe("forest");
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBeNull();
    expect(localStorage.length).toBe(0);
  });

  it("deletes the pre-#586 un-namespaced key once a farm has its own", () => {
    // The legacy key is read at pre-paint only as a fallback for a farm the
    // device can name. Once that farm has a real key, the fallback is stale.
    localStorage.setItem("cluckwork.brand", "terracotta");
    applyBrand("forest");
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBe("forest");
    expect(localStorage.getItem("cluckwork.brand")).toBeNull();
  });

  it("keeps the legacy key when there is no proven farm to supersede it", () => {
    // Deleting it on an unbound tab would strand a single-farm device with no
    // pre-paint source at all until its next explicit login.
    clearBoundAccount();
    localStorage.setItem("cluckwork.brand", "terracotta");
    applyBrand("forest");
    expect(localStorage.getItem("cluckwork.brand")).toBe("terracotta");
  });

  it("a failed slug write leaves the legacy fallback intact", () => {
    // Ordering, pinned. If the legacy removal ran BEFORE the slug write, a
    // failing write would leave the device with no pre-paint source at all:
    // fallback destroyed, replacement never written.
    localStorage.setItem("cluckwork.brand", "terracotta");
    const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    applyBrand("forest");
    set.mockRestore();
    expect(localStorage.getItem("cluckwork.brand")).toBe("terracotta");
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBeNull();
  });

  it("survives the legacy removal throwing after a successful write", () => {
    localStorage.setItem("cluckwork.brand", "terracotta");
    const remove = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyBrand("forest")).not.toThrow();
    remove.mockRestore();
    // The slug key still landed — the legacy removal is hygiene, not the write.
    expect(localStorage.getItem("cluckwork.brand:sunny-acres")).toBe("forest");
  });
});
