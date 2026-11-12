import { describe, it, expect } from "vitest";
import { freshPassword } from "./passwords";

describe("freshPassword", () => {
  it("meets the Identity password policy on every call", () => {
    for (let i = 0; i < 1000; i++) {
      const value = freshPassword();
      expect(value.length).toBeGreaterThanOrEqual(12);
      expect(value).toMatch(/[a-z]/);
      expect(value).toMatch(/[A-Z]/);
      expect(value).toMatch(/\d/);
      expect(value).toMatch(/[^a-zA-Z\d]/);
    }
  });
});
