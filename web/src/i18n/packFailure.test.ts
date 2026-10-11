import { afterEach, describe, expect, it, vi } from "vitest";
import i18n, { LAZY_PACKS, PACK_TIMEOUT_MS } from "./index";

// #1149 — a pack that fails at once (offline) or never answers (a stalled
// connection) must settle on English, never a blank app. Spied, not
// vi.mock'd: i18next reads ten namespaces in parallel, and Vitest hands some of
// those concurrent imports the real module.
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("a language pack that cannot load (#1149)", () => {
  it("settles on English when the request is rejected", async () => {
    vi.spyOn(LAZY_PACKS, "es").mockRejectedValue(new Error("Failed to fetch dynamically imported module"));
    await i18n.changeLanguage("es");
    expect(i18n.resolvedLanguage).toBe("en");
    expect(i18n.t("auth:signIn")).toBe("Sign in");
  });

  // The hinted boot and the /me-resolved bootstrap both reach i18next through
  // changeLanguage, and both land on this listener.
  it("labels the document with the language on screen and keeps the requested one for the next load", async () => {
    vi.spyOn(LAZY_PACKS, "es").mockRejectedValue(new Error("Failed to fetch dynamically imported module"));
    await i18n.changeLanguage("es");
    expect(document.documentElement.lang).toBe("en");
    expect(localStorage.getItem("cluckwork.lang")).toBe("es");
  });

  it("settles on English once the deadline passes on a request that never answers", async () => {
    vi.useFakeTimers();
    vi.spyOn(LAZY_PACKS, "tl").mockReturnValue(new Promise(() => {}));
    let settled = false;
    const switching = i18n.changeLanguage("tl").then(() => { settled = true; });
    await vi.advanceTimersByTimeAsync(PACK_TIMEOUT_MS - 1);
    expect(settled).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    await switching;
    expect(i18n.resolvedLanguage).toBe("en");
    expect(i18n.t("auth:signIn")).toBe("Sign in");
  });
});
