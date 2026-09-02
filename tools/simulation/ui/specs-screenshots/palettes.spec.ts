// tools/simulation/ui/specs-screenshots/palettes.spec.ts — #664.
//
// A separate, UNCOMMITTED capture: 4 palettes x 2 themes = 8 combinations,
// across the dashboard, one list screen (sales) and daily entry. #651/#652
// were only ever screenshot-checked in light + the default palette; this is
// the by-eye pass the rest of that work never got. Writes into a gitignored
// directory (out-palettes/), not docs/images/ — 8 states x 3 screens would
// bloat the repo for no ongoing benefit, and nothing here is meant to be
// published or diffed.
//
// Run deliberately, same as screenshots.spec.ts:
//
//     npm run screenshots:palettes    # from tools/simulation/ui, with the sim stack up
//
// ================== WHY THE ATTRIBUTES, NOT SETTINGS ==================
//
// The palette and theme are set by writing `data-brand`/`data-theme` directly
// on `document.documentElement`, not by driving the Settings screen. Two
// reasons: it exercises exactly the CSS attribute selectors the palettes are
// defined with (`:root[data-brand="forest"][data-theme="dark"]`), and it does
// not persist a farm setting through the app's own `applyBrand` — which
// writes to `localStorage` — so this spec's palette choice does not leak into
// every other spec's fixture, which would otherwise inherit whatever this one
// left set.
//
// BRANDS/DEFAULT_BRAND are duplicated from web/src/lib/brand.ts rather than
// imported: only the i18n catalogs cross the project boundary (see
// tsconfig.json's `include` and src/i18n.ts), so this list can drift if a
// palette is added or renamed there without a matching edit here.

import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test, type Page } from "../src/fixtures";
import { castMember } from "../src/cast";
import { tEn } from "../src/i18n";

// tools/simulation/ui/specs-screenshots/ -> tools/simulation/ui/out-palettes/
const OUT_DIR = fileURLToPath(new URL("../out-palettes/", import.meta.url));

function outPath(name: string): string {
  mkdirSync(OUT_DIR, { recursive: true });
  return `${OUT_DIR}${name}`;
}

const DEFAULT_BRAND = "aubergine";
const BRANDS = ["aubergine", "forest", "slate", "terracotta"] as const;
const THEMES = ["light", "dark"] as const;

// Each `page.goto` below is a full browser navigation (not a client-side
// route change), which re-runs the pre-paint script and resets both
// attributes to the farm's stored default — so this is called again after
// every navigation, not just once per test.
//
// Called AFTER the screen's own data-loaded assertion, never right after
// `goto`: FarmContext applies the farm's real brand synchronously once its
// account bootstrap resolves (web/src/farm/FarmContext.tsx), which lands
// shortly after navigation and would silently overwrite an override set
// before it — every non-default capture rendered as the farm's own
// aubergine until this was moved.
async function setPalette(page: Page, brand: string, theme: string): Promise<void> {
  await page.evaluate(
    ([brand, theme, isDefault]) => {
      if (isDefault) delete document.documentElement.dataset.brand;
      else document.documentElement.dataset.brand = brand;
      document.documentElement.dataset.theme = theme;
    },
    [brand, theme, brand === DEFAULT_BRAND] as const,
  );
}

// Same blur-then-screenshot shape as screenshots.spec.ts's capture(): a field
// this spec typed into keeps its focus ring in the published image otherwise.
async function capture(page: Page, name: string): Promise<void> {
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.screenshot({ path: outPath(name), animations: "disabled" });
}

test.describe("palette x theme visual matrix (#664)", () => {
  for (const brand of BRANDS) {
    for (const theme of THEMES) {
      test(`dashboard, sales, daily entry — ${brand}/${theme}`, async ({ page, signIn }) => {
        await signIn(castMember("Manager"));

        await page.goto("/");
        await expect(page.locator(".capture-tile").first()).toBeVisible();
        await setPalette(page, brand, theme);
        await capture(page, `dashboard-${brand}-${theme}.png`);

        await page.goto("/sales");
        await expect(page.getByRole("heading", { name: tEn("sales:ordersHeading") })).toBeVisible();
        const orders = page.getByRole("table").first();
        await expect(orders.getByRole("row")).not.toHaveCount(0);
        await setPalette(page, brand, theme);
        await capture(page, `sales-${brand}-${theme}.png`);

        await page.goto("/daily-entry");
        await expect(
          page.getByRole("spinbutton", { name: tEn("dailyEntry:totalEggsLabel") }),
        ).toBeVisible();
        await setPalette(page, brand, theme);
        await capture(page, `daily-entry-${brand}-${theme}.png`);
      });
    }
  }
});
