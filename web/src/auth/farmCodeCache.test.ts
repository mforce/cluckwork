import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  canonicalFarmCode,
  isFarmCode,
  readFarmCodes,
  rememberFarmCode,
  removeFarmCode,
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

  it("removes only the requested canonical farm from raw storage", async () => {
    localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b", "farm-c"]));
    await removeFarmCode(" Farm-B ");
    expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-a", "farm-c"]);
  });

  it("removes the only remembered farm from raw storage", async () => {
    localStorage.setItem(KEY, JSON.stringify(["farm-a"]));
    await removeFarmCode("farm-a");
    expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual([]);
  });

  it("removeFarmCode is best-effort when storage throws", async () => {
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exhausted");
    });
    await expect(removeFarmCode("farm-a")).resolves.toBeUndefined();
    spy.mockRestore();
  });

  // A read FAILURE is not an empty roster: getItem can throw while setItem
  // would still succeed (a quota-limited or partially-broken storage). Writing
  // "[]" in that case would destroy every remembered code this call never saw
  // — the removal must be a no-op, not a wipe.
  it("a throwing getItem makes the removal a no-op: it never writes an empty array over an unreadable roster", async () => {
    localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b"]));
    const getSpy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("storage unavailable");
    });
    const setSpy = vi.spyOn(Storage.prototype, "setItem"); // real implementation — would succeed
    await expect(removeFarmCode("farm-a")).resolves.toBeUndefined();
    expect(setSpy).not.toHaveBeenCalled();
    setSpy.mockRestore();
    getSpy.mockRestore();
    // The roster the storage actually holds is untouched.
    expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-a", "farm-b"]);
  });

  // The read-failure no-op must NOT swallow the malformed-storage behaviour:
  // READABLE malformed or non-array JSON is an EMPTY roster (mirrors
  // readFarmCodes), not a failed read — so the removal writes the raw "[]"
  // immediately, normalising the stored value in the same call. The raw
  // stored text is asserted DIRECTLY, not through a follow-up rememberFarmCode
  // (which would normalise the same storage and mask a no-op removal).
  it("unparseable stored JSON: the removal writes raw [] immediately (readable malformed is an empty roster, not a read failure)", async () => {
    localStorage.setItem(KEY, "{not json");
    await removeFarmCode("farm-a");
    // The stored value is now the empty JSON array, verbatim — a no-op
    // removal (or one that left the corrupt value) leaves "{not json" here.
    expect(localStorage.getItem(KEY)).toBe("[]");
  });

  it("non-array stored JSON: the removal writes raw [] immediately (readable non-array is an empty roster, not a read failure)", async () => {
    localStorage.setItem(KEY, JSON.stringify({ farm: "farm-a" }));
    await removeFarmCode("farm-a");
    // The object is not an array, so the roster is empty and the stored value
    // is replaced by the empty JSON array in the same call. A no-op removal
    // would leave the object here.
    expect(localStorage.getItem(KEY)).toBe("[]");
  });

  // The rewrite goes through the same canonicalisation as the read path: an
  // operator-typed, case-mangled or padded code that a hand-written array
  // carries is removed by its canonical form, and malformed entries are
  // dropped rather than propagated into the rewritten roster. Asserted on the
  // raw stored text so the normalisation is proven by the REMOVAL itself.
  it("a removal rewrites the roster through canonicalisation: drops invalid entries and matches case-mangled forms", async () => {
    localStorage.setItem(KEY, JSON.stringify(["ab", "Farm-A", "farm-b"]));
    await removeFarmCode(" farm-A ");
    expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-b"]);
  });

  // #598 review (codex P2) — removeFarmCode must normalise the readable raw
  // roster EXACTLY like readFarmCodes before removing: canonicalise, dedupe in
  // first-seen order, cap at 10, then filter. Capping BEFORE deduping (the old
  // shape) truncates a hand-written roster of ten farm-a variants so farm-b is
  // dropped by the cap and forgetting farm-a rewrites "[]", erasing farm-b.
  it("forgetting a duplicated head never erases the codes the read path shows: canonicalise, dedupe first, then cap at 10", async () => {
    const variants = [
      "farm-a",
      "Farm-A",
      "FARM-A",
      " farm-a ",
      "\tfarm-a",
      "farm-a\n",
      " farm-a\t",
      " farm-a\r\n",
      " farm-A ",
      "farm-a ",
    ];
    localStorage.setItem(KEY, JSON.stringify([...variants, "farm-b"]));
    // The read path shows BOTH farms: the ten variants collapse to farm-a and
    // farm-b survives the cap. The removal must agree with what is displayed.
    expect(readFarmCodes()).toEqual(["farm-a", "farm-b"]);
    await removeFarmCode("farm-a");
    expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-b"]);
    expect(readFarmCodes()).toEqual(["farm-b"]);
  });

  it("removeFarmCode with an invalid value resolves and leaves the roster untouched", async () => {
    localStorage.setItem(KEY, JSON.stringify(["farm-a"]));
    await expect(removeFarmCode("ab")).resolves.toBeUndefined();
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
      let seenName: string | undefined;
      stubLocks({
        request: (name, cb) => {
          seenName = name;
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
      // The lock name is asserted HERE, after the await, where nothing in the
      // callback can throw into rememberFarmCode's own catch and swallow it.
      expect(seenName).toBe(LOCK);
      expect(callbackSawConcurrentWrite).toBe(true);
      expect(concurrentFarmSurvived).toBe(true);
    });

    // #535 review round 3 — there is NO test here pinning the
    // `if (locks === undefined) { write(); return; }` branch, and that is
    // deliberate: the branch is behaviourally REDUNDANT with the catch below it.
    // With `locks` undefined, the guard writes and returns; delete the guard and
    // `locks.request` throws a TypeError which the catch answers with the
    // identical write. Same storage, same call count, no externally observable
    // difference — so no test can distinguish them, and a mutation deleting the
    // guard survives the whole suite. Verified by running it.
    //
    // The branch stays because it states intent: relying on `undefined.request()`
    // throwing is accidental control flow, and narrowing the catch later would
    // silently break browsers without navigator.locks. An earlier draft "pinned"
    // it with a `vi.fn()` that was never wired to anything and asserted it was
    // not called — a tautology that read as safety. Do not write that again.
    //
    // The degradation OUTCOME is covered: the rejection test below drives the
    // fallback write, and the two mutations that ARE observable — removing the
    // lock, and hoisting the re-read out of it — both redden the first test here.
    it("with navigator.locks absent, the code is still written", async () => {
      localStorage.setItem(KEY, JSON.stringify(["farm-a"]));
      const navigatorStub = { ...globalThis.navigator } as Navigator & { locks?: LockManager };
      // Deliberately NO .locks — this exercises the lock-free degradation path.
      vi.stubGlobal("navigator", navigatorStub);
      await rememberFarmCode("farm-b");
      expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-b", "farm-a"]);
    });

    it("when navigator.locks is present, a request IS made and the name is the roster lock", async () => {
      const request = vi.fn((_name: string) => Promise.resolve());
      const navigatorStub = { ...globalThis.navigator } as Navigator & { locks?: LockManager };
      navigatorStub.locks = { request: request as unknown as LockManager["request"] } as LockManager;
      vi.stubGlobal("navigator", navigatorStub);
      await rememberFarmCode("farm-b");
      expect(request).toHaveBeenCalledTimes(1);
      expect(request.mock.calls[0][0]).toBe(LOCK);
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

  // #587 — removeFarmCode reuses the same lock protocol and the same never-
  // rejects contract as rememberFarmCode. The same caveat applies: the true
  // cross-tab race is not expressible in a single jsdom context, so these are
  // structural guards that the removal re-reads INSIDE the lock and that every
  // degradation path still writes. No cross-tab ordering promise is asserted
  // for the absent/rejected-lock fallback — it is deliberately best-effort.
  describe("removeFarmCode under Web Locks (#587)", () => {
    const LOCK = "cluckwork.farmCodes.write";

    // Rebuilds the same navigator stub shape as the rememberFarmCode suite.
    function stubLocks(impl: {
      request?: (name: string, cb: () => void) => Promise<unknown> | unknown;
    }): void {
      const navigatorStub = { ...globalThis.navigator } as Navigator & { locks?: LockManager };
      navigatorStub.locks = { request: (name, cb) => impl.request?.(name, cb as () => void) } as LockManager;
      vi.stubGlobal("navigator", navigatorStub);
    }

    it("re-reads INSIDE the lock callback: a roster change that lands while the lock is held is not overwritten", async () => {
      localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b"]));
      let seenName: string | undefined;
      let concurrentSurvived = false;
      stubLocks({
        request: (name, cb) => {
          seenName = name;
          // A concurrent tab completing a login for farm-c after this lock was
          // acquired. Only a re-read inside the write callback sees it; a
          // roster read taken before the lock was acquired would clobber it.
          localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b", "farm-c"]));
          cb();
          concurrentSurvived = (JSON.parse(localStorage.getItem(KEY) ?? "[]") as string[]).includes("farm-c");
          return Promise.resolve();
        },
      });
      await removeFarmCode("farm-b");
      // Asserted after the await, where nothing in the callback can throw into
      // removeFarmCode's own catch and swallow it.
      expect(seenName).toBe(LOCK);
      expect(concurrentSurvived).toBe(true);
      // farm-b is gone; the concurrently-remembered farm-c survives.
      expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-a", "farm-c"]);
    });

    it("with navigator.locks absent, the removal is still written", async () => {
      localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b"]));
      const navigatorStub = { ...globalThis.navigator } as Navigator & { locks?: LockManager };
      vi.stubGlobal("navigator", navigatorStub);
      await removeFarmCode("farm-a");
      expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-b"]);
    });

    it("when locks.request rejects, removeFarmCode does NOT reject and the filtered array is still written", async () => {
      localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b"]));
      stubLocks({
        request: () => Promise.reject(new Error("lock unavailable")),
      });
      await expect(removeFarmCode("farm-b")).resolves.toBeUndefined();
      expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-a"]);
    });
  });
});
