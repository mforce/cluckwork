import { describe, it, expect, vi } from "vitest";
import { createFakeIndexedDb } from "./fakeIndexedDb";

describe("createFakeIndexedDb", () => {
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
    expect(secondSawUpgrade).toBe(false);
  });
});
