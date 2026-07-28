import { describe, it, expect, afterEach, beforeEach } from "vitest";
import i18n from "./index";

// Behaviour of the real (already-initialised) i18next singleton. The seeding
// LOGIC — hint → validated initial language — is unit-tested directly on the pure
// `pickInitialLanguage` in ../lib/languageHint.test.ts (a genuine reload of the
// singleton isn't possible here: i18next is an externalised dep outside Vitest's
// resettable module graph). Here we cover the two runtime side effects wired in
// i18n/index.ts: the `languageChanged` listener persists the hint and keeps
// <html lang> in sync.
const KEY = "cluckwork.lang";

beforeEach(() => {
  localStorage.removeItem(KEY);
});

afterEach(async () => {
  await i18n.changeLanguage("en");
  localStorage.removeItem(KEY);
});

describe("languageChanged persists the device hint", () => {
  it("writes the new language to the hint on every switch", async () => {
    await i18n.changeLanguage("tl");
    expect(localStorage.getItem(KEY)).toBe("tl");
    await i18n.changeLanguage("es");
    expect(localStorage.getItem(KEY)).toBe("es");
  });
});

describe("<html lang> tracks the UI language", () => {
  it("updates document.documentElement.lang on switch", async () => {
    await i18n.changeLanguage("es");
    expect(document.documentElement.lang).toBe("es");
    await i18n.changeLanguage("tl");
    expect(document.documentElement.lang).toBe("tl");
  });

  it("stays in sync with i18n.language (listener is registered before init, so it never lags)", async () => {
    await i18n.changeLanguage("es");
    expect(document.documentElement.lang).toBe(i18n.language);
  });
});
