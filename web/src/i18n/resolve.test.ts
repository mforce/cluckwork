import { describe, it, expect } from "vitest";
import { resolveLanguage } from "./resolve";

describe("resolveLanguage", () => {
  it("prefers the user's language when a pack exists", () => {
    expect(resolveLanguage("en", "en-US", ["en", "es"])).toBe("en");
    expect(resolveLanguage("es", "en-US", ["en", "es"])).toBe("es");
  });

  it("falls back to the farm-locale language subtag when the user's has no pack", () => {
    // user asked for fr (no pack) → farm locale es-MX → "es"
    expect(resolveLanguage("fr", "es-MX", ["en", "es"])).toBe("es");
  });

  it("falls back to English when neither has a pack", () => {
    expect(resolveLanguage("fr", "de-DE", ["en", "es"])).toBe("en");
  });

  it("treats null/undefined user language as unset", () => {
    expect(resolveLanguage(null, "es-MX", ["en", "es"])).toBe("es");
    expect(resolveLanguage(undefined, "en-GB", ["en", "es"])).toBe("en");
  });

  it("matches case-insensitively (BCP-47 subtags are not case-sensitive)", () => {
    expect(resolveLanguage("ES", "en-US", ["en", "es"])).toBe("es");
    expect(resolveLanguage(null, "ES-MX", ["en", "es"])).toBe("es");
  });

  it("is English-only by default (only 'en' installed)", () => {
    // The real SUPPORTED_LANGUAGES today: everything resolves to en.
    expect(resolveLanguage("es", "es-MX")).toBe("en");
  });
});
