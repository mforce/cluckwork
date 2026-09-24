import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";

// #835 — the one assertion that needs a real font engine.
//
// The unit suite (web/src/opticalSize.test.ts) proves the app IMPORTS an Inter
// entry whose faces carry the `opsz` axis. It cannot prove the browser then
// loads and uses it: jsdom has no font engine, so nothing there distinguishes
// the display cut from the text cut, and everything the issue promises lives
// in that gap.
//
// `font-optical-sizing: auto` is the CSS initial value, so measuring the same
// text against `none` is the whole test: the widths differ only when the loaded
// face really varies on opsz. No golden pixel width, which would look portable
// and in fact drift with the browser build.
//
// The probe text is LETTERS, deliberately. The figures this slice is for all
// carry `font-variant-numeric: tabular-nums`, which fixes every digit to one
// advance by design — so measuring a figure's width compares two cuts that are
// required to be the same width, and passes at ~1px of noise whichever face is
// loaded. That is a guard that reads as safety and proves nothing; the first
// version of this spec was it.
test("display text renders at a display optical size, body text does not", async ({ page, signIn }) => {
  await signIn(owner());
  // Expenses carries the largest text still set in Inter after #864 moved the
  // page headings and every Dashboard figure to Georgia: the period total at
  // 2rem, where `auto` resolves to Inter's opsz 32 display cut.
  await page.goto("/expenses");
  await expect(page.getByRole("heading", { level: 2 })).toBeVisible();

  const measured = await page.evaluate(() => {
    const probe = (px: number, mode: string) => {
      const span = document.createElement("span");
      span.style.cssText = `position:fixed;left:-9999px;white-space:pre;font-family:var(--font);`
        + `font-weight:700;font-size:${px}px;font-optical-sizing:${mode}`;
      span.textContent = "Recorded sellable eggs";
      document.body.append(span);
      const measurement = { width: span.getBoundingClientRect().width, family: getComputedStyle(span).fontFamily };
      span.remove();
      return measurement;
    };
    const leaves = [...document.body.querySelectorAll<HTMLElement>("p, span, strong, div, td, h3")]
      .filter((el) => el.childNodes.length === 1 && el.firstChild?.nodeType === Node.TEXT_NODE)
      .filter((el) => (el.textContent ?? "").trim().length >= 3);
    const biggest = leaves
      .filter((el) => !getComputedStyle(el).fontFamily.includes("Georgia"))
      .sort((a, b) => parseFloat(getComputedStyle(b).fontSize) - parseFloat(getComputedStyle(a).fontSize))[0];
    return {
      display: { auto: probe(32, "auto").width, none: probe(32, "none").width },
      body: { auto: probe(14, "auto").width, none: probe(14, "none").width },
      probeFamily: probe(32, "auto").family,
      figure: biggest === undefined ? null : {
        text: (biggest.textContent ?? "").trim().slice(0, 24),
        px: parseFloat(getComputedStyle(biggest).fontSize),
        family: getComputedStyle(biggest).fontFamily,
        opticalSizing: getComputedStyle(biggest).fontOpticalSizing,
      },
    };
  });

  // 1. The face the browser LOADED varies on opsz. A direction, not a ratio,
  //    so a font-package bump cannot fail this for the wrong reason.
  expect(measured.display.auto,
    "32px text renders identically with optical sizing on and off — the loaded "
    + "Inter face has no opsz axis (#835)")
    .toBeLessThan(measured.display.none - 4);

  // 2. Body text at 14px: `auto` lands on Inter's opsz 14 default, which is
  //    exactly what `none` renders, so this must NOT move. A rule pinning
  //    opsz 32 globally would pass assertion 1 and fail here.
  expect(Math.abs(measured.body.auto - measured.body.none),
    "14px text changed width with optical sizing — something is forcing a "
    + "display cut onto body copy (#835)")
    .toBeLessThan(0.5);

  // 3. The app's own display text is on that same face, so 1 and 2 describe
  //    it. Without this the probe could be proving a fact about a font no
  //    screen renders.
  expect(measured.figure, "no Inter text on /expenses").not.toBeNull();
  expect(measured.figure!.px,
    `the largest Inter text on /expenses is only ${measured.figure!.px}px ("${measured.figure!.text}") — `
    + "below the display range this slice is for").toBeGreaterThanOrEqual(24);
  expect(measured.figure!.family).toBe(measured.probeFamily);
  expect(measured.figure!.opticalSizing).not.toBe("none");
});
