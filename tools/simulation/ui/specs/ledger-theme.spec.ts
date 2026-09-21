import { test, expect, type Page } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

async function probeVar(page: Page, name: string): Promise<string> {
  return page.evaluate((n) => {
    const probe = document.createElement("span");
    probe.style.color = `var(${n})`;
    document.body.append(probe);
    const color = getComputedStyle(probe).color;
    probe.remove();
    return color;
  }, name);
}

for (const theme of ["light", "dark"] as const) {
  test(`Field Console row links and headings follow the shared ${theme} theme`, async ({ page, signIn }) => {
    await page.emulateMedia({ colorScheme: theme });
    await signIn(owner());
    await page.goto("/history");
    // #930 — a link is --ink in every theme (#834); FieldConsole no longer
    // scopes --link to --brand, so this must hold in light AND dark, not
    // just the aubergine default.
    const ink = await probeVar(page, "--ink");
    for (const label of [tEn("common:recordHistory.viewHistoryLink"), tEn("history:editButton")]) {
      await expect(page.getByRole("link", { name: label, exact: true }).first()).toHaveCSS("color", ink);
    }
    await expect(page.getByRole("button", { name: tEn("history:adjustButton"), exact: true }).first()).toHaveCSS("color", ink);
    await page.goto("/reports");
    const section = page.getByRole("heading", { name: tEn("reports:productionHeading"), exact: true });
    await expect(section).toHaveCSS("font-size", "15px");
    expect(await section.evaluate(el => getComputedStyle(el).fontFamily)).not.toContain("Georgia");
    await page.goto("/water");
    await expect(page.getByRole("button", { name: tEn("water:correctButton"), exact: true }).first()).toHaveCSS("color", ink);
  });

  test(`payment dialog uses the shared h2 typography in ${theme}`, async ({ page, signIn }) => {
    await page.emulateMedia({ colorScheme: theme });
    await signIn(owner());
    await page.goto("/sales");
    const confirmed = page.getByRole("row").filter({ hasText: "Sim Customer 3" })
      .filter({ hasText: tEn("enums:status.Confirmed") }).first();
    await confirmed.getByRole("button", { name: tEn("sales:open"), exact: true }).click();
    await page.getByRole("button", { name: tEn("sales:recordPayment"), exact: true }).click();
    const title = page.getByRole("dialog").getByRole("heading", { name: tEn("sales:recordPayment"), exact: true });
    expect(await title.evaluate(el => el.tagName)).toBe("H2");
    await expect(title).toHaveCSS("font-family", "Georgia, serif");
  });
}

// #930 — FieldConsole scoped `--link` to `--brand` (introduced by #899), a
// fixed hex that does not vary between light and dark, shadowing the dark
// theme's `--ink` and leaving row-action links at ~1:1 idle contrast.
// styles.test.ts's link guard cannot see this: it walks only :root token
// DEFAULTS (deliberately, per web/src/test/cssTokens.ts — jsdom cannot
// resolve a real custom-property cascade either), never a component's own
// runtime override. This section is the guard that catches it: it reads the
// ACTUAL computed colour of a real row-action element on every one of the
// eight FieldConsole-consuming screens (the seven named in the issue's blast
// radius plus Sales, which joined via #927's own LINK_ACTION_SX copy), in
// every dark palette, for both the idle and the MUI-disabled state.
const BRANDS = ["aubergine", "forest", "slate", "terracotta"] as const;

function channelOf(c: number): number {
  const s = c / 255;
  return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
}

function parseRgba(value: string): [number, number, number, number] {
  const m = /rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)(?:\s*,\s*([\d.]+))?\s*\)/.exec(value);
  if (!m) throw new Error(`not an rgb() colour: ${value}`);
  return [Number(m[1]), Number(m[2]), Number(m[3]), m[4] === undefined ? 1 : Number(m[4])];
}

function luminanceOf([r, g, b]: [number, number, number]): number {
  return 0.2126 * channelOf(r) + 0.7152 * channelOf(g) + 0.0722 * channelOf(b);
}

// getComputedStyle(el).color returns the DECLARED colour, alpha included —
// never what the browser actually painted. MUI's dark `action.disabled` is
// rgba(255,255,255,0.3), a translucent white; reading its RGB channels alone
// (ignoring alpha) measures it as opaque white against a dark surface — the
// best possible contrast, rather than the ~2.7:1 it truly paints as. The
// compositing below is load-bearing, not a simplification target.
function contrastOf(fgCss: string, bgCss: string): number {
  const [fr, fgc, fb, fa] = parseRgba(fgCss);
  const [br, bg, bb] = parseRgba(bgCss);
  const painted: [number, number, number] = [fr * fa + br * (1 - fa), fgc * fa + bg * (1 - fa), fb * fa + bb * (1 - fa)];
  const [lPainted, lBg] = [luminanceOf(painted), luminanceOf([br, bg, bb])];
  return (Math.max(lPainted, lBg) + 0.05) / (Math.min(lPainted, lBg) + 0.05);
}

async function setBrand(page: Page, brand: string): Promise<void> {
  await page.evaluate((b) => {
    if (b === "aubergine") delete document.documentElement.dataset.brand;
    else document.documentElement.dataset.brand = b;
  }, brand);
}

// Toggles the real MUI class/attribute a disabled Button carries, reads the
// resulting computed colour, then reverts — so the probed element stays
// interactive for anything a caller does with it afterwards.
async function disabledColorOf(locator: ReturnType<Page["getByRole"]>): Promise<string> {
  return locator.evaluate((el) => {
    el.classList.add("Mui-disabled");
    el.setAttribute("disabled", "");
    const colour = getComputedStyle(el).color;
    el.classList.remove("Mui-disabled");
    el.removeAttribute("disabled");
    return colour;
  });
}

function assertContrast(brand: string, label: string, idle: string, disabled: string, surface: string, surface2: string): void {
  expect(contrastOf(idle, surface), `${brand}: ${label} idle vs --surface`).toBeGreaterThanOrEqual(4.5);
  expect(contrastOf(idle, surface2), `${brand}: ${label} idle vs --surface-2`).toBeGreaterThanOrEqual(4.5);
  expect(contrastOf(disabled, surface), `${brand}: ${label} disabled vs --surface`).toBeGreaterThanOrEqual(3);
  expect(contrastOf(disabled, surface2), `${brand}: ${label} disabled vs --surface-2`).toBeGreaterThanOrEqual(3);
}

// The five routes with a reachable real element: no admin gate, no
// interaction beyond an optional one-time reveal click. `ready` is what the
// screen shows BEFORE any reveal — the signal that FarmContext's own account
// bootstrap has resolved, so `setBrand` (which follows) isn't overwritten by
// it. `reveal` then runs, and `locator` is the FINAL target it uncovers.
type RouteCheck = {
  label: string; path: string;
  ready: (page: Page) => ReturnType<Page["getByRole"]>;
  reveal?: (page: Page) => Promise<void>;
  locator: (page: Page) => ReturnType<Page["getByRole"]>;
};
const historyAdjust = (page: Page) => page.getByRole("button", { name: tEn("history:adjustButton"), exact: true }).first();
// Two "Manage categories" affordances share this label: a page-header
// `variant="contained"` toggle (DOM order first) and the add-expense panel's
// own `CONSOLE_LINK_SX` text button (DOM order last) — the one this issue is
// actually about.
const expensesManageCategories = (page: Page) => page.getByRole("button", { name: tEn("expenses:manageCategoriesButton"), exact: true }).last();
const waterCorrect = (page: Page) => page.getByRole("button", { name: tEn("water:correctButton"), exact: true }).first();
const inventoryOpen = (page: Page) => page.getByRole("button", { name: tEn("inventory:openButton"), exact: true }).first();
const inventoryChooseAnother = (page: Page) => page.getByRole("button", { name: tEn("inventory:chooseAnotherItem"), exact: true });
const stockLots = (page: Page) => page.getByRole("button", { name: tEn("stock:lotsButton"), exact: true }).first();
const stockAdjustmentHistory = (page: Page) => page.getByRole("link", { name: tEn("common:recordHistory.viewAdjustmentHistoryLink"), exact: true }).first();
const ROUTE_CHECKS: RouteCheck[] = [
  { label: "History adjust", path: "/history", ready: historyAdjust, locator: historyAdjust },
  { label: "Expenses manage categories", path: "/expenses", ready: expensesManageCategories, locator: expensesManageCategories },
  { label: "Water correct", path: "/water", ready: waterCorrect, locator: waterCorrect },
  { label: "Inventory choose another item", path: "/inventory", ready: inventoryOpen,
    reveal: (page) => inventoryOpen(page).click(), locator: inventoryChooseAnother },
  // The lots table (and its "Adjustment history" link) only renders once a
  // grade's own "lots" toggle is open.
  { label: "Stock adjustment history", path: "/stock", ready: stockLots,
    reveal: (page) => stockLots(page).click(), locator: stockAdjustmentHistory },
];

// Feed and Reports carry no `CONSOLE_LINK_SX` button and their only
// `className="link"` element is gated behind a load-more page or an error
// state neither screen reaches under the default fixture. This still tests
// the real cascade on the real screen: `button.link` is the exact selector
// every such control resolves through (styles.css), so a node built with
// that class inside the screen's own live `[data-field-console]` section
// answers the same question a naturally-occurring one would.
async function fieldConsoleLinkColors(page: Page): Promise<{ idle: string; disabled: string }> {
  return page.evaluate(() => {
    const scope = document.querySelector("[data-field-console]");
    if (!scope) throw new Error("no [data-field-console] section on this screen");
    const probe = document.createElement("button");
    probe.className = "link";
    probe.textContent = "probe";
    scope.append(probe);
    const idle = getComputedStyle(probe).color;
    probe.classList.add("Mui-disabled");
    probe.setAttribute("disabled", "");
    const disabled = getComputedStyle(probe).color;
    probe.remove();
    return { idle, disabled };
  });
}

for (const brand of BRANDS) {
  test(`Field Console and Sales row-action links clear WCAG contrast in dark/${brand} (#930)`, async ({ page, signIn }) => {
    await page.emulateMedia({ colorScheme: "dark" });
    await signIn(owner());
    let surface = "";
    let surface2 = "";

    for (const check of ROUTE_CHECKS) {
      await page.goto(check.path);
      await expect(check.ready(page)).toBeVisible();
      // Set AFTER the screen's own data-loaded state, never right after
      // goto: FarmContext applies the farm's real (aubergine) brand once its
      // account bootstrap resolves, and that would otherwise overwrite this
      // override.
      await setBrand(page, brand);
      if (check.reveal) await check.reveal(page);
      if (surface === "") {
        surface = await probeVar(page, "--surface");
        surface2 = await probeVar(page, "--surface-2");
      }
      const target = check.locator(page);
      await expect(target).toBeVisible();
      const idle = await target.evaluate((node) => getComputedStyle(node).color);
      const disabled = await disabledColorOf(target);
      assertContrast(brand, check.label, idle, disabled, surface, surface2);
    }

    for (const [path, label] of [["/feed", "Feed"], ["/reports", "Reports"]] as const) {
      await page.goto(path);
      await expect(page.locator("[data-field-console]")).toBeVisible();
      await setBrand(page, brand);
      const { idle, disabled } = await fieldConsoleLinkColors(page);
      assertContrast(brand, label, idle, disabled, surface, surface2);
    }

    // Sales joined FieldConsole's row-action styling after #927, via its own
    // LINK_ACTION_SX copy — including the Draft line item's own save/cancel
    // pair the issue calls out by name.
    await page.goto("/sales");
    const draft = page.getByRole("row").filter({ hasText: "Sim Customer 1" }).filter({ hasText: tEn("enums:status.Draft") });
    await expect(draft).toBeVisible();
    await setBrand(page, brand);
    await draft.getByRole("button", { name: tEn("sales:open"), exact: true }).click();
    const manifest = page.getByRole("region").getByRole("table").first();
    await manifest.getByRole("button", { name: tEn("sales:edit"), exact: true }).first().click();
    const save = manifest.getByRole("button", { name: tEn("sales:save"), exact: true });
    const saveIdle = await save.evaluate((el) => getComputedStyle(el).color);
    const saveDisabled = await disabledColorOf(save);
    assertContrast(brand, "Sales Draft Save", saveIdle, saveDisabled, surface, surface2);
  });
}
