// tools/simulation/ui/src/fixtures.ts — the `test` every spec imports.
//
// Adds five things to Playwright's base test:
//   * `farm`    — the farm's real timezone/currency, for date fields and money.
//   * `signIn`  — sign a cast persona in THROUGH THE LOGIN FORM.
//   * `nav`     — locators for the DESKTOP shell, so specs name destinations, not CSS.
//   * `phone`   — locators for the PHONE shell (BottomNav's tab bar + More sheet).
//   * `shellLayout` — which of those two the project under test is running.
//
// ================== WHY THE SHELL IS TWO FIXTURES, NOT ONE ==================
//
// Below 900px the sidebar is `display: none` and BottomNav owns navigation
// (#814). The two shells are not interchangeable renderings of one nav: the
// sidebar shows every permitted destination at once, while the tab bar shows
// four and hides the rest inside a CLOSED dialog. So a `toBeHidden()` that
// means "this role may not go there" on the sidebar means "the sheet happens to
// be shut" on a phone, and passes for a reason the spec never claimed.
//
// `shellLayout` is a per-project option rather than something either fixture
// sniffs from the viewport, so the answer comes from the run's configuration
// and a spec cannot end up disagreeing with it.
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

/** Which shell the project under test renders. Set per project, never sniffed. */
export type ShellLayout = "desktop" | "phone";

/**
 * BottomNav's More sheet, WHILE OPEN.
 *
 * This type is only ever produced by `openMore()`, which is what stops a spec
 * reaching for a sheet link without opening the sheet: a locator that reports
 * `toBeHidden()` means "this role may not go there" and "nobody opened the
 * menu" indistinguishably, and only the first is ever the claim.
 *
 * **What that does NOT do, stated because an earlier version of this comment
 * claimed otherwise.** The returned locators are ordinary and reusable, and
 * `openMore()` checks visibility once, when it returns. Nothing re-checks that
 * the sheet is still open at the moment a locator is used — so an absence
 * assertion written AFTER something closed the sheet (clicking a destination
 * closes it; BottomNav does that itself) would be vacuous again. The compiler
 * enforces the handle, not the state. Pair any absence claim with a visible
 * control in the same sheet, the way the role-gate specs do on the sidebar.
 */
export interface MoreSheet {
  /** The sheet itself (`role="dialog"`, named nav:menuTitle). */
  dialog: Locator;
  /** A destination link inside the open sheet, by its nav i18n key. */
  link(labelKey: string): Locator;
  /** The sign-out control in the sheet foot — the phone shell's only one. */
  signOut: Locator;
  // No `close()`. The one spec here dismisses the sheet by clicking a
  // destination, which is what BottomNav's own onClick does, so an explicit
  // close helper would be a locator (`common:close` on the Dialog's × button)
  // that no run ever exercises — an untested handle sitting in a fixture other
  // specs will copy from. Add it with its first real caller, and not before.
}

export interface PhoneShell {
  /** The fixed bottom bar's nav landmark (`aria-label` = nav:tabBarAriaLabel). */
  tabbar: Locator;
  /** One of the four thumb tabs, by its nav i18n key. */
  tab(labelKey: string): Locator;
  /** The fifth slot, which opens the sheet holding every other destination. */
  more: Locator;
  openMore(): Promise<MoreSheet>;
}

export interface Fixtures {
  farm: FarmContext;
  signIn: (member: CastMember) => Promise<void>;
  /** Option fixture, set by the project. Decides which shell fixture is usable. */
  shellLayout: ShellLayout;
  nav: ShellNav;
  phone: PhoneShell;
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
      //
      // The farm code comes from the MEMBER, because this stack carries two farms:
      // the simulation fixture on `default-farm` and the README-capture farm the
      // dashboard screenshot is taken from. Only that second farm's Owner carries
      // a `farmCode`, so every persona written before it keeps signing into the
      // default farm without being touched.
      await page.getByLabel(tEn("auth:farmCode")).fill(member.farmCode ?? "default-farm");
      await page.getByLabel(tEn("auth:email")).fill(member.email);
      await page.getByLabel(tEn("auth:password")).fill(member.password);
      await page.getByRole("button", { name: tEn("auth:signIn") }).click();

      // THE ASSERTION THAT SIGN-IN WORKED is the app shell appearing — not the
      // URL changing, and not the absence of an error. `isLoading` gates the
      // router until the bootstrap refresh settles, so a URL check can pass
      // while the screen is still empty. `main#main-content` only ever renders
      // inside AppLayout, behind ProtectedRoute + SessionProvider, so its
      // presence means the whole authenticated path completed.
      //
      // Matched on a STRUCTURAL handle, never on a label. Naming a landmark
      // (`navigation` + `nav:primaryNavAriaLabel`) would tie signing in to the
      // ENGLISH label — and a user's language is a persisted server-side
      // preference, so any persona left in es/tl by the i18n spec could no
      // longer sign in at all. That is not hypothetical; it happened.
      //
      // The bare `main` element is not usable either: the login screen is a
      // `<main class="auth">` too, so it cannot tell the shell from the form it
      // replaced. The `id` is what separates them — AppLayout's is the only
      // element in the app carrying `main-content`, and the login screen's has
      // no id at all.
      //
      // This replaced `getByRole("complementary")`, which cannot be used here
      // any more: the sidebar is `display: none` below 900px (#814), so under
      // the phone project sign-in would never complete. Say plainly what was
      // given up — this assertion is NARROWER than the one it replaces. It
      // proves the authenticated shell mounted; it does NOT prove any nav
      // chrome rendered, in either layout. A spec that cares about the nav must
      // assert on it itself, through `nav` or `phone`.
      await expect(page.locator("main#main-content")).toBeVisible();
    });
  },

  // Declared here, supplied by each project's `use` block in
  // playwright.config.ts. The default is "desktop" so every spec written before
  // #814 keeps the shell it was written against without being touched.
  shellLayout: ["desktop", { option: true }],

  nav: async ({ page, shellLayout }, use) => {
    // REFUSED under the phone layout, on purpose, and this is the load-bearing
    // half of #814 rather than a convenience.
    //
    // Three specs assert `toBeHidden()` on a sidebar link to prove a role gate:
    // specs/worker.spec.ts ("is not offered the admin setup destinations"),
    // specs/session-races.spec.ts (the late-refresh race's admin-destination
    // check) and specs/readonly.spec.ts ("is not offered the destinations it
    // cannot use"). At phone width those destinations live inside a CLOSED
    // dialog, so every one of those NEGATIVE assertions would pass with the
    // gate wide open.
    //
    // Precisely, because an earlier version of this comment overstated it:
    // those three tests would not go green — each pairs its hidden-links loop
    // with a positive control (`nav.link("nav:stock")` and friends must be
    // VISIBLE), and the control is what would fail against an absent sidebar.
    // So the failure mode is not a silently passing suite; it is a suite that
    // fails for the wrong reason while the assertions carrying the actual
    // guarantee have quietly stopped being able to fail. That is still the
    // thing worth preventing, and it is why the refusal is here rather than in
    // a comment asking people to be careful.
    //
    // Making the fixture unavailable turns that into a construction error
    // instead of something a reviewer has to spot: a spec that wants both
    // widths has to say which shell it means.
    if (shellLayout === "phone") {
      throw new Error(
        "The `nav` fixture is the DESKTOP sidebar and does not exist below 900px. "
          + "Use the `phone` fixture, and reach a non-tab destination through "
          + "`(await phone.openMore()).link(key)` — a sheet link is hidden while the "
          + "sheet is shut, so a toBeHidden() on the sidebar's locator would pass here "
          + "for a reason the spec never claimed.",
      );
    }
    await use(shellNav(page));
  },

  phone: async ({ page, shellLayout }, use) => {
    // The mirror of the refusal above. Above 900px `.tabbar` is `display: none`,
    // so every locator here would resolve to a hidden element and any
    // `toBeHidden()` written against it would be vacuous in the same way.
    if (shellLayout === "desktop") {
      throw new Error(
        "The `phone` fixture is BottomNav's tab bar, which is `display: none` above 900px. "
          + "Tag the test @phone so it runs under the chromium-phone project, or use the "
          + "`nav` fixture for the desktop sidebar.",
      );
    }
    await use(phoneShell(page));
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

export function phoneShell(page: Page): PhoneShell {
  // Scoped to the TAB BAR's nav landmark for the same reason `shellNav` scopes
  // to the sidebar's: both shells are in the DOM at once and CSS decides which
  // one is on screen, so an unscoped getByRole("link") matches twice.
  const tabbar = page.getByRole("navigation", { name: tEn("nav:tabBarAriaLabel") });

  // More is a <button> in the bar's fifth slot, not a link — it opens a dialog
  // rather than navigating, and its accessible name is the `nav:moreButton`
  // span beside an aria-hidden icon.
  const more = tabbar.getByRole("button", { name: tEn("nav:moreButton"), exact: true });

  return {
    tabbar,
    tab: (labelKey: string) =>
      tabbar.getByRole("link", { name: tEn(labelKey as `nav:${string}`), exact: true }),
    more,
    openMore: async () => {
      await more.click();

      // The sheet is a Dialog portalled to <body>, so it is scoped by its own
      // role and title rather than by the bar it was opened from.
      const dialog = page.getByRole("dialog", { name: tEn("nav:menuTitle") });
      await expect(dialog).toBeVisible();

      return {
        dialog,
        link: (labelKey: string) =>
          dialog.getByRole("link", { name: tEn(labelKey as `nav:${string}`), exact: true }),
        signOut: dialog.getByRole("button", { name: tEn("nav:signOut"), exact: true }),
      };
    },
  };
}

export { expect };
export type { Locator, Page };
