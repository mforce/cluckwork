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
});
