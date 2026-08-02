// i18n render — es and tl do not break the screens (#277 cross-cutting, #182).
//
// ================== WHAT MAKES THIS NON-VACUOUS ==================
//
// The suite builds its selectors from the SPA's own catalogs (src/i18n.ts), so
// there is an obvious way for this spec to be worthless: pick a key whose
// Spanish and English strings happen to be identical, assert it renders, and
// pass no matter what the language machinery does. That is not hypothetical —
// `nav:dashboard` really is "Dashboard" in BOTH en and tl.
//
// So the spec asserts, first, that the strings it is about to look for ACTUALLY
// DIFFER from English, and fails loudly if a catalog change ever makes them the
// same. Verify the instrument can observe the thing before trusting what it says.
//
// ================== AND WHY IT USES ITS OWN PERSONA ==================
//
// `LanguageSelector` persists through `PUT /me/language`. Switching language is
// a durable change to that USER, not a page-local toggle — see `i18nPersona()`.

import { expect, test, type Page } from "../src/fixtures";
import { i18nPersona } from "../src/cast";
import { t, tEn, type Language } from "../src/i18n";

/**
 * The language `<select>`, located WITHOUT using its label.
 *
 * Its label is `account:language`, which is itself translated ("Language" ->
 * "Idioma" -> "Wika") — so the moment the switch under test works, a
 * `getByLabel(tEn(...))` locator stops matching. That is not a hypothetical: it
 * turned the cleanup hook into a 45-second hang that reported as a failure of
 * the NEXT test. Matching on the option values, which are language-invariant
 * codes, keeps the handle stable across exactly the change being tested.
 */
function languageSelect(page: Page) {
  return page.locator('select:has(option[value="en"]):has(option[value="es"])');
}

/**
 * Keys chosen because all three catalogs give them genuinely different strings
 * (en "Language" / es "Idioma" / tl "Wika"; en "Reports" / es "Informes" /
 * tl "Mga Report"). The guard below re-checks that at run time rather than
 * trusting this comment.
 */
const WITNESS_KEYS = ["account:language", "nav:reports"] as const;

const LANGUAGE_OPTION = { en: "English", es: "Español", tl: "Tagalog" } as const;

test.describe("i18n", () => {
  // Always hand the persona back in English, even when an assertion above fails
  // — otherwise one red run leaves the account in Spanish and the NEXT run of
  // this spec starts from a state it did not choose.
  test.afterEach(async ({ page }) => {
    // Best-effort and time-boxed: this is cleanup, and a cleanup hook that can
    // itself hang converts one failure into a cascade across the whole file.
    try {
      await page.goto("/account", { timeout: 10_000 });
      await languageSelect(page).selectOption("en", { timeout: 10_000 });
      // Wait for the persist to land, not just the local switch — the point of
      // the reset is the SERVER-side preference, which is what the next run
      // bootstraps from.
      await expect(page.locator("html")).toHaveAttribute("lang", "en", { timeout: 10_000 });
    } catch {
      // Deliberately swallowed. A failed reset is worth knowing about but is not
      // itself the finding, and re-throwing here would mask the real failure.
    }
  });

  for (const lang of ["es", "tl"] as const satisfies readonly Language[]) {
    test(`switching to ${lang} renders that language across the shell`, async ({ page, signIn }) => {
      // The guard. A key whose translation equals English cannot distinguish
      // "the language switched" from "nothing happened".
      for (const key of WITNESS_KEYS) {
        expect(
          t(lang, key),
          `"${key}" is identical in en and ${lang}, so this spec cannot tell a working language `
            + `switch from a broken one. Pick a different witness key.`,
        ).not.toBe(tEn(key));
      }

      await signIn(i18nPersona());

      // Start from a known language rather than whatever this account was last
      // left in — the persona is reserved, but a previous failed run is still a
      // possible starting state.
      await page.goto("/account");
      const selector = languageSelect(page);
      await selector.selectOption("en");
      await expect(page.getByRole("link", { name: tEn("nav:reports"), exact: true })).toBeVisible();

      // The switch, made the way a user makes it.
      const persisted = page.waitForResponse((response) =>
        response.url().includes("/api/v1/me/language")
          && response.request().method() === "PUT",
      );
      await selector.selectOption(lang);
      expect((await persisted).ok(), `the ${lang} preference was not persisted`).toBe(true);

      // THE ASSERTION: the destination the user reads in the sidebar is now in
      // the new language. The sidebar is the right place to look because it is
      // rendered by a different component tree than the screen that owns the
      // selector — a switch that only repainted the current screen would pass a
      // narrower check and still be broken everywhere else.
      await expect(
        page.getByRole("link", { name: t(lang, "nav:reports"), exact: true }),
        `the sidebar did not switch to ${lang}`,
      ).toBeVisible();
      await expect(page.getByRole("link", { name: tEn("nav:reports"), exact: true })).toBeHidden();

      // <html lang> follows. This is a real accessibility guarantee, not
      // decoration: it is what tells a screen reader which voice to use, and
      // index.html ships a static "en" that a broken listener would leave in
      // place while the visible text changed underneath it.
      await expect(page.locator("html")).toHaveAttribute("lang", lang);

      // A local i18next change is optimistic. Reload so the preference must be
      // recovered from the server rather than surviving only in component
      // memory.
      await page.reload();
      await expect(page.locator("html")).toHaveAttribute("lang", lang);
      await expect(
        page.getByRole("link", { name: t(lang, "nav:reports"), exact: true }),
      ).toBeVisible();

      // And the screens still WORK in that language — the point of #277's "not
      // broken", as opposed to "translated". A populated data screen renders its
      // table, not an error.
      await page.getByRole("link", { name: t(lang, "nav:reports"), exact: true }).click();
      await expect(page.getByRole("heading", { name: t(lang, "reports:title") })).toBeVisible();
      await expect(page.getByRole("alert")).toBeHidden();
      await expect(
        page
          .getByRole("table")
          .filter({ has: page.getByRole("columnheader", { name: t(lang, "reports:dateHeader") }) }),
      ).toBeVisible();
    });
  }

  test("the language selector offers every installed pack in its own script", async ({
    page,
    signIn,
  }) => {
    await signIn(i18nPersona());
    await page.goto("/account");

    // Each pack is named in ITS OWN language ("Español", not "Spanish") — the
    // one string in the app deliberately left untranslated, because somebody who
    // cannot read the current language must still be able to find their own.
    for (const [code, name] of Object.entries(LANGUAGE_OPTION)) {
      await expect(languageSelect(page).locator(`option[value="${code}"]`)).toHaveText(name);
    }
  });
});
