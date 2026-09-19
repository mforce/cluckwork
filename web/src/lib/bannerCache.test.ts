import { describe, it, expect, vi, beforeEach } from "vitest";
import {
  bannerKeyFor, cacheBannerBytes, clearBannerIfWrongAccount, forgetBannerFor, readCachedBannerBlob,
} from "./bannerCache";
import { bindAccount, bindFarm, clearBoundAccount, farmBindingToken } from "../auth/tokenStore";
import * as tokenStore from "../auth/tokenStore";

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
// request's error event). `error: null` exercises openDb's own
// `req.error ?? new Error(...)` fallback — a real IDBRequest CAN fire
// onerror with no `.error` set.
function stubFailingIndexedDbOpen(error: Error | null = new Error("blocked")) {
  return vi.spyOn(indexedDB, "open").mockImplementation(() => {
    const req = new EventTarget() as unknown as IDBOpenDBRequest;
    Object.defineProperty(req, "error", { value: error, configurable: true });
    queueMicrotask(() => req.onerror?.(new Event("error") as unknown as never));
    return req;
  });
}

// Codex review — open succeeds, but every transaction it hands out then
// ABORTS without its request ever erroring: the shape getBannerRecord's and
// deleteBannerRecord's `tx.onabort` backstops exist for (a real abort
// usually errors the request too, but nothing guaranteed that, and the
// promise hung forever without this). `error: null` exercises the
// `tx.error ?? new Error(...)` fallback.
function stubAbortingTransaction(error: Error | null = new Error("aborted")) {
  return vi.spyOn(indexedDB, "open").mockImplementation(() => {
    const openReq = {} as IDBOpenDBRequest;
    const db = {
      transaction: () => {
        const tx: Record<string, unknown> = {
          error,
          objectStore: () => ({
            get: () => ({}) as IDBRequest,
            delete: () => ({}) as IDBRequest,
          }),
        };
        queueMicrotask(() => (tx.onabort as (() => void) | undefined)?.());
        return tx as unknown as IDBTransaction;
      },
      close: () => {},
    } as unknown as IDBDatabase;
    Object.defineProperty(openReq, "result", { value: db, configurable: true });
    queueMicrotask(() => (openReq as unknown as { onsuccess?: () => void }).onsuccess?.());
    return openReq;
  });
}

// Open succeeds, and the readonly transaction's GET REQUEST itself errors —
// distinct from stubAbortingTransaction above, which aborts the transaction
// without ever giving the request's own handlers a chance to fire.
// Exercises getBannerRecord's `req.onerror` path, never reached by any
// other stub here. `error: null` exercises its `??` fallback.
function stubGetRequestErrors(error: Error | null) {
  return vi.spyOn(indexedDB, "open").mockImplementation(() => {
    const openReq = {} as IDBOpenDBRequest;
    const db = {
      transaction: () => {
        const tx: Record<string, unknown> = {
          objectStore: () => ({
            get: () => {
              const req: Record<string, unknown> = {};
              Object.defineProperty(req, "error", { value: error, configurable: true });
              queueMicrotask(() => (req.onerror as (() => void) | undefined)?.());
              return req as unknown as IDBRequest;
            },
          }),
        };
        return tx as unknown as IDBTransaction;
      },
      close: () => {},
    } as unknown as IDBDatabase;
    Object.defineProperty(openReq, "result", { value: db, configurable: true });
    queueMicrotask(() => (openReq as unknown as { onsuccess?: () => void }).onsuccess?.());
    return openReq;
  });
}

// Codex review — the read transaction (getBannerRecord, inside
// clearBannerIfWrongAccount) succeeds and returns `record`, but the
// FOLLOWING write transaction (deleteBannerRecord) either ERRORS or ABORTS.
// Reached only after a real prior read, which stubAbortingTransaction and
// stubGetRequestErrors above can't do (both fail the very FIRST transaction,
// before any read completes).
function stubReadSucceedsThenWriteOutcome(
  record: unknown,
  outcome: "error" | "abort",
  error: Error | null,
) {
  return vi.spyOn(indexedDB, "open").mockImplementation(() => {
    const openReq = {} as IDBOpenDBRequest;
    const db = {
      transaction: (_name: string, mode: string) => {
        if (mode === "readonly") {
          const tx: Record<string, unknown> = {
            objectStore: () => ({
              get: () => {
                const req: Record<string, unknown> = { result: record };
                queueMicrotask(() => {
                  (req.onsuccess as (() => void) | undefined)?.();
                  queueMicrotask(() => (tx.oncomplete as (() => void) | undefined)?.());
                });
                return req as unknown as IDBRequest;
              },
            }),
          };
          return tx as unknown as IDBTransaction;
        }
        const tx: Record<string, unknown> = {
          error,
          objectStore: () => ({ delete: () => ({}) as IDBRequest }),
        };
        queueMicrotask(() => {
          const handler = outcome === "error" ? tx.onerror : tx.onabort;
          (handler as (() => void) | undefined)?.();
        });
        return tx as unknown as IDBTransaction;
      },
      close: () => {},
    } as unknown as IDBDatabase;
    Object.defineProperty(openReq, "result", { value: db, configurable: true });
    queueMicrotask(() => (openReq as unknown as { onsuccess?: () => void }).onsuccess?.());
    return openReq;
  });
}

// Codex review — open succeeds, but the WRITE transaction (put or delete —
// the object store calls that matter are unused return values either way)
// either ERRORS or ABORTS rather than completing. Covers putBanner's
// `tx.onerror`/`tx.onabort` from the caller side (finding 3's corrected
// comment: a failed REPLACEMENT write leaves the previous record in place)
// and, with a null `error`, their `??` fallbacks.
function stubWriteTransactionOutcome(outcome: "error" | "abort", error: Error | null) {
  return vi.spyOn(indexedDB, "open").mockImplementation(() => {
    const openReq = {} as IDBOpenDBRequest;
    const db = {
      transaction: () => {
        const tx: Record<string, unknown> = {
          error,
          objectStore: () => ({
            put: () => ({}) as IDBRequest,
            delete: () => ({}) as IDBRequest,
          }),
        };
        queueMicrotask(() => {
          const handler = outcome === "error" ? tx.onerror : tx.onabort;
          (handler as (() => void) | undefined)?.();
        });
        return tx as unknown as IDBTransaction;
      },
      close: () => {},
    } as unknown as IDBDatabase;
    Object.defineProperty(openReq, "result", { value: db, configurable: true });
    queueMicrotask(() => (openReq as unknown as { onsuccess?: () => void }).onsuccess?.());
    return openReq;
  });
}

// Real open() for the first `n` calls (delegating to the fake IndexedDB
// already installed for this test), then every open after that fails —
// lets a test succeed at ONE IndexedDB operation (e.g. the write inside
// cacheBannerBytes) and then fail at the NEXT one (e.g. its own post-write
// cleanup delete), which a single blanket stub can't express.
function stubOpenFailsAfterNCalls(n: number) {
  const realOpen = indexedDB.open.bind(indexedDB);
  let calls = 0;
  return vi.spyOn(indexedDB, "open").mockImplementation((name: string, version?: number) => {
    calls += 1;
    if (calls <= n) return realOpen(name, version);
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

    // Codex review, correcting finding 3's comment — a failed REPLACEMENT
    // write does not clear the previous banner; the put simply never
    // landed, so whatever was there survives.
    it("leaves the previous banner in place when a replacement write errors", async () => {
      await cacheBannerBytes(blob("original"), farmBindingToken());
      const spy = stubWriteTransactionOutcome("error", new Error("quota exceeded"));
      await cacheBannerBytes(blob("replacement-that-fails"), farmBindingToken());
      spy.mockRestore();
      expect(await blobText(await readCachedBannerBlob("sunny-acres"))).toBe("original");
    });

    it("leaves the previous banner in place when a replacement write ABORTS (not just errors)", async () => {
      await cacheBannerBytes(blob("original"), farmBindingToken());
      const spy = stubWriteTransactionOutcome("abort", new Error("aborted"));
      await cacheBannerBytes(blob("replacement-that-aborts"), farmBindingToken());
      spy.mockRestore();
      expect(await blobText(await readCachedBannerBlob("sunny-acres"))).toBe("original");
    });

    // The `tx.error ?? new Error(...)` fallback on both the write path's
    // onerror and onabort handlers — a real IDBTransaction can fire either
    // event with no `.error` set.
    it("still fails cleanly when a write errors or aborts with no error object", async () => {
      let spy = stubWriteTransactionOutcome("error", null);
      await expect(cacheBannerBytes(blob(), farmBindingToken())).resolves.not.toThrow();
      spy.mockRestore();
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();

      spy = stubWriteTransactionOutcome("abort", null);
      await expect(cacheBannerBytes(blob(), farmBindingToken())).resolves.not.toThrow();
      spy.mockRestore();
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    // Codex review — the SECOND staleness check (right after blob.arrayBuffer()
    // resolves, before the write starts) exists for a rebind that the first,
    // entry-time check is too early to see. Driven by mocking
    // farmBindingToken's return sequence for the same reason the post-write
    // test below does: racing the real async timing to land a rebind in one
    // specific microtask window is not something a test should depend on.
    it("writes nothing when the token goes stale between reading the blob and writing it", async () => {
      const tokenAtStart = farmBindingToken();
      const spy = vi.spyOn(tokenStore, "farmBindingToken");
      spy.mockReturnValueOnce(tokenAtStart).mockReturnValue("a-different-binding-now");
      await cacheBannerBytes(blob("never-written"), tokenAtStart);
      spy.mockRestore();
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    // Codex review — the third staleness check (after the write itself
    // lands) exists for exactly this: a rebind that happens too late for
    // the earlier two checks to catch, discovered only once the write is
    // done. Driven by mocking farmBindingToken's return sequence, since
    // racing the fake IndexedDB's real timing to land a rebind in that
    // specific microtask window is not something a test should depend on.
    it("deletes the record it just wrote once the token no longer matches by the time the write itself completes", async () => {
      const tokenAtStart = farmBindingToken();
      const spy = vi.spyOn(tokenStore, "farmBindingToken");
      spy
        .mockReturnValueOnce(tokenAtStart) // entry guard
        .mockReturnValueOnce(tokenAtStart) // post-arrayBuffer() guard — write proceeds
        .mockReturnValue("a-different-binding-entirely"); // post-write guard — triggers cleanup
      await cacheBannerBytes(blob("mid-write"), tokenAtStart);
      spy.mockRestore();
      // The cleanup delete is fire-and-forget (`void deleteBannerRecord(...)`),
      // so it may still be in flight the instant cacheBannerBytes returns.
      await vi.waitFor(async () => expect(await readCachedBannerBlob("sunny-acres")).toBeNull());
    });

    // Codex review — the post-write cleanup delete is itself
    // `void deleteBannerRecord(slug).catch(() => {})`: fire-and-forget, so a
    // failure there must be swallowed rather than becoming an unhandled
    // rejection. Proven by letting the WRITE succeed (via the real fake
    // IndexedDB) and then failing only the cleanup delete's own open() —
    // the just-written record survives, since the delete that would have
    // removed it never completed.
    it("swallows a failed cleanup delete after a mid-write rebind, leaving the just-written record in place", async () => {
      const tokenAtStart = farmBindingToken();
      const tokenSpy = vi.spyOn(tokenStore, "farmBindingToken");
      tokenSpy
        .mockReturnValueOnce(tokenAtStart)
        .mockReturnValueOnce(tokenAtStart)
        .mockReturnValue("a-different-binding-entirely");
      const openSpy = stubOpenFailsAfterNCalls(1); // the write's own open() succeeds; the cleanup delete's open() fails
      await expect(cacheBannerBytes(blob("mid-write"), tokenAtStart)).resolves.not.toThrow();
      tokenSpy.mockRestore();
      openSpy.mockRestore();
      expect(await blobText(await readCachedBannerBlob("sunny-acres"))).toBe("mid-write");
    });

    // The feature check (openDb) is exercised through every "IndexedDB is
    // unavailable" test above via a FAILING open — this is the OTHER shape,
    // where `indexedDB` doesn't exist on `globalThis` at all (some browsers'
    // private mode), which never even calls `.open()`.
    it("does nothing when IndexedDB does not exist on this device at all", async () => {
      const original = indexedDB;
      vi.stubGlobal("indexedDB", undefined);
      await expect(cacheBannerBytes(blob(), farmBindingToken())).resolves.not.toThrow();
      vi.stubGlobal("indexedDB", original);
      expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
    });

    // The `req.error ?? new Error(...)` fallback on openDb's own onerror —
    // a real IDBOpenDBRequest can fire onerror with no `.error` set.
    it("still fails cleanly when the open request errors with no error object", async () => {
      const spy = stubFailingIndexedDbOpen(null);
      await expect(cacheBannerBytes(blob(), farmBindingToken())).resolves.not.toThrow();
      spy.mockRestore();
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

    // Codex review — getBannerRecord's tx.onabort backstop. Without it, an
    // abort that never errors the request left this promise pending forever
    // instead of resolving; this test would time out on that version.
    it("returns null (rather than hanging) when the read transaction aborts without erroring", async () => {
      const spy = stubAbortingTransaction();
      await expect(readCachedBannerBlob("sunny-acres")).resolves.toBeNull();
      spy.mockRestore();
    });

    // The `tx.error ?? new Error(...)` fallback on the read path's onabort
    // backstop — a real abort can fire with no `.error` set.
    it("returns null when the read transaction aborts with no error object", async () => {
      const spy = stubAbortingTransaction(null);
      await expect(readCachedBannerBlob("sunny-acres")).resolves.toBeNull();
      spy.mockRestore();
    });

    // Codex review — getBannerRecord's `req.onerror` path: the GET REQUEST
    // itself errors, distinct from the transaction aborting above (which
    // never lets the request's own handlers fire).
    it("returns null when the read request itself errors", async () => {
      const spy = stubGetRequestErrors(new Error("boom"));
      await expect(readCachedBannerBlob("sunny-acres")).resolves.toBeNull();
      spy.mockRestore();
    });

    // The `req.error ?? new Error(...)` fallback on that same path.
    it("returns null when the read request errors with no error object", async () => {
      const spy = stubGetRequestErrors(null);
      await expect(readCachedBannerBlob("sunny-acres")).resolves.toBeNull();
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

    // Codex review — deleteBannerRecord's tx.onabort backstop, reached only
    // after a genuine mismatched read (a stub that aborts the very first
    // transaction, like the one above, never gets this far).
    it("resolves (rather than hanging) when the mismatch is confirmed but the delete transaction aborts without erroring", async () => {
      const spy = stubReadSucceedsThenWriteOutcome(
        { accountId: "acct-B", bytes: new ArrayBuffer(0), type: "image/png" },
        "abort",
        new Error("aborted"),
      );
      await expect(clearBannerIfWrongAccount("sunny-acres", "acct-A")).resolves.not.toThrow();
      spy.mockRestore();
    });

    // deleteBannerRecord's `tx.onerror` path — never reached by the abort
    // case above, since that fires onabort instead.
    it("resolves (rather than hanging) when the mismatch is confirmed but the delete transaction errors", async () => {
      const spy = stubReadSucceedsThenWriteOutcome(
        { accountId: "acct-B", bytes: new ArrayBuffer(0), type: "image/png" },
        "error",
        new Error("boom"),
      );
      await expect(clearBannerIfWrongAccount("sunny-acres", "acct-A")).resolves.not.toThrow();
      spy.mockRestore();
    });

    // The `tx.error ?? new Error(...)` fallback on both of the above.
    it("resolves cleanly when the delete errors or aborts with no error object", async () => {
      const record = { accountId: "acct-B", bytes: new ArrayBuffer(0), type: "image/png" };
      let spy = stubReadSucceedsThenWriteOutcome(record, "error", null);
      await expect(clearBannerIfWrongAccount("sunny-acres", "acct-A")).resolves.not.toThrow();
      spy.mockRestore();

      spy = stubReadSucceedsThenWriteOutcome(record, "abort", null);
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
