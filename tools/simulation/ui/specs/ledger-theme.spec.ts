import { test, expect, type Page } from "../src/fixtures";
import { owner } from "../src/cast";
import { commitNamedPicker, selectOptionContaining } from "../src/dom";
import { farmToday } from "../src/farm";
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
//
// `opacity` is a SEPARATE multiplier from the colour's own alpha: styles.css's
// global `:where(button:disabled) { opacity: .5 }` composites the WHOLE
// element (already resolved colour and all) at half strength over its
// backdrop, on top of whatever alpha the colour itself carries. Skipping this
// and reading only `color` measures a disabled `--muted` button as its full,
// un-dimmed contrast — a second, independent way this floor was missed after
// the colour fix landed (a real button in dark measured 2.84:1/2.68:1, both
// under 3:1, until this file's own opacity parameter caught it).
function contrastOf(fgCss: string, bgCss: string, opacity = 1): number {
  const [fr, fgc, fb, fa] = parseRgba(fgCss);
  const [br, bg, bb] = parseRgba(bgCss);
  const effectiveAlpha = fa * opacity;
  const painted: [number, number, number] = [
    fr * effectiveAlpha + br * (1 - effectiveAlpha),
    fgc * effectiveAlpha + bg * (1 - effectiveAlpha),
    fb * effectiveAlpha + bb * (1 - effectiveAlpha),
  ];
  const [lPainted, lBg] = [luminanceOf(painted), luminanceOf([br, bg, bb])];
  return (Math.max(lPainted, lBg) + 0.05) / (Math.min(lPainted, lBg) + 0.05);
}

async function setBrand(page: Page, brand: string): Promise<void> {
  await page.evaluate((b) => {
    if (b === "aubergine") delete document.documentElement.dataset.brand;
    else document.documentElement.dataset.brand = b;
  }, brand);
}

// Toggles the real MUI class/attribute a disabled Button carries (this is a
// CASCADE PROBE, not proof of a reachable app state — see the genuine-state
// tests below for that), reads the resulting computed colour AND opacity,
// then reverts so the probed element stays interactive for anything a caller
// does with it afterwards. Opacity matters because it composites independently
// of the colour's own alpha (see `contrastOf`).
async function disabledStateOf(locator: ReturnType<Page["getByRole"]>): Promise<{ color: string; opacity: number }> {
  return locator.evaluate((el) => {
    el.classList.add("Mui-disabled");
    el.setAttribute("disabled", "");
    const style = getComputedStyle(el);
    const state = { color: style.color, opacity: Number(style.opacity) };
    el.classList.remove("Mui-disabled");
    el.removeAttribute("disabled");
    return state;
  });
}

// A plain read, no state toggling — for an element ALREADY disabled by the
// app itself, not a probe forcing the state.
async function colorAndOpacityOf(locator: ReturnType<Page["getByRole"]>): Promise<{ color: string; opacity: number }> {
  return locator.evaluate((el) => {
    const style = getComputedStyle(el);
    return { color: style.color, opacity: Number(style.opacity) };
  });
}

function assertContrast(
  brand: string, label: string, idle: string, disabled: { color: string; opacity: number },
  surface: string, surface2: string,
): void {
  expect(contrastOf(idle, surface), `${brand}: ${label} idle vs --surface`).toBeGreaterThanOrEqual(4.5);
  expect(contrastOf(idle, surface2), `${brand}: ${label} idle vs --surface-2`).toBeGreaterThanOrEqual(4.5);
  expect(contrastOf(disabled.color, surface, disabled.opacity), `${brand}: ${label} disabled vs --surface`).toBeGreaterThanOrEqual(3);
  expect(contrastOf(disabled.color, surface2, disabled.opacity), `${brand}: ${label} disabled vs --surface-2`).toBeGreaterThanOrEqual(3);
}

// FORCED-STATE CASCADE PROBES, one per screen: a real element, on the real
// screen, with `.Mui-disabled`/`disabled` forced onto it to read the CSS rules
// that state activates. None of these prove the app ever REACHES that state —
// most of these controls have no `disabled` prop at all (`manageCategories`,
// `chooseAnotherItem`, `writeOffButton`) or are only disabled while a request
// is genuinely in flight, which this loop does not attempt to reach. That
// proof is the separate, dedicated tests further down, one per distinct
// disabled-styling implementation (a CONSOLE_LINK_SX MUI Button, Sales Draft
// Save's `editConflict`, a raw `button.link`) — this loop's job is the
// cascade across all eight screens and four palettes, not app-state coverage.
//
// `ready` is what the screen shows BEFORE any reveal — the signal that
// FarmContext's own account bootstrap has resolved, so `setBrand` (which
// follows) isn't overwritten by it. `reveal` then runs, `locator` is the
// FINAL target it uncovers, and `disabledLocator` (default: `locator`) is
// what the disabled half of the probe forces state onto — distinct from
// `locator` only where forcing `disabled` on the idle element would be
// meaningless (Stock's "Adjustment history" is an `<a>`; HTML has no
// `disabled` attribute or `:disabled` pseudo-class for anchors, so the probe
// targets the SAME screen's real `button.link` "write off" control instead).
type RouteCheck = {
  label: string; path: string;
  ready: (page: Page) => ReturnType<Page["getByRole"]>;
  reveal?: (page: Page) => Promise<void>;
  locator: (page: Page) => ReturnType<Page["getByRole"]>;
  disabledLocator?: (page: Page) => ReturnType<Page["getByRole"]>;
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
const stockWriteOff = (page: Page) => page.getByRole("button", { name: tEn("stock:writeOffButton"), exact: true }).first();
const ROUTE_CHECKS: RouteCheck[] = [
  { label: "History adjust", path: "/history", ready: historyAdjust, locator: historyAdjust },
  { label: "Expenses manage categories", path: "/expenses", ready: expensesManageCategories, locator: expensesManageCategories },
  { label: "Water correct", path: "/water", ready: waterCorrect, locator: waterCorrect },
  { label: "Inventory choose another item", path: "/inventory", ready: inventoryOpen,
    reveal: (page) => inventoryOpen(page).click(), locator: inventoryChooseAnother },
  // The lots table (and its "Adjustment history" link and "write off" button)
  // only renders once a grade's own "lots" toggle is open.
  { label: "Stock adjustment history", path: "/stock", ready: stockLots,
    reveal: (page) => stockLots(page).click(), locator: stockAdjustmentHistory, disabledLocator: stockWriteOff },
];

// Feed and Reports carry no `CONSOLE_LINK_SX` button and their only
// `className="link"` element is gated behind a load-more page or an error
// state neither screen reaches under the default fixture. This is a CASCADE
// PROBE, same disclosure as `ROUTE_CHECKS` above: it tests the real cascade
// on the real screen — `button.link` is the exact selector every such control
// resolves through (styles.css), so a node built with that class inside the
// screen's own live `[data-field-console]` section answers the same cascade
// question a naturally-occurring one would — but it is not evidence the app
// itself ever disables a control there.
async function fieldConsoleLinkColors(page: Page): Promise<{ idle: string; disabled: { color: string; opacity: number } }> {
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
    const disabledStyle = getComputedStyle(probe);
    const disabled = { color: disabledStyle.color, opacity: Number(disabledStyle.opacity) };
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
      const disabledTarget = check.disabledLocator ? check.disabledLocator(page) : target;
      const disabled = await disabledStateOf(disabledTarget);
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
    const saveDisabled = await disabledStateOf(save);
    assertContrast(brand, "Sales Draft Save", saveIdle, saveDisabled, surface, surface2);
  });
}

// #930 P2 — GENUINE app-reachable disabled states, one per distinct
// disabled-styling implementation this issue's controls use: a CONSOLE_LINK_SX
// MUI Button, a raw button.link, and Sales Draft Save's `editConflict` (which,
// unlike busy, carries no `aria-busy` and so is NOT covered by the existing
// `:where(button:disabled[aria-busy="true"])` opacity exception — this is the
// specific case that stayed broken after the colour-only fix). Unlike the
// forced-state probes above, these hold a real request or a real stale-data
// mismatch so the browser reaches the state the way a user would, not by
// toggling a class from outside.
//
// --muted, --surface, --surface-2, and styles.css's global disabled-opacity
// rule are all palette-invariant (none is redeclared per data-brand block —
// confirmed by grep, and by the four palette-sweep runs above measuring the
// identical disabled contrast regardless of brand), so one dark-mode run of
// each proves every palette; repeating these across all four would only
// re-measure the same two numbers at real request-hold cost each time.
test("History's shared busy state genuinely disables an adjust button (CONSOLE_LINK_SX) without aria-busy, in dark (#930)", async ({ page, signIn }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await signIn(owner());
  await page.goto("/history");
  const surface = await probeVar(page, "--surface");
  const surface2 = await probeVar(page, "--surface-2");

  const row = page.getByRole("row").filter({ has: page.getByRole("button", { name: tEn("history:voidButton"), exact: true }) }).first();
  await expect(row).toBeVisible();
  const adjustBtn = row.getByRole("button", { name: tEn("history:adjustButton"), exact: true });
  await expect(adjustBtn).toBeEnabled();
  const adjustIdle = await adjustBtn.evaluate((el) => getComputedStyle(el).color);

  await row.getByRole("button", { name: tEn("history:voidButton"), exact: true }).click();
  const voidDialog = page.getByRole("dialog").filter({ has: page.getByRole("button", { name: tEn("history:voidConfirmLabel"), exact: true }) });
  await voidDialog.getByRole("textbox").fill("cw930 contrast probe — held, never sent");

  // Held at the network layer and aborted, never forwarded — the entry is
  // never actually voided, so the shared simulation fixture is untouched.
  // `.catch()` on the abort itself, not the hold: Chromium can independently
  // cancel a held request on its own (a page navigation would do it; none
  // happens here, but the browser's own bookkeeping is not this test's to
  // assert on) and Playwright then refuses a second `abort()` on the same
  // route with "Route is already handled!" — a race in request lifecycle,
  // not evidence about the assertions already taken above this point.
  let release: () => void = () => {};
  const held = new Promise<void>((resolve) => { release = resolve; });
  await page.route("**/daily-entries/*/void", async (route) => {
    await held;
    await route.abort().catch(() => {});
  });
  await voidDialog.getByRole("button", { name: tEn("history:voidConfirmLabel"), exact: true }).click();

  // `useDialogAction`'s `busy` is page-wide: every row's adjust button is
  // disabled without its OWN `aria-busy` — the voiding button itself is a
  // BusyButton and keeps full opacity via the existing exception, which is
  // exactly why that exception does not save this sibling. (History's other
  // raw button.link, "load more", is a WEAKER case than this one: it isn't
  // merely disabled while busy, it UNMOUNTS — usePagedList's canLoadMore is
  // `hasMore && !loading`, and runWrite sets `loading` for the duration of
  // any write on this list. A hidden control has no rendered contrast to
  // fail, so this path is covered by Inventory's `openButton` below instead,
  // which stays mounted and merely disables.)
  await expect(adjustBtn).toBeDisabled();
  const adjustDisabled = await colorAndOpacityOf(adjustBtn);
  assertContrast("aubergine", "History adjust (genuine busy, CONSOLE_LINK_SX)", adjustIdle, adjustDisabled, surface, surface2);

  release();
  await page.unroute("**/daily-entries/*/void");
});

test("Inventory's shared busy state genuinely disables a raw button.link without aria-busy, in dark (#930)", async ({ page, signIn }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await signIn(owner());
  await page.goto("/inventory");
  const surface = await probeVar(page, "--surface");
  const surface2 = await probeVar(page, "--surface-2");

  // Two distinct rows: one to deactivate (the source of the busy flag), one
  // whose OWN "open" button — unconditionally rendered, never hidden by the
  // action in flight — is observed disabled by that shared flag.
  const rows = page.getByRole("row").filter({ has: page.getByRole("button", { name: tEn("inventory:openButton"), exact: true }) });
  await expect(rows.first()).toBeVisible();
  expect(await rows.count(), "Inventory needs at least 2 items for this probe").toBeGreaterThanOrEqual(2);
  const observedOpen = rows.nth(0).getByRole("button", { name: tEn("inventory:openButton"), exact: true });
  const deactivateRow = rows.nth(1);
  const deactivateBtn = deactivateRow.getByRole("button", { name: tEn("inventory:deactivateButton"), exact: true });
  const activateBtn = deactivateRow.getByRole("button", { name: tEn("inventory:activateButton"), exact: true });
  // Whichever of the pair is currently offered — this probe reverses nothing
  // it does not also hold open forever, so either direction is fine.
  const toggleBtn = (await deactivateBtn.isVisible().catch(() => false)) ? deactivateBtn : activateBtn;

  await expect(observedOpen).toBeEnabled();
  const openIdle = await observedOpen.evaluate((el) => getComputedStyle(el).color);

  let release: () => void = () => {};
  const held = new Promise<void>((resolve) => { release = resolve; });
  await page.route("**/inventory/items/*/*activate", async (route) => {
    await held;
    await route.abort().catch(() => {});
  });
  await toggleBtn.click();

  await expect(observedOpen).toBeDisabled();
  const openDisabled = await colorAndOpacityOf(observedOpen);
  assertContrast("aubergine", "Inventory open (genuine busy, raw button.link)", openIdle, openDisabled, surface, surface2);

  release();
  await page.unroute("**/inventory/items/*/*activate");
});

test("Sales Draft Save genuinely disables via editConflict, without aria-busy, in dark (#930)", async ({ page, signIn, farm }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await signIn(owner());
  const surface = await probeVar(page, "--surface");
  const surface2 = await probeVar(page, "--surface-2");

  // Self-owned order (manager.spec.ts's doctrine): editConflict needs the
  // EDITED item's own server value to differ from what it was when editing
  // started, which this test manufactures by rewriting a refetch response —
  // reusing "Sim Customer 1"'s shared draft would risk another spec reading
  // a line this test perturbed.
  const customerName = `cw930 Conflict Probe ${Date.now()}`;
  await page.goto("/customers");
  await page.getByRole("button", { name: tEn("customers:newCustomerButton"), exact: true }).click();
  const customerDialog = page.getByRole("dialog", { name: tEn("customers:newCustomerButton"), exact: true });
  await customerDialog.getByLabel(tEn("customers:nameFieldLabel"), { exact: true }).fill(customerName);
  await customerDialog.getByLabel(tEn("customers:phoneFieldLabel"), { exact: true }).fill("555-0199");
  await customerDialog.getByRole("button", { name: tEn("customers:addCustomerButton"), exact: true }).click();
  await expect(customerDialog).toBeHidden();

  await page.goto("/sales");
  await page.getByRole("button", { name: tEn("sales:newOrder"), exact: true }).click();
  const orderDialog = page.getByRole("dialog", { name: tEn("sales:newOrder"), exact: true });
  await commitNamedPicker(orderDialog, tEn("sales:customer"), customerName);
  await orderDialog.getByLabel(tEn("sales:date"), { exact: true }).fill(farmToday(farm.timeZoneId));
  const [creationResponse] = await Promise.all([
    page.waitForResponse((res) => res.request().method() === "POST" && /\/api\/v1\/sales$/.test(new URL(res.url()).pathname)),
    orderDialog.getByRole("button", { name: tEn("sales:newDraftOrder"), exact: true }).click(),
  ]);
  const orderId: string = (await creationResponse.json()).id;
  await expect(orderDialog).toBeHidden();

  await selectOptionContaining(page.getByLabel(tEn("sales:product"), { exact: true }), "Sim Large Eggs");
  await page.getByLabel(tEn("sales:quantityWithUnit", { unit: tEn("sales:unitEgg").toLowerCase() }), { exact: true }).fill("10");
  await page.getByLabel(tEn("sales:unitPriceWithCurrency", { code: farm.currencyCode })).fill("0.50");
  await page.getByRole("button", { name: tEn("sales:addLine"), exact: true }).click();
  const manifest = page.getByRole("region").getByRole("table").first();
  await expect(manifest.getByRole("row").filter({ hasText: "Sim Large Eggs" })).toHaveCount(1);

  await manifest.getByRole("button", { name: tEn("sales:edit"), exact: true }).first().click();
  // Not `exact: true`: SalesPage.tsx renders this field's own label
  // lowercased (`t("editQuantityAriaLabel").toLowerCase()`), so it never
  // matches the catalog string's original casing exactly.
  const qtyField = manifest.getByRole("spinbutton", { name: tEn("sales:editQuantityAriaLabel"), exact: false });
  await qtyField.fill("25");
  const save = manifest.getByRole("button", { name: tEn("sales:save"), exact: true });
  const saveIdle = await save.evaluate((el) => getComputedStyle(el).color);

  // Simulates a concurrent edit landing between this line's own open and its
  // save: rewrites the SAME order's refetch response (fired by adding a
  // second, unrelated line — a real, additive write this test's own order
  // absorbs) so the first item's quantity comes back different from what
  // `editor.serverQuantity` snapshotted at open time.
  await page.route(`**/api/v1/sales/${orderId}`, async (route) => {
    if (route.request().method() !== "GET") return route.continue();
    const response = await route.fetch();
    const body = await response.json();
    if (Array.isArray(body.items) && body.items.length > 0) body.items[0].quantity += 5;
    await route.fulfill({ response, json: body });
  });
  await selectOptionContaining(page.getByLabel(tEn("sales:product"), { exact: true }), "Sim Large Eggs");
  await page.getByLabel(tEn("sales:quantityWithUnit", { unit: tEn("sales:unitEgg").toLowerCase() }), { exact: true }).fill("1");
  await page.getByLabel(tEn("sales:unitPriceWithCurrency", { code: farm.currencyCode })).fill("0.50");
  await page.getByRole("button", { name: tEn("sales:addLine"), exact: true }).click();
  await expect(manifest.getByRole("row").filter({ hasText: "Sim Large Eggs" })).toHaveCount(2);

  await expect(page.getByText(tEn("sales:editConflict"))).toBeVisible();
  await expect(save).toBeDisabled();
  const saveDisabled = await colorAndOpacityOf(save);
  assertContrast("aubergine", "Sales Draft Save (genuine editConflict, no aria-busy)", saveIdle, saveDisabled, surface, surface2);

  await page.unroute(`**/api/v1/sales/${orderId}`);
});
