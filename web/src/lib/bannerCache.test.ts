import { describe, it, expect, vi, beforeEach } from "vitest";
import {
  bannerKeyFor, cacheBannerBytes, clearBannerIfWrongAccount, forgetBannerFor, readCachedBannerBlob,
} from "./bannerCache";
import { bindAccount, bindFarm, clearBoundAccount, farmBindingToken } from "../auth/tokenStore";

beforeEach(() => {
  bindAccount("acct-A");
  bindFarm("sunny-acres");
});

const blob = (data = "fake-image-bytes") => new Blob([data], { type: "image/png" });

async function blobText(b: Blob | null): Promise<string | null> {
  return b === null ? null : b.text();
}

// A stub for `indexedDB.open` that fires the request's onerror asynchronously
// — the shape every IndexedDB failure test below needs (open itself never
// throws synchronously in a real browser; it always fails through the
// request's error event).
function stubFailingIndexedDbOpen() {
  return vi.spyOn(indexedDB, "open").mockImplementation(() => {
    const req = new EventTarget() as unknown as IDBOpenDBRequest;
    Object.defineProperty(req, "error", { value: new Error("blocked"), configurable: true });
    queueMicrotask(() => req.onerror?.(new Event("error") as unknown as never));
    return req;
  });
}

describe("bannerCache", () => {
  describe("cacheBannerBytes — the cached path", () => {
    it("caches the blob under the bound farm's key, readable back byte-for-byte", async () => {
      await cacheBannerBytes(blob("hello"), farmBindingToken());
      const cached = await readCachedBannerBlob("sunny-acres");
      expect(await blobText(cached)).toBe("hello");
    });

    it("does nothing when the token is stale (the tab rebound while the write was in flight)", async () => {
      const staleToken = farmBindingToken();
      bindFarm("other-farm"); // rebinding changes the token
      await cacheBannerBytes(blob(), staleToken);
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
      expect(await readCachedBannerBlob("other-farm")).toBeNull();
    });

    it("caches nothing when the tab is unbound (a fresh tab restored from the refresh cookie)", async () => {
      clearBoundAccount();
      // farmBindingToken() still returns a real value with no binding — the
      // call must still no-op, matching applyBrand's own contract.
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    // #833 finding 5 — a defensive ceiling, matching
    // ImageSanitizer.MaxBannerByteLengthCeiling (15 MiB), against a future
    // caller handing this function something a live upload could never have
    // produced. Constructing a real 15 MiB+1 Blob is cheap and exact — no
    // need to mock `.size`.
    it("refuses a blob over the 15 MiB ceiling", async () => {
      const oversized = new Blob([new Uint8Array(15 * 1024 * 1024 + 1)]);
      await cacheBannerBytes(oversized, farmBindingToken());
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    it("does nothing when IndexedDB is unavailable", async () => {
      const spy = stubFailingIndexedDbOpen();
      await expect(cacheBannerBytes(blob(), farmBindingToken())).resolves.not.toThrow();
      spy.mockRestore();
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    // #833 findings 2/3 — the account id travels WITH the blob, not just the
    // slug, so a later sign-in can tell whether this entry still belongs to
    // the account that wrote it. Proven indirectly through
    // clearBannerIfWrongAccount, since the record shape is this module's
    // own implementation detail.
    it("stores the bound account id alongside the blob", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      await clearBannerIfWrongAccount("sunny-acres", "acct-A");
      expect(await readCachedBannerBlob("sunny-acres")).not.toBeNull(); // matching id: untouched
    });
  });

  describe("readCachedBannerBlob — the first-visit and cached paths", () => {
    it("first visit: returns null when nothing is cached for that farm code", async () => {
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    it("cached: returns the stored blob for a matching farm code", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(await readCachedBannerBlob("sunny-acres")).not.toBeNull();
    });

    // #833 finding 2 — keyed to the exact code, not "how many farms this
    // device remembers": a DIFFERENT typed/picked code must never resolve
    // to another farm's cached image.
    it("returns null for a code that does not match any cached entry", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(await readCachedBannerBlob("a-different-farm")).toBeNull();
    });

    it("matches case-insensitively and trims surrounding whitespace, like the server's own lookup", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(await readCachedBannerBlob("  Sunny-Acres  ")).not.toBeNull();
    });

    it("returns null for a blank field", async () => {
      expect(await readCachedBannerBlob("")).toBeNull();
      expect(await readCachedBannerBlob("   ")).toBeNull();
    });

    it("returns null when IndexedDB cannot be read", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      const spy = stubFailingIndexedDbOpen();
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
      spy.mockRestore();
    });
  });

  describe("forgetBannerFor — the forget path", () => {
    it("removes the cached banner for the given farm", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      expect(await readCachedBannerBlob("sunny-acres")).not.toBeNull();
      forgetBannerFor("sunny-acres");
      await vi.waitFor(async () => expect(await readCachedBannerBlob("sunny-acres")).toBeNull());
    });

    it("leaves a DIFFERENT farm's cached banner untouched", async () => {
      await cacheBannerBytes(blob(), farmBindingToken());
      bindFarm("other-farm");
      await cacheBannerBytes(blob("other-bytes"), farmBindingToken());

      forgetBannerFor("sunny-acres");
      await vi.waitFor(async () => expect(await readCachedBannerBlob("sunny-acres")).toBeNull());
      expect(await readCachedBannerBlob("other-farm")).not.toBeNull();
    });

    it("does not throw when IndexedDB is unavailable", () => {
      const spy = vi.spyOn(indexedDB, "open").mockImplementation(() => {
        throw new Error("blocked");
      });
      expect(() => forgetBannerFor("sunny-acres")).not.toThrow();
      spy.mockRestore();
    });
  });

  describe("clearBannerIfWrongAccount — #833 findings 2/3", () => {
    it("deletes the entry when the signed-in account differs from the one that wrote it", async () => {
      await cacheBannerBytes(blob(), farmBindingToken()); // written under acct-A
      await clearBannerIfWrongAccount("sunny-acres", "acct-B");
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    it("leaves the entry when the signed-in account matches", async () => {
      await cacheBannerBytes(blob(), farmBindingToken()); // written under acct-A
      await clearBannerIfWrongAccount("sunny-acres", "acct-A");
      expect(await readCachedBannerBlob("sunny-acres")).not.toBeNull();
    });

    it("does nothing when there is no entry to reconcile", async () => {
      await expect(clearBannerIfWrongAccount("sunny-acres", "acct-A")).resolves.not.toThrow();
    });

    it("does not throw when IndexedDB is unavailable", async () => {
      const spy = vi.spyOn(indexedDB, "open").mockImplementation(() => {
        throw new Error("blocked");
      });
      await expect(clearBannerIfWrongAccount("sunny-acres", "acct-A")).resolves.not.toThrow();
      spy.mockRestore();
    });
  });

  describe("bannerKeyFor", () => {
    it("is currently the identity function over the slug", () => {
      expect(bannerKeyFor("sunny-acres")).toBe("sunny-acres");
    });
  });
});
