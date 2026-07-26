import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { initialTheme, applyTheme } from "./theme";

beforeEach(() => {
  document.documentElement.removeAttribute("data-theme");
  localStorage.clear();
});
afterEach(() => vi.unstubAllGlobals());

describe("theme", () => {
  it("reads the data-theme attribute the pre-paint script always sets", () => {
    // The OS preference is not consulted here: theme-init.js has already
    // resolved it into the attribute before React ever runs (#149).
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    document.documentElement.dataset.theme = "light";
    expect(initialTheme()).toBe("light");
    document.documentElement.dataset.theme = "dark";
    expect(initialTheme()).toBe("dark");
  });

  it("defaults to light when the attribute is absent (non-browser host)", () => {
    // Cannot happen in the app — theme-init.js is render-blocking — but jsdom
    // and any non-browser host land here, and it must not throw.
    expect(initialTheme()).toBe("light");
  });

  it("ignores a garbage attribute value rather than trusting it", () => {
    document.documentElement.dataset.theme = "banana";
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

  it("applyTheme still applies the attribute when localStorage is unavailable", () => {
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyTheme("dark")).not.toThrow();
    expect(document.documentElement.dataset.theme).toBe("dark"); // in-memory choice still applies
    spy.mockRestore();
  });

  it("applyTheme drops a stale key when the write fails but reads still work", () => {
    // Quota exhaustion: getItem works, setItem throws. Without the removeItem
    // fallback the OLD value survives, so a user who switches dark->light in
    // session reloads back into "dark" — neither their choice nor the OS seed.
    applyTheme("dark");
    expect(localStorage.getItem("cluckwork.theme")).toBe("dark");

    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    applyTheme("light");
    spy.mockRestore();

    expect(document.documentElement.dataset.theme).toBe("light");
    expect(localStorage.getItem("cluckwork.theme")).toBeNull();
  });

  it("applyTheme survives removeItem also throwing", () => {
    const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    const remove = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    expect(() => applyTheme("dark")).not.toThrow();
    expect(document.documentElement.dataset.theme).toBe("dark");
    set.mockRestore();
    remove.mockRestore();
  });
});
