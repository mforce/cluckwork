import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { initialTheme, applyTheme } from "./theme";

beforeEach(() => {
  document.documentElement.removeAttribute("data-theme");
  localStorage.clear();
});
afterEach(() => vi.unstubAllGlobals());

describe("theme", () => {
  it("reads an explicit data-theme when present, ignoring the OS preference", () => {
    // even if the OS says dark, an explicit choice wins
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    document.documentElement.dataset.theme = "light";
    expect(initialTheme()).toBe("light");
    document.documentElement.dataset.theme = "dark";
    expect(initialTheme()).toBe("dark");
  });

  it("falls back to the OS preference when no theme is set", () => {
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    expect(initialTheme()).toBe("dark");
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: false }));
    expect(initialTheme()).toBe("light");
  });

  it("defaults to light when matchMedia is unavailable (e.g. jsdom / non-browser host)", () => {
    // jsdom does not implement matchMedia — initialTheme must not throw
    expect(window.matchMedia).toBeUndefined();
    expect(initialTheme()).toBe("light");
  });

  it("applyTheme sets the root data-theme attribute and persists the choice", () => {
    applyTheme("dark");
    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(localStorage.getItem("cluckwork.theme")).toBe("dark");

    applyTheme("light");
    expect(document.documentElement.dataset.theme).toBe("light");
    expect(localStorage.getItem("cluckwork.theme")).toBe("light");
  });

  it("applyTheme still applies the attribute when localStorage is unavailable (private mode)", () => {
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyTheme("dark")).not.toThrow();
    expect(document.documentElement.dataset.theme).toBe("dark"); // in-memory choice still applies
    spy.mockRestore();
  });
});
