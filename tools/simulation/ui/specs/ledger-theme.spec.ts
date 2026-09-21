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
// ACTUAL computed colour of the real row-action buttons FieldConsole and
// Sales render, in every dark palette, for both the idle and the
// MUI-disabled state (MUI's own default dark `action.disabled`,
// rgba(255,255,255,0.3), clears only ~2.6:1 against --surface/--surface-2 —
// under the 3:1 floor this issue also requires).
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
// (ignoring alpha) measures it as opaque white against a dark surface, the
// best possible contrast rather than the ~2.6:1 it truly paints as. A first
// version of this helper did exactly that and passed the disabled floor
// vacuously — caught by a manual mutation check, not by this suite, which is
// why alpha compositing is load-bearing here and not simplification.
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

for (const brand of BRANDS) {
  test(`Field Console and Sales row-action links clear WCAG contrast in dark/${brand} (#930)`, async ({ page, signIn }) => {
    await page.emulateMedia({ colorScheme: "dark" });
    await signIn(owner());

    await page.goto("/history");
    const adjust = page.getByRole("button", { name: tEn("history:adjustButton"), exact: true }).first();
    await expect(adjust).toBeVisible();
    // Set AFTER the screen's own data-loaded state, never right after goto:
    // FarmContext applies the farm's real (aubergine) brand once its account
    // bootstrap resolves, and that would otherwise overwrite this override.
    await setBrand(page, brand);
    const surface = await probeVar(page, "--surface");
    const surface2 = await probeVar(page, "--surface-2");

    const idle = await adjust.evaluate((el) => getComputedStyle(el).color);
    expect(contrastOf(idle, surface), `${brand}: idle vs --surface`).toBeGreaterThanOrEqual(4.5);
    expect(contrastOf(idle, surface2), `${brand}: idle vs --surface-2`).toBeGreaterThanOrEqual(4.5);
    const disabled = await disabledColorOf(adjust);
    expect(contrastOf(disabled, surface), `${brand}: disabled vs --surface`).toBeGreaterThanOrEqual(3);
    expect(contrastOf(disabled, surface2), `${brand}: disabled vs --surface-2`).toBeGreaterThanOrEqual(3);

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
    expect(contrastOf(saveIdle, surface), `${brand}: Sales save idle vs --surface`).toBeGreaterThanOrEqual(4.5);
    expect(contrastOf(saveIdle, surface2), `${brand}: Sales save idle vs --surface-2`).toBeGreaterThanOrEqual(4.5);
    const saveDisabled = await disabledColorOf(save);
    expect(contrastOf(saveDisabled, surface), `${brand}: Sales save disabled vs --surface`).toBeGreaterThanOrEqual(3);
    expect(contrastOf(saveDisabled, surface2), `${brand}: Sales save disabled vs --surface-2`).toBeGreaterThanOrEqual(3);
  });
}
