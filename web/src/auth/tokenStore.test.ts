import { describe, it, expect } from "vitest";
import { loadTokens, saveTokens, clearTokens, KEY } from "./tokenStore";
import type { TokenPair } from "../api/types";

// localStorage is reset after every test in src/test/setup.ts.

const PAIR: TokenPair = { accessToken: "at", refreshToken: "rt", expiresAt: "2099-01-01T00:00:00Z" };

describe("tokenStore", () => {
  it("round-trips a saved token pair", () => {
    saveTokens(PAIR);
    expect(loadTokens()).toEqual(PAIR);
  });

  it("returns null when nothing is stored", () => {
    expect(loadTokens()).toBeNull();
  });

  it("overwrites an existing pair — the rotated token wins (refresh rotation)", () => {
    saveTokens(PAIR);
    const rotated = { accessToken: "at2", refreshToken: "rt2", expiresAt: "2099-06-01T00:00:00Z" };
    saveTokens(rotated);
    expect(loadTokens()).toEqual(rotated);
  });

  it("clearTokens removes the stored pair", () => {
    saveTokens(PAIR);
    clearTokens();
    expect(loadTokens()).toBeNull();
    expect(localStorage.getItem(KEY)).toBeNull();
  });

  it("evicts a corrupt entry and returns null (self-healing)", () => {
    localStorage.setItem(KEY, "{ not valid json");
    expect(loadTokens()).toBeNull();
    // the bad value is removed so it can't wedge every future load
    expect(localStorage.getItem(KEY)).toBeNull();
  });

  it("persists under the shared KEY the rest of the app reads", () => {
    saveTokens(PAIR);
    expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual(PAIR);
  });
});
