import { describe, it, expect } from "vitest";
import { getAccessToken, setAccessToken, clearAccessToken, purgeLegacyTokens } from "./tokenStore";

// The in-memory access token is reset after every test in src/test/setup.ts.

describe("tokenStore (in-memory access token, #145)", () => {
  it("starts empty", () => {
    expect(getAccessToken()).toBeNull();
  });

  it("round-trips the access token", () => {
    setAccessToken("at1");
    expect(getAccessToken()).toBe("at1");
  });

  it("overwrites on rotation — the newest token wins", () => {
    setAccessToken("at1");
    setAccessToken("at2");
    expect(getAccessToken()).toBe("at2");
  });

  it("clearAccessToken drops the token", () => {
    setAccessToken("at1");
    clearAccessToken();
    expect(getAccessToken()).toBeNull();
  });

  it("never touches localStorage — the token is memory-only", () => {
    setAccessToken("at1");
    expect(localStorage.length).toBe(0);
  });

  it("purgeLegacyTokens removes a pre-#145 localStorage token", () => {
    localStorage.setItem("cluckwork.tokens", JSON.stringify({ accessToken: "old", refreshToken: "old" }));
    purgeLegacyTokens();
    expect(localStorage.getItem("cluckwork.tokens")).toBeNull();
  });

  it("purgeLegacyTokens is a no-op when there is nothing to purge", () => {
    expect(() => purgeLegacyTokens()).not.toThrow();
    expect(getAccessToken()).toBeNull();
  });
});
