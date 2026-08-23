import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  accountScopedKey,
  purgeUnscopedAccountState,
  readAccountScoped,
  writeAccountScoped,
} from "./accountStorage";
import { bindAccount, clearBoundAccount } from "../auth/tokenStore";

// `boundAccountId` is MODULE state read once at import, and setup.ts never calls
// clearBoundAccount() — so a bind leaks into every later test in the file. Reset
// it here so each test starts unbound.
beforeEach(() => {
  clearBoundAccount();
  localStorage.clear();
  sessionStorage.clear();
});

describe("accountStorage", () => {
  const BASE = "cluckwork.lastFlockId";
  const GUID = "11111111-1111-1111-1111-111111111111";
  const NS = `${BASE}:${GUID}`;

  it("bound — accountScopedKey namespaces with the account guid", () => {
    bindAccount(GUID);
    expect(accountScopedKey(BASE)).toBe(NS);
  });

  it("unbound — accountScopedKey returns null", () => {
    expect(accountScopedKey(BASE)).toBeNull();
  });

  it("unbound — readAccountScoped returns null", () => {
    expect(readAccountScoped(BASE)).toBeNull();
  });

  it("unbound — writeAccountScoped writes nothing", () => {
    writeAccountScoped(BASE, "flock-1");
    expect(localStorage.length).toBe(0);
  });

  it("bound — write then read round-trips", () => {
    bindAccount(GUID);
    writeAccountScoped(BASE, "flock-1");
    expect(readAccountScoped(BASE)).toBe("flock-1");
  });

  it("bound + getItem throws — returns null and the spy was called with the namespaced key", () => {
    bindAccount(GUID);
    const spy = vi
      .spyOn(Storage.prototype, "getItem")
      .mockImplementation(() => {
        throw new Error("storage unavailable");
      });
    expect(readAccountScoped(BASE)).toBeNull();
    expect(spy).toHaveBeenCalledWith(NS);
    spy.mockRestore();
  });

  it("bound + setItem throws — does not throw out and the spy was called", () => {
    bindAccount(GUID);
    const spy = vi
      .spyOn(Storage.prototype, "setItem")
      .mockImplementation(() => {
        throw new Error("quota exhausted");
      });
    expect(() => writeAccountScoped(BASE, "flock-1")).not.toThrow();
    expect(spy).toHaveBeenCalledWith(NS, "flock-1");
    spy.mockRestore();
  });

  it("removeItem throws — purgeUnscopedAccountState does not throw out", () => {
    const spy = vi
      .spyOn(Storage.prototype, "removeItem")
      .mockImplementation(() => {
        throw new Error("storage unavailable");
      });
    expect(() => purgeUnscopedAccountState()).not.toThrow();
    spy.mockRestore();
  });

  it("purge removes the bare key and leaves a namespaced key intact", () => {
    bindAccount(GUID);
    localStorage.setItem(BASE, "flock-1");
    localStorage.setItem(NS, "flock-2");
    purgeUnscopedAccountState();
    expect(localStorage.getItem(BASE)).toBeNull();
    expect(localStorage.getItem(NS)).toBe("flock-2");
  });
});
