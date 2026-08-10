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

/**
 * Resolves when the server has answered the PUT that persists THIS language —
 * matched on the request payload, not just on the route (#486).
 *
 * Matching the route alone is not enough, because more than one of these is in
 * flight at a time. Every test normalises to English first, and the select is
 * `disabled` while a persist runs (`LanguageSelector.tsx`), so Playwright's
 * actionability check parks the NEXT `selectOption` until the reset's PUT
 * answers. A route-only listener registered in between is then satisfied by the
 * reset's response, and the assertion that the target language was persisted
 * passes without that request ever having been observed — which is how #486
 * reached a reload with nothing saved and an assertion that had already gone
 * green. `putMeLanguage` sends `{ language }` (`web/src/api/cluckwork.ts`), so
 * the payload is what tells the two apart.
 */
function languagePersisted(page: Page, lang: string, timeout = 10_000) {
  return page.waitForResponse((response) => {
    const request = response.request();
    if (!response.url().includes("/api/v1/me/language") || request.method() !== "PUT") return false;
    try {
      return (request.postDataJSON() as { language?: string } | null)?.language === lang;
    } catch {
      // A body that will not parse is not the request we are waiting for.
      return false;
    }
  }, { timeout });
}

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
      // Settled at creation so it can never float. If the selectOption below
      // throws — the select missing, the page not reaching /account — control
      // leaves through the catch without awaiting this, and its timeout then
      // surfaces as an unhandled rejection. Measured: that fails EVERY test in
      // the file with "page.waitForResponse: Test ended.", including ones that
      // never touch the language switch, which is precisely the cascade this
      // best-effort hook exists to prevent (#486 review).
      const reset = languagePersisted(page, "en").catch(() => undefined);
      await languageSelect(page).selectOption("en", { timeout: 10_000 });
      // Wait for the persist to land, not just the local switch — the point of
      // the reset is the SERVER-side preference, which is what the next run
      // bootstraps from.
      //
      // #486 — this used to assert `html lang="en"`, which proves nothing about
      // the server: i18next sets that attribute synchronously on
      // languageChanged, so it was already correct while the PUT was still in
      // flight. Leaving cleanup's PUT in flight is what let the NEXT test's
      // persist listener match THIS reset's response instead of its own.
      await reset;
    } catch {
      // Deliberately swallowed. A failed reset is worth knowing about but is not
      // itself the finding, and re-throwing here would mask the real failure.
    }
  });

  for (const lang of ["es", "tl"] as const satisfies readonly Language[]) {
    test(`switching to ${lang} renders that language across the shell`, async ({
      page,
      signIn,
      farm,
    }) => {
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

      // The switch, made the way a user makes it. Bound to THIS language's
      // payload: registered here, a route-only listener is satisfied by the
      // English reset above while `selectOption` is still parked on the
      // disabled select, and would report a persist that had not happened
      // (#486 — see languagePersisted).
      const persisted = languagePersisted(page, lang);
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

      // DURABILITY — and the device hint has to be cleared first, or this proves
      // nothing.
      //
      // `web/src/i18n/index.ts` writes a localStorage hint on every
      // `languageChanged` and seeds `i18next.init({ lng })` from it
      // SYNCHRONOUSLY at module load. So after a plain reload
      // `document.documentElement.lang` is already correct before any network
      // call, and asserting it would pass even if `PUT /me/language` had
      // persisted nothing at all (PR #390 review round 3 — the assertion was
      // added to prove server persistence and was reading device state).
      //
      // Clearing the hint removes that shortcut: the app then opens in its
      // default language and can only arrive back here via `/me`. Safe to clear
      // — the access token lives in memory (#145) and the refresh token in an
      // HttpOnly cookie, so neither is in localStorage to lose.
      await page.evaluate(() => window.localStorage.clear());
      await page.reload();

      // GUARD, not a comment, because this assertion's non-vacuity depends on a
      // fixture value that lives in another repo's seed data. `resolveLanguage`
      // falls back to the FARM LOCALE's subtag before English, so if the sim
      // account's locale ever became `es-*`, the `es` case would resolve to
      // Spanish with nothing persisted and go silently vacuous again — the exact
      // failure this change fixed, one layer down (PR #390 review round 4).
      expect(
        farm.locale.slice(0, 2),
        `the fixture's farm locale is "${farm.locale}", so an unpersisted "${lang}" preference `
          + `would fall back to ${lang} anyway and this assertion would prove nothing`,
      ).not.toBe(lang);

      // Now both of these are load-bearing. The nav link is the stronger of the
      // two: SessionContext gates the shell until it has applied the language
      // resolved from `/me`, so an unpersisted preference renders English here.
      await expect(
        page.getByRole("link", { name: t(lang, "nav:reports"), exact: true }),
        `the ${lang} preference did not survive a reload with the device hint cleared — `
          + `it was never persisted server-side`,
      ).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("lang", lang);

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
