import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  canonicalFarmCode,
  isFarmCode,
  readFarmCodes,
  rememberFarmCode,
} from "./farmCodeCache";

const KEY = "cluckwork.farmCodes";

// Assert a fixture REALLY is what we claim before building assertions on it.
const GENUINELY_INVALID = ["ab", "-abc", "abc-", "sunny_acres", "a".repeat(33), "sunny acres", ""];

describe("farmCodeCache", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("isFarmCode rejects genuinely-invalid values", () => {
    for (const value of GENUINELY_INVALID) {
      expect(isFarmCode(value), JSON.stringify(value)).toBe(false);
    }
  });

  it("isFarmCode accepts lowercase canonical farm-code shapes", () => {
    // isFarmCode validates the RAW shape and does not normalise, so only the
    // lowercase canonical forms pass. "Sunny-Acres" / " sunny-acres " are valid
    // FARM CODES (they sign in), but only after normalizeFarmCode canonicalises
    // them — covered by the canonicalFarmCode assertion below.
    const lowercase = ["sunny-acres", "abc", "a".repeat(32)];
    for (const value of lowercase) {
      expect(isFarmCode(value), JSON.stringify(value)).toBe(true);
    }
  });

  it("a trailing newline fails isFarmCode but canonicalFarmCode strips it first (#535 review round 1)", () => {
    // REVIEW-CORRECTED fixture. The review note claimed `$` without the `m`
    // flag matches before a single trailing newline and so `isFarmCode(...)`
    // returns TRUE for "sunny-acres\n". That is NOT JavaScript semantics: a
    // non-multiline `$` matches only at the true end of input (the match-before-
    // trailing-newline behaviour is the `/m` flag's, or Python's `re`). The
    // executed behaviour against this pattern is FALSE. The note's underlying
    // concern stands — someone calling isFarmCode directly with a stray newline
    // — so both halves are pinned here to whatever is true today. Harmless in
    // practice only because every live caller goes through canonicalFarmCode,
    // whose .trim() strips a trailing newline first — asserted on the second line.
    expect(isFarmCode("sunny-acres\n")).toBe(false);
    // canonicalFarmCode trims before validating, so the padded value is accepted
    // and returned canonical — the only path live callers ever take.
    expect(canonicalFarmCode("sunny-acres\n")).toBe("sunny-acres");
  });

  it("canonicalFarmCode accepts case-mangled and space-padded values", () => {
    expect(canonicalFarmCode("Sunny-Acres")).toBe("sunny-acres");
    expect(canonicalFarmCode(" sunny-acres ")).toBe("sunny-acres");
  });

  it("canonicalFarmCode rejects non-string values", () => {
    expect(canonicalFarmCode(null)).toBeNull();
    expect(canonicalFarmCode(123)).toBeNull();
    expect(canonicalFarmCode({ farm: "sunny-acres" })).toBeNull();
    expect(canonicalFarmCode(["sunny-acres"])).toBeNull();
  });

  it("mixed-case and space-padded values normalise on write", () => {
    rememberFarmCode(" Sunny-Acres ");
    const stored = JSON.parse(localStorage.getItem(KEY) ?? "[]");
    expect(stored).toEqual(["sunny-acres"]);
  });

  it("mixed-case and space-padded values normalise and dedupe on read", () => {
    localStorage.setItem(KEY, JSON.stringify(["Sunny-Acres", " sunny-acres "]));
    expect(readFarmCodes()).toEqual(["sunny-acres"]);
  });

  it("write-side cap: keeps the most-recent 10 of 12 in raw storage, dropping the two oldest", () => {
    const codes = Array.from({ length: 12 }, (_, i) => `farm${String(i + 1).padStart(2, "0")}`);
    for (const code of codes) rememberFarmCode(code);
    const stored = JSON.parse(localStorage.getItem(KEY) ?? "[]");
    expect(stored).toHaveLength(10);
    // most-recent-first, so farm12 is first and the two oldest (farm01, farm02) are dropped
    expect(stored[0]).toBe("farm12");
    expect(stored).not.toContain("farm01");
    expect(stored).not.toContain("farm02");
  });

  it("write-side dedupe + most-recent-first in raw storage", () => {
    rememberFarmCode("farm-a");
    rememberFarmCode("farm-b");
    rememberFarmCode("farm-a"); // re-remembered: moves to front, no duplicate
    const stored = JSON.parse(localStorage.getItem(KEY) ?? "[]");
    expect(stored).toEqual(["farm-a", "farm-b"]);
  });

  it("read-side cap: caps a hand-written oversized array at 10", () => {
    const codes = Array.from({ length: 12 }, (_, i) => `farm${String(i + 1).padStart(2, "0")}`);
    localStorage.setItem(KEY, JSON.stringify(codes));
    expect(readFarmCodes()).toHaveLength(10);
  });

  it("read-side dedupe: drops duplicates from a hand-written array", () => {
    localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b", "farm-a", "farm-c"]));
    expect(readFarmCodes()).toEqual(["farm-a", "farm-b", "farm-c"]);
  });

  it("non-array JSON parses to []", () => {
    localStorage.setItem(KEY, JSON.stringify({ farm: "farm-a" }));
    expect(readFarmCodes()).toEqual([]);
  });

  it("unparseable JSON parses to []", () => {
    localStorage.setItem(KEY, "{not json");
    expect(readFarmCodes()).toEqual([]);
  });

  it("getItem throwing parses to []", () => {
    const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("storage unavailable");
    });
    expect(readFarmCodes()).toEqual([]);
    spy.mockRestore();
  });

  it("setItem throwing does not throw out", () => {
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exhausted");
    });
    expect(() => rememberFarmCode("farm-a")).not.toThrow();
    spy.mockRestore();
  });

  it("rememberFarmCode with an invalid value is a no-op leaving an existing list intact", () => {
    localStorage.setItem(KEY, JSON.stringify(["farm-a"]));
    rememberFarmCode("ab"); // genuinely invalid
    expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-a"]);
  });

  // #535 (codex P2) — the true cross-tab lost update is NOT expressible in
  // Vitest: it needs two independent JS contexts sharing one localStorage, and
  // jsdom has a single context where the read-modify-write is synchronous. These
  // are STRUCTURAL guards instead, pinning that rememberFarmCode re-reads the
  // roster INSIDE the lock callback (not merely that a lock was requested), and
  // that every degradation path still writes. They would not reproduce the race
  // itself, but they would go red if the lock or the inside-the-lock re-read were
  // removed.
  describe("rememberFarmCode under Web Locks (#535 P2)", () => {
    const LOCK = "cluckwork.farmCodes.write";

    // Build a navigator stub exposing only `locks` so globalThis.navigator?.locks
    // resolves, without replacing the rest of the jsdom navigator.
    function stubLocks(impl: {
      request?: (
        name: string,
        cb: () => void,
      ) => Promise<unknown> | unknown;
    }): void {
      const navigatorStub = { ...globalThis.navigator } as Navigator & { locks?: LockManager };
      navigatorStub.locks = { request: (name, cb) => impl.request?.(name, cb as () => void) } as LockManager;
      vi.stubGlobal("navigator", navigatorStub);
    }

    it("re-reads INSIDE the lock callback: a roster change that lands while the lock is held is not overwritten", async () => {
      localStorage.setItem(KEY, JSON.stringify(["farm-a"]));
      let callbackSawConcurrentWrite = false;
      let concurrentFarmSurvived = false;
      stubLocks({
        request: (name, cb) => {
          expect(name).toBe(LOCK);
          // Simulate a concurrent tab that completes its login AFTER this lock
          // was acquired but BEFORE this callback runs. Only a re-read inside
          // write() — i.e. after the lock — sees it. A roster read taken before
          // the lock was acquired (the mutation this guards against) would already
          // have been computed and would overwrite and drop it.
          const concurrent = ["farm-a", "farm-c"];
          localStorage.setItem(KEY, JSON.stringify(concurrent));
          cb();
          const after = JSON.parse(localStorage.getItem(KEY) ?? "[]") as string[];
          callbackSawConcurrentWrite = after.includes("farm-c");
          concurrentFarmSurvived = after.includes("farm-c");
          return Promise.resolve();
        },
      });
      await rememberFarmCode("farm-b");
      expect(callbackSawConcurrentWrite).toBe(true);
      expect(concurrentFarmSurvived).toBe(true);
    });

    it("with navigator.locks undefined, the code is still written (degradation path)", async () => {
      localStorage.setItem(KEY, JSON.stringify(["farm-a"]));
      await rememberFarmCode("farm-b");
      await expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-b", "farm-a"]);
    });

    it("when locks.request rejects, rememberFarmCode does NOT reject and the code is still written", async () => {
      stubLocks({
        request: () => Promise.reject(new Error("lock unavailable")),
      });
      await expect(rememberFarmCode("farm-c")).resolves.toBeUndefined();
      await expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-c"]);
    });

    it("await rememberFarmCode with an invalid value resolves and never throws", async () => {
      stubLocks({
        request: (_name, cb) => {
          cb();
          return Promise.resolve();
        },
      });
      await expect(rememberFarmCode("ab")).resolves.toBeUndefined();
    });
  });
});
