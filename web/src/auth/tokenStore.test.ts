import { describe, it, expect, vi } from "vitest";
import {
  bindAccount,
  bindFarm,
  clearAccessToken,
  clearBoundAccount,
  getAccessToken,
  getBoundFarmCode,
  purgeLegacyTokens,
  setAccessToken,
} from "./tokenStore";

// The in-memory access token is reset after every test in src/test/setup.ts.

describe("tokenStore (in-memory access token, #145)", () => {
  it("loads with an empty in-memory binding when sessionStorage reads are blocked", async () => {
    vi.resetModules();
    const getItem = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new DOMException("The operation is insecure.", "SecurityError");
    });

    try {
      const unavailableStorage = await import("./tokenStore");
      expect(unavailableStorage.getBoundAccountId()).toBeNull();
      expect(getItem).toHaveBeenCalledWith("cluckwork.boundAccountId");
    } finally {
      getItem.mockRestore();
    }
  });

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

  it("purgeLegacyTokens survives storage being unavailable", () => {
    // Safari private mode and hardened browser profiles throw from the storage
    // API rather than returning null. The purge runs at startup, so an
    // uncaught throw here would take the whole app down before first paint.
    const removeItem = vi
      .spyOn(Storage.prototype, "removeItem")
      .mockImplementation(() => {
        throw new DOMException("The operation is insecure.", "SecurityError");
      });

    try {
      expect(() => purgeLegacyTokens()).not.toThrow();
      expect(removeItem).toHaveBeenCalled();
    } finally {
      removeItem.mockRestore();
    }
  });

  it("purgeLegacyTokens is a no-op when there is nothing to purge", () => {
    expect(() => purgeLegacyTokens()).not.toThrow();
    expect(getAccessToken()).toBeNull();
  });

  it("keeps the non-secret farm binding across a reload and removes it on logout", async () => {
    clearBoundAccount();
    bindAccount("acct-A");

    vi.resetModules();
    const afterReload = await import("./tokenStore");
    expect(afterReload.getBoundAccountId()).toBe("acct-A");

    afterReload.clearBoundAccount();
    vi.resetModules();
    const afterLogout = await import("./tokenStore");
    expect(afterLogout.getBoundAccountId()).toBeNull();
    clearBoundAccount();
  });
});

// #586 — the farm binding is stored WITH the account it was proven against, in
// one record, so the two can never desync. Every case below is about that pair.
describe("tokenStore farm binding (#586)", () => {
  it("returns the slug while the account binding still matches", () => {
    bindAccount("acct-A");
    bindFarm("sunny-acres");
    expect(getBoundFarmCode()).toBe("sunny-acres");
  });

  it("returns null once the tab is bound to a DIFFERENT account", () => {
    // The leak the pairing exists to stop: a slug that outlived its account
    // would key the NEW farm's palette under the PREVIOUS farm's code.
    bindAccount("acct-A");
    bindFarm("sunny-acres");
    bindAccount("acct-B");
    expect(getBoundFarmCode()).toBeNull();
  });

  it("returns null when the tab has no account binding at all", () => {
    // A bare `===` would let null === null match and hand back a slug nothing
    // proved. The record is written AFTER the clear so it survives it.
    clearBoundAccount();
    sessionStorage.setItem(
      "cluckwork.boundFarm",
      JSON.stringify({ accountId: "acct-A", slug: "sunny-acres" }),
    );
    expect(getBoundFarmCode()).toBeNull();
  });

  it("bindFarm(null) removes the record", () => {
    bindAccount("acct-A");
    bindFarm("sunny-acres");
    bindFarm(null);
    expect(getBoundFarmCode()).toBeNull();
    expect(sessionStorage.getItem("cluckwork.boundFarm")).toBeNull();
  });

  it("stores nothing when there is no account to pin the slug to", () => {
    clearBoundAccount();
    bindFarm("sunny-acres");
    expect(sessionStorage.getItem("cluckwork.boundFarm")).toBeNull();
  });

  it("clearBoundAccount drops the farm binding with the account binding", () => {
    bindAccount("acct-A");
    bindFarm("sunny-acres");
    clearBoundAccount();
    expect(getBoundFarmCode()).toBeNull();
    // The RECORD, not just the getter. Without this line the test passes even
    // if clearBoundAccount never calls bindFarm at all, because the cleared
    // ACCOUNT already makes the getter return null — a false green two
    // reviewers caught independently.
    expect(sessionStorage.getItem("cluckwork.boundFarm")).toBeNull();
  });

  it("ignores every malformed record rather than throwing", () => {
    // sessionStorage is editable by anything on this origin, and destructuring
    // a parsed `null` throws — so each shape is screened, not assumed.
    bindAccount("acct-A");
    for (const raw of [
      "not json",
      "null",
      '"a string"',
      JSON.stringify(["sunny-acres"]),
      JSON.stringify({ accountId: 1, slug: "sunny-acres" }),
      JSON.stringify({ accountId: "acct-A" }),
      JSON.stringify({ slug: "sunny-acres" }),
    ]) {
      sessionStorage.setItem("cluckwork.boundFarm", raw);
      expect(getBoundFarmCode()).toBeNull();
    }
  });

  it("survives sessionStorage being unavailable on every path", () => {
    bindAccount("acct-A");
    const denied = () => {
      throw new DOMException("The operation is insecure.", "SecurityError");
    };

    const getItem = vi.spyOn(Storage.prototype, "getItem").mockImplementation(denied);
    try {
      expect(getBoundFarmCode()).toBeNull();
    } finally {
      getItem.mockRestore();
    }

    const setItem = vi.spyOn(Storage.prototype, "setItem").mockImplementation(denied);
    try {
      expect(() => bindFarm("sunny-acres")).not.toThrow();
    } finally {
      setItem.mockRestore();
    }

    const removeItem = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(denied);
    try {
      expect(() => bindFarm(null)).not.toThrow();
    } finally {
      removeItem.mockRestore();
    }
  });
});
