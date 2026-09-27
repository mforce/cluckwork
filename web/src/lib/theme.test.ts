import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { initialTheme, applyTheme, watchDeviceTheme, resetExplicitThemeChoiceForTests } from "./theme";
import { stubMatchMedia } from "../test/matchMedia";

beforeEach(() => {
  document.documentElement.removeAttribute("data-theme");
  localStorage.clear();
  resetExplicitThemeChoiceForTests();
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

describe("watchDeviceTheme (#976)", () => {
  it("follows a live OS scheme change while nothing is explicitly saved", () => {
    const media = stubMatchMedia(false);
    const stop = watchDeviceTheme();
    media.triggerChange(true);
    expect(document.documentElement.dataset.theme).toBe("dark");
    media.triggerChange(false);
    expect(document.documentElement.dataset.theme).toBe("light");
    stop();
  });

  it("an explicit saved choice wins over a later OS change", () => {
    const media = stubMatchMedia(false);
    applyTheme("light");
    const stop = watchDeviceTheme();
    media.triggerChange(true); // OS flips to dark; the saved "light" must not move
    expect(document.documentElement.dataset.theme).toBe("light");
    stop();
  });

  it("stops listening after the returned unsubscribe runs", () => {
    const media = stubMatchMedia(false); // resolves "light" once on attach
    const stop = watchDeviceTheme();
    expect(document.documentElement.dataset.theme).toBe("light");
    stop();
    media.triggerChange(true);
    expect(document.documentElement.dataset.theme).toBe("light"); // unaffected post-unsubscribe
  });

  // #976 round 2, finding 1: a failed write must not make a later OS change
  // look like "nothing was ever chosen".
  it("keeps an explicit choice across a later OS change even when the write failed", () => {
    const setSpy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    applyTheme("light");
    setSpy.mockRestore();
    expect(localStorage.getItem("cluckwork.theme")).toBeNull(); // the write really failed

    const media = stubMatchMedia(false);
    const stop = watchDeviceTheme();
    media.triggerChange(true); // OS flips to dark; the in-memory "light" must not move
    expect(document.documentElement.dataset.theme).toBe("light");
    stop();
  });

  // #976 round 2, finding 3: theme-init.js resolves the OS preference once at
  // load; an OS change between then and this listener attaching (React still
  // loading) must not wait for a SECOND change to be caught.
  it("reconciles with the current media state immediately on attach", () => {
    stubMatchMedia(true); // OS already dark before subscribing
    const stop = watchDeviceTheme();
    expect(document.documentElement.dataset.theme).toBe("dark");
    stop();
  });

  it("does not reconcile on attach when an explicit choice already exists", () => {
    stubMatchMedia(true); // OS says dark
    applyTheme("light"); // chosen before subscribing
    const stop = watchDeviceTheme();
    expect(document.documentElement.dataset.theme).toBe("light");
    stop();
  });

  it("treats a storage read failure as no saved choice, still following the device", () => {
    const getSpy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("storage denied");
    });
    stubMatchMedia(true);
    const stop = watchDeviceTheme();
    expect(document.documentElement.dataset.theme).toBe("dark");
    getSpy.mockRestore();
    stop();
  });
});
