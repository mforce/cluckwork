// Own cleanup so the cell-style guard inspects the DOM before RTL unmounts it.
import "@testing-library/react/dont-cleanup-after-each";
import "@testing-library/jest-dom/vitest";
import i18n from "../i18n"; // initialise the i18next singleton so t()/useTranslation work
import { afterEach, beforeEach, expect, vi } from "vitest";
import { cleanup } from "@testing-library/react";
import { createFakeIndexedDb } from "./fakeIndexedDb";
import { clearAccessToken } from "../auth/tokenStore";
import { ledgerCellStyles } from "./ledgerCellStyles";

Element.prototype.scrollIntoView ??= vi.fn();

beforeEach(() => {
  // Default to "no session": any fetch a test doesn't explicitly mock — notably
  // the AuthProvider load-time silent refresh (#145) — resolves 401, so the
  // bootstrap settles as unauthenticated without a real network call. Tests that
  // need specific responses re-stub fetch in their own beforeEach (which runs
  // after this one).
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 401 })));
  vi.stubGlobal("indexedDB", createFakeIndexedDb());
});

afterEach(() => {
  const { violations } = ledgerCellStyles(document);
  cleanup();
  vi.unstubAllGlobals();
  clearAccessToken();
  // A test that switches the UI language (e.g. via changeLanguage or the
  // LanguageSelector) must not leak that language into the next test file's
  // shared i18next singleton — reset to the default so every test starts en.
  // Reset BEFORE clearing storage: the languageChanged handler writes the
  // "cluckwork.lang" device hint (see i18n/index.ts), so the reset must run
  // first and the clear then wipes that hint too → every test starts hint-free.
  void i18n.changeLanguage("en");
  localStorage.clear();
  sessionStorage.clear();
  expect(violations, "Field Console cell emphasis").toEqual([]);
});
