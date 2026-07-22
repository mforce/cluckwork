import { describe, it, expect, afterEach, vi } from "vitest";
import { newId } from "./ids";

// The whole point of this module is the branch jsdom does NOT take by default,
// so the tests force both.
// randomUUID is not an OWN property of `crypto` — it lives on the prototype —
// and under Vitest the global `crypto` is Node's webcrypto while `Crypto` is
// jsdom's class, so they are different realms (`crypto instanceof Crypto` is
// false) and touching Crypto.prototype does nothing at all. Shadowing an own
// property on the object actually in use is realm-proof; deleting the shadow
// afterwards lets the real one show through again.
const withoutRandomUUID = (run: () => void) => {
  Object.defineProperty(crypto, "randomUUID", { value: undefined, configurable: true });
  try {
    run();
  } finally {
    // @ts-expect-error removing the shadow, not the prototype's real member.
    delete crypto.randomUUID;
  }
};

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

afterEach(() => vi.restoreAllMocks());

describe("newId", () => {
  it("uses the platform UUID where it exists", () => {
    const spy = vi.spyOn(crypto, "randomUUID");
    expect(newId()).toMatch(UUID_V4);
    expect(spy).toHaveBeenCalled();
  });

  it("still returns a real v4 UUID where randomUUID is missing", () => {
    withoutRandomUUID(() => {
      // A phone on http://192.168.x.x:5173 lands here. Version and variant bits
      // must still be right, or the server's key parsing rejects the header.
      for (let i = 0; i < 50; i++) expect(newId()).toMatch(UUID_V4);
    });
  });

  it("draws the fallback from getRandomValues, not Math.random", () => {
    withoutRandomUUID(() => {
      const spy = vi.spyOn(crypto, "getRandomValues");
      const random = vi.spyOn(Math, "random");
      newId();
      expect(spy).toHaveBeenCalled();
      expect(random).not.toHaveBeenCalled();
    });
  });

  it("does not repeat itself", () => {
    withoutRandomUUID(() => {
      const seen = new Set(Array.from({ length: 500 }, newId));
      expect(seen.size).toBe(500);
    });
  });
});
