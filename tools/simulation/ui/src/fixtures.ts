// tools/simulation/ui/src/fixtures.ts — the `test` every spec imports.
//
// Adds three things to Playwright's base test:
//   * `farm`    — the farm's real timezone/currency, for date fields and money.
//   * `signIn`  — sign a cast persona in THROUGH THE LOGIN FORM.
//   * `nav`     — locators for the shell, so specs name destinations, not CSS.
//
// ================== WHY signIn DRIVES THE FORM ==================
//
// It would be faster to POST /auth/login and inject the token. That is exactly
// the shortcut to refuse. The access token lives in a MODULE-LEVEL JS VARIABLE
// (web/src/auth/tokenStore.ts, #145) — never localStorage, never sessionStorage —
// and the refresh token is an HttpOnly cookie the page cannot set. There is no
// supported way to inject a session from outside, and a test that invented one
// would be asserting against a state the application can never actually be in.
//
// Signing in for real also means every spec exercises the login path, the
// bootstrap `/me` + `/account` read, and the language resolution, on the way to
// whatever it was actually about. Those are the most-used code paths in the app;
// having them under continuous load from the rest of the suite is free coverage.

import { test as base, expect, type Locator, type Page } from "@playwright/test";
import { type CastMember } from "./cast";
import { farmContext, type FarmContext } from "./farm";
import { tEn } from "./i18n";

export interface ShellNav {
  /** The desktop sidebar's nav landmark (`aria-label` = nav:primaryNavAriaLabel). */
  primary: Locator;
  /** A destination link by its nav i18n key, e.g. `nav:reports`. */
  link(labelKey: string): Locator;
  /** The sign-out control in the sidebar foot. */
  signOut: Locator;
}

export interface Fixtures {
  farm: FarmContext;
  signIn: (member: CastMember) => Promise<void>;
  nav: ShellNav;
}

export const test = base.extend<Fixtures>({
  farm: async ({}, use) => {
    await use(await farmContext());
  },

  signIn: async ({ page }, use) => {
    await use(async (member: CastMember) => {
      await page.goto("/login");

      // Both fields are `<label>Text<input/></label>`, so the label text IS the
      // accessible name — getByLabel is the user-visible handle, not a structural one.
      await page.getByLabel(tEn("auth:email")).fill(member.email);
      await page.getByLabel(tEn("auth:password")).fill(member.password);
      await page.getByRole("button", { name: tEn("auth:signIn") }).click();

      // THE ASSERTION THAT SIGN-IN WORKED is the app shell appearing — not the
      // URL changing, and not the absence of an error. `isLoading` gates the
      // router until the bootstrap refresh settles, so a URL check can pass
      // while the screen is still empty. The primary nav only ever renders
      // inside AppLayout, behind ProtectedRoute + SessionProvider, so its
      // presence means the whole authenticated path completed.
      await expect(page.getByRole("navigation", { name: tEn("nav:primaryNavAriaLabel") }))
        .toBeVisible();
    });
  },

  nav: async ({ page }, use) => {
    await use(shellNav(page));
  },
});

export function shellNav(page: Page): ShellNav {
  // Scoped to the SIDEBAR's nav landmark on purpose. BottomNav renders the same
  // destinations (CSS hides it above 901px), so an unscoped getByRole("link")
  // matches twice and every click is strict-mode-ambiguous.
  const primary = page.getByRole("navigation", { name: tEn("nav:primaryNavAriaLabel") });
  return {
    primary,
    link: (labelKey: string) =>
      primary.getByRole("link", { name: tEn(labelKey as `nav:${string}`), exact: true }),
    signOut: primary.getByRole("button", { name: tEn("nav:signOut") }),
  };
}

export { expect };
