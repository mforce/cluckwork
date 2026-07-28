import { describe, it, expect, afterEach, vi } from "vitest";
import { pickInitialLanguage, readLanguageHint, writeLanguageHint } from "./languageHint";

const KEY = "cluckwork.lang";

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("languageHint", () => {
  it("round-trips a language code through localStorage", () => {
    writeLanguageHint("es");
    expect(readLanguageHint()).toBe("es");
    expect(localStorage.getItem(KEY)).toBe("es");
  });

  it("returns null when nothing is stored", () => {
    localStorage.clear();
    expect(readLanguageHint()).toBeNull();
  });

  it("does NOT validate — validation is the read site's job", () => {
    // The helper is a dumb store; i18n init validates against installed packs.
    writeLanguageHint("fr");
    expect(readLanguageHint()).toBe("fr");
  });

  it("returns null (never throws) when the storage read fails", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new DOMException("SecurityError");
    });
    expect(() => readLanguageHint()).not.toThrow();
    expect(readLanguageHint()).toBeNull();
  });

  it("swallows a storage write failure (best-effort)", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("QuotaExceededError");
    });
    expect(() => writeLanguageHint("tl")).not.toThrow();
  });
});

describe("pickInitialLanguage", () => {
  const supported = ["en", "es", "tl"] as const;

  it("returns the hint when it is an installed pack", () => {
    expect(pickInitialLanguage("es", supported, "en")).toBe("es");
    expect(pickInitialLanguage("tl", supported, "en")).toBe("tl");
  });

  it("falls back when the hint is null", () => {
    expect(pickInitialLanguage(null, supported, "en")).toBe("en");
  });

  it("falls back on an unsupported / garbage hint", () => {
    expect(pickInitialLanguage("fr", supported, "en")).toBe("en");
    expect(pickInitialLanguage("", supported, "en")).toBe("en");
    expect(pickInitialLanguage("es-MX-nonsense", supported, "en")).toBe("en");
  });

  it("is case-insensitive (BCP-47 subtags are, and hints can be tampered)", () => {
    expect(pickInitialLanguage("ES", supported, "en")).toBe("es");
    expect(pickInitialLanguage("Tl", supported, "en")).toBe("tl");
  });
});
