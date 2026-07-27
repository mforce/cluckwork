import { describe, it, expect } from "vitest";
import i18n from "./index";

describe("i18n catalog + fallback", () => {
  it("resolves an existing English key", () => {
    expect(i18n.t("auth:signIn")).toBe("Sign in");
  });

  it("falls back to English for a language that lacks the key", async () => {
    // Simulate a future pack that is missing a key: it must render the English
    // string (fallbackLng), never blank or the raw key.
    i18n.addResourceBundle("xx", "auth", {}, true, true);
    await i18n.changeLanguage("xx");
    expect(i18n.t("auth:signIn")).toBe("Sign in");
    await i18n.changeLanguage("en");
  });

  it("never returns null for a key", () => {
    // returnNull:false — a resolved key is always a string.
    expect(typeof i18n.t("common:cancel")).toBe("string");
  });
});
