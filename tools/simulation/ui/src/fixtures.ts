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
import { activeMutant } from "./mutants";

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
  /** Auto-fixture: installs the mutation harness when CLUCKWORK_E2E_MUTANT is set. Inert otherwise. */
  mutation: void;
}

export const test = base.extend<Fixtures>({
  // Auto so no spec has to opt in — a mutant that only applied to specs which
  // remembered to ask for it would be a mutation check with holes in exactly the
  // places nobody thought about.
  //
  // Installed BEFORE the spec body runs, so a spec's own `page.route` calls are
  // registered later and therefore match first (Playwright tries handlers in
  // reverse registration order). Their `route.fallback()` then reaches the
  // mutant, which is the layering the specs already assume.
  mutation: [
    async ({ page }, use) => {
      const active = activeMutant();
      if (active) {
        await active.mutant.apply(page);
        test.info().annotations.push({
          type: "MUTANT",
          description: `${active.name} — breaks ${active.mutant.breaks}. A PASS here is a SURVIVING MUTANT.`,
        });
      }
      await use();
    },
    { auto: true },
  ],

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
      // while the screen is still empty. The sidebar only ever renders inside
      // AppLayout, behind ProtectedRoute + SessionProvider, so its presence
      // means the whole authenticated path completed.
      //
      // Matched on the `complementary` LANDMARK, with no accessible name.
      // Naming it (`navigation` + `nav:primaryNavAriaLabel`) would tie signing in
      // to the ENGLISH label — and a user's language is a persisted server-side
      // preference, so any persona left in es/tl by the i18n spec could no
      // longer sign in at all. That is not hypothetical; it happened. `main` is
      // not usable either: the login screen is a `<main>` too, so it cannot tell
      // the shell from the form it replaced.
      await expect(page.getByRole("complementary")).toBeVisible();
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

  // Sign out is NOT inside that landmark — AppLayout puts it in the sidebar
  // FOOT, a sibling of <nav> alongside the theme toggle. Scoping it to `primary`
  // finds nothing and fails 45 seconds later as "waiting for … Sign out", which
  // reads like the button having been removed. So it is scoped to the
  // <aside> (`complementary`) that contains both. That is still narrow enough to
  // stay unambiguous: BottomNav's own Sign out lives in the More sheet, which is
  // a dialog portalled to <body> and only exists while open.
  const sidebar = page.getByRole("complementary");
  return {
    primary,
    link: (labelKey: string) =>
      primary.getByRole("link", { name: tEn(labelKey as `nav:${string}`), exact: true }),
    signOut: sidebar.getByRole("button", { name: tEn("nav:signOut") }),
  };
}

export { expect };
export type { Locator, Page };
