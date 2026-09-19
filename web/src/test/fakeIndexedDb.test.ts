import { describe, it, expect, vi } from "vitest";
import { createFakeIndexedDb } from "./fakeIndexedDb";

describe("createFakeIndexedDb", () => {
  // Codex review, #833 follow-up — open() used to capture `databases.get(name)`
  // at CALL time rather than inside its own queued microtask. Two opens issued
  // back-to-back synchronously (the shape bannerCache.ts produces whenever two
  // of its exported functions are called without an await between them, e.g.
  // Settings' fire-and-forget banner cache racing a Login read) both saw "no
  // existing database" and each created its own — a write through one
  // invisible to a read through the other.
  it("gives two open() calls issued in the same tick the SAME database", async () => {
    const idb = createFakeIndexedDb();
    let firstDb: unknown;
    let secondDb: unknown;

    const req1 = idb.open("shared", 1) as unknown as IDBOpenDBRequest;
    req1.onupgradeneeded = () => {
      (req1.result as IDBDatabase).createObjectStore("store");
    };
    req1.onsuccess = () => {
      firstDb = req1.result;
    };

    // Issued in the SAME synchronous tick as req1, before either request's
    // queued microtask has had a chance to run.
    const req2 = idb.open("shared", 1) as unknown as IDBOpenDBRequest;
    let secondSawUpgrade = false;
    req2.onupgradeneeded = () => {
      secondSawUpgrade = true;
    };
    req2.onsuccess = () => {
      secondDb = req2.result;
    };

    await vi.waitFor(() => {
      expect(firstDb).toBeDefined();
      expect(secondDb).toBeDefined();
    });

    expect(secondDb).toBe(firstDb);
    // Only the FIRST open of a fresh database creates it; the second must
    // see it already exists rather than creating (and upgrading) a sibling.
    expect(secondSawUpgrade).toBe(false);
  });
});
