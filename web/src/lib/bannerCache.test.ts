import { describe, it, expect, vi, beforeEach } from "vitest";
import { bannerKeyFor, cacheBannerBytes, forgetBannerFor, readCachedBanner } from "./bannerCache";
import { bindAccount, bindFarm, clearBoundAccount, farmBindingToken } from "../auth/tokenStore";

beforeEach(() => {
  localStorage.clear();
  bindAccount("acct-A");
  bindFarm("sunny-acres");
});

const blob = (data = "fake-image-bytes") => new Blob([data], { type: "image/png" });

describe("bannerCache", () => {
  describe("cacheBannerBytes — the cached path", () => {
    it("caches the blob as a data URL under the bound farm's key", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      const stored = localStorage.getItem(bannerKeyFor("sunny-acres"));
      expect(stored).not.toBeNull();
      expect(stored).toMatch(/^data:/);
    });

    it("does nothing when the token is stale (the tab rebound while the read was in flight)", async () => {
      const staleToken = farmBindingToken();
      bindFarm("other-farm"); // rebinding changes the token
      await cacheBannerBytes(blob(), staleToken);
      expect(localStorage.getItem(bannerKeyFor("sunny-acres"))).toBeNull();
      expect(localStorage.getItem(bannerKeyFor("other-farm"))).toBeNull();
    });

    it("caches nothing when the tab is unbound (a fresh tab restored from the refresh cookie)", async () => {
      clearBoundAccount();
      // farmBindingToken() still returns a real value with no binding — the
      // call must still no-op, matching applyBrand's own contract.
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(localStorage.getItem(bannerKeyFor("sunny-acres"))).toBeNull();
    });

    it("drops a half-written value when the storage write fails", async () => {
      const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
        throw new Error("quota exceeded");
      });
      await expect(cacheBannerBytes(blob(), farmBindingToken())).resolves.not.toThrow();
      spy.mockRestore();
      expect(localStorage.getItem(bannerKeyFor("sunny-acres"))).toBeNull();
    });

    it("survives removeItem also throwing after a failed write", async () => {
      const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
        throw new Error("quota exceeded");
      });
      const remove = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
        throw new Error("storage denied");
      });
      await expect(cacheBannerBytes(blob(), farmBindingToken())).resolves.not.toThrow();
      set.mockRestore();
      remove.mockRestore();
    });
  });

  describe("readCachedBanner — the first-visit and cached paths", () => {
    it("first visit: returns null when nothing is cached for the one remembered farm", () => {
      expect(readCachedBanner(["sunny-acres"])).toBeNull();
    });

    it("cached: returns the stored data URL for the one remembered farm", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(readCachedBanner(["sunny-acres"])).toMatch(/^data:/);
    });

    it("returns null with two or more remembered farms — which one is ambiguous", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(readCachedBanner(["sunny-acres", "other-farm"])).toBeNull();
    });

    it("returns null with zero remembered farms", () => {
      expect(readCachedBanner([])).toBeNull();
    });

    it("returns null when localStorage cannot be read", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
        throw new Error("storage denied");
      });
      expect(readCachedBanner(["sunny-acres"])).toBeNull();
      spy.mockRestore();
    });
  });

  describe("forgetBannerFor — the forget path", () => {
    it("removes the cached banner for the given farm", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(readCachedBanner(["sunny-acres"])).not.toBeNull();
      forgetBannerFor("sunny-acres");
      expect(readCachedBanner(["sunny-acres"])).toBeNull();
    });

    it("leaves a DIFFERENT farm's cached banner untouched", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      bindFarm("other-farm");
      await cacheBannerBytes(blob("other-bytes"), farmBindingToken());

      forgetBannerFor("sunny-acres");
      expect(readCachedBanner(["sunny-acres"])).toBeNull();
      expect(readCachedBanner(["other-farm"])).not.toBeNull();
    });

    it("does not throw when localStorage is unavailable", () => {
      const spy = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
        throw new Error("storage denied");
      });
      expect(() => forgetBannerFor("sunny-acres")).not.toThrow();
      spy.mockRestore();
    });
  });
});
