import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";

// #835 — the one assertion that needs a real font engine.
//
// The unit suite (web/src/opticalSize.test.ts) proves the app IMPORTS an Inter
// entry whose faces carry the `opsz` axis. It cannot prove the browser then
// uses it: jsdom has no font engine, so nothing there distinguishes the display
// cut from the text cut, and everything the issue promises lives in that gap.
//
// `font-optical-sizing: auto` is the CSS initial value, so measuring the same
// element against `none` is the whole test: the widths differ only when the
// loaded face really does vary on opsz. That also makes the check self-
// controlling — no golden pixel width, which would look portable and in fact
// drift with the browser build.
test("display figures render at a display optical size, body rows do not", async ({ page, signIn }) => {
  await signIn(owner());
  // Expenses carries the largest text still set in Inter after #864 moved the
  // page headings and every Dashboard figure to Georgia: the period total at
  // 2rem, where `auto` resolves to Inter's opsz 32 display cut.
  await page.goto("/expenses");
  await expect(page.getByRole("heading", { level: 2 })).toBeVisible();

  const measured = await page.evaluate(() => {
    const widthAt = (el: HTMLElement, mode: string) => {
      const previous = el.style.fontOpticalSizing;
      el.style.fontOpticalSizing = mode;
      const range = document.createRange();
      range.selectNodeContents(el);
      const width = range.getBoundingClientRect().width;
      el.style.fontOpticalSizing = previous;
      return width;
    };
    const leaves = [...document.body.querySelectorAll<HTMLElement>("p, span, strong, div, td, h3")]
      .filter((el) => el.childNodes.length === 1 && el.firstChild?.nodeType === Node.TEXT_NODE)
      .filter((el) => (el.textContent ?? "").trim().length >= 3)
      .filter((el) => !getComputedStyle(el).fontFamily.includes("Georgia"));
    const report = (el: HTMLElement | undefined) => el === undefined ? null : {
      text: (el.textContent ?? "").trim().slice(0, 24),
      px: parseFloat(getComputedStyle(el).fontSize),
      auto: widthAt(el, "auto"),
      none: widthAt(el, "none"),
    };
    const largest = (px: number) => px >= 28;
    const body = (px: number) => px > 0 && px <= 16;
    return {
      display: report(leaves.find((el) => largest(parseFloat(getComputedStyle(el).fontSize)))),
      body: report(leaves.find((el) => body(parseFloat(getComputedStyle(el).fontSize)))),
    };
  });

  expect(measured.display, "no Inter text at 28px or more on /expenses").not.toBeNull();
  expect(measured.body, "no Inter body text on /expenses").not.toBeNull();

  // The display cut is narrower than the text cut. A direction, not a ratio,
  // so a font-package bump cannot make this fail for the wrong reason.
  expect(measured.display!.auto,
    `display text at ${measured.display!.px}px ("${measured.display!.text}") renders identically with optical `
    + "sizing on and off — the loaded Inter face has no opsz axis (#835)")
    .toBeLessThan(measured.display!.none - 1);

  // Body text at 14-16px: `auto` lands on Inter's opsz 14 default, which is
  // exactly what `none` renders, so this must NOT move. A rule pinning opsz 32
  // globally would pass the assertion above and fail here.
  expect(Math.abs(measured.body!.auto - measured.body!.none),
    `body text at ${measured.body!.px}px ("${measured.body!.text}") changed width with optical sizing — `
    + "something is forcing a display cut onto body copy (#835)")
    .toBeLessThan(0.5);
});
