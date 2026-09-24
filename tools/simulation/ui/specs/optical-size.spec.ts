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
test("display text renders at a display optical size", async ({ page, signIn }) => {
  await signIn(owner());
  // Expenses carries the largest text still set in Inter after #864 moved the
  // page headings and every Dashboard figure to Georgia: the period total at
  // 2rem, where `auto` resolves to Inter's opsz 32 display cut.
  await page.goto("/expenses");
  await expect(page.getByRole("heading", { level: 2 })).toBeVisible();

  const measured = await page.evaluate(() => {
    const probe = (px: number, mode: string) => {
      const span = document.createElement("span");
      // #948 — the app now pins `font-variation-settings: "opsz" 14` at
      // :root (styles.css) so body/table text stays off the display cut;
      // that value is INHERITED, so a detached probe testing raw
      // `font-optical-sizing` behavior must clear it explicitly or it would
      // always measure the inherited pin instead of `mode`.
      span.style.cssText = `position:fixed;left:-9999px;white-space:pre;font-family:var(--font);`
        + `font-weight:700;font-size:${px}px;font-optical-sizing:${mode};font-variation-settings:normal;`;
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

  // 2. The app's own display text is on that same face, so 1 describes it.
  //    Without this the probe could be proving a fact about a font no screen
  //    renders. Whether BODY text stays off this cut is a separate question —
  //    see the real-element test below; a detached 14px probe here would only
  //    prove the fact about the probe, not about anything MUI actually renders
  //    (#948 review round 1).
  expect(measured.figure, "no Inter text on /expenses").not.toBeNull();
  expect(measured.figure!.px,
    `the largest Inter text on /expenses is only ${measured.figure!.px}px ("${measured.figure!.text}") — `
    + "below the display range this slice is for").toBeGreaterThanOrEqual(24);
  expect(measured.figure!.family).toBe(measured.probeFamily);
  expect(measured.figure!.opticalSizing).not.toBe("none");
});

// #948 review round 1 (Codex gpt-6-sol), P2: the test above proves the loaded
// face VARIES on opsz, but its body assertion was a detached span pinned to a
// literal 14px — a size nothing on the real page renders at. It could not see
// that `font-optical-sizing: auto` (the CSS default #835 relies on) varies
// CONTINUOUSLY with the element's actual font-size, so MUI body text at its
// real 16px on phones (FarmThemeProvider.tsx) drifted toward the display cut
// too — not just the six intended stat figures. (Expenses' own `<td>`s do NOT
// serve this case: `FieldConsole.tsx` pins every ledger table cell to 0.75rem
// at every breakpoint via a higher-specificity `sx` override, so this uses
// the app-bar's real body1 farm-name label instead — same mechanism, same
// bug, actually present on the page.)
//
// Two independent signals, on the SAME page:
//
//  1. Computed `font-variation-settings` — deterministic, no font-rendering
//     noise. The fix is a CSS declaration and its explicit clear, and a real
//     browser's cascade either delivers the INHERITED root pin to real body
//     text or it does not; this reads that fact directly rather than
//     inferring it from a glyph-width delta.
//  2. A width comparison against the SAME text forced to opsz 14 — proves the
//     pin (or its absence) actually changes rendering, not just the
//     declaration. Deliberately NOT run against the figure: every stat figure
//     this slice targets carries `font-variant-numeric: tabular-nums` (fixed
//     digit advance by design), so a currency figure's width barely moves
//     between opsz cuts — measured in Chromium, "$835.00" moves only 4px
//     between opsz 14 and its real (display-cut) rendering, against a ~7px
//     gap for ordinary letters at the same 32px (see the header comment on
//     the test above). The body text used here is ordinary letters, so this
//     check is safe from that trap.
test("real display figures resolve to the display cut; real body text stays at the text cut", { tag: "@phone" }, async ({ page, signIn }) => {
  await signIn(owner());
  await page.goto("/expenses");
  await expect(page.getByRole("heading", { level: 2 })).toBeVisible();
  await expect(page.locator('[aria-label^="Total for this period"]')).toBeVisible();

  const measured = await page.evaluate(() => {
    const widthAtOpsz14 = (el: HTMLElement) => {
      const cs = getComputedStyle(el);
      const span = document.createElement("span");
      span.style.cssText = "position:fixed;left:-9999px;top:-9999px;white-space:pre;"
        + `font-family:${cs.fontFamily};font-weight:${cs.fontWeight};font-size:${cs.fontSize};`
        + `letter-spacing:${cs.letterSpacing};font-variant-numeric:${cs.fontVariantNumeric};`
        + `font-variation-settings:"opsz" 14;`;
      span.textContent = el.textContent ?? "";
      document.body.append(span);
      const width = span.getBoundingClientRect().width;
      span.remove();
      return width;
    };

    // A block-level element's own bounding box can be wider than its glyphs
    // (it fills its container); a Range over the text node measures the
    // actual rendered run instead, the same quantity the synthetic probe
    // above measures.
    const textRunWidth = (el: HTMLElement) => {
      const range = document.createRange();
      range.selectNodeContents(el);
      return range.getBoundingClientRect().width;
    };

    const leaves = [...document.body.querySelectorAll<HTMLElement>("p, span, strong, div, td, h3")]
      .filter((el) => el.childNodes.length === 1 && el.firstChild?.nodeType === Node.TEXT_NODE)
      .filter((el) => (el.textContent ?? "").trim().length >= 3)
      .filter((el) => !getComputedStyle(el).fontFamily.includes("Georgia"))
      // Excludes the desktop sidebar's `display: none` farm-name label and
      // similar — computed style is still queryable for an unrendered
      // element, but it has no real layout box to measure.
      .filter((el) => el.getClientRects().length > 0);

    const figure = leaves
      .filter((el) => parseFloat(getComputedStyle(el).fontSize) >= 24)
      .sort((a, b) => parseFloat(getComputedStyle(b).fontSize) - parseFloat(getComputedStyle(a).fontSize))[0] ?? null;

    // Real MUI body1 text at its real phone size (16px, FarmThemeProvider.tsx)
    // — deliberately not the intended figure, not a caption/label sized well
    // below body scale, and single-line: a wrapped paragraph's Range spans
    // multiple lines, which is not the same quantity as the synthetic
    // single-line probe below.
    const body = leaves.find((el) => {
      if (el === figure) return false;
      const px = parseFloat(getComputedStyle(el).fontSize);
      if (px < 15 || px >= 20) return false;
      const range = document.createRange();
      range.selectNodeContents(el);
      return range.getClientRects().length === 1;
    }) ?? null;

    return {
      figure: figure === null ? null : {
        text: (figure.textContent ?? "").trim().slice(0, 32),
        fontSize: getComputedStyle(figure).fontSize,
        fontVariationSettings: getComputedStyle(figure).fontVariationSettings,
      },
      body: body === null ? null : {
        text: (body.textContent ?? "").trim().slice(0, 32),
        fontSize: getComputedStyle(body).fontSize,
        fontVariationSettings: getComputedStyle(body).fontVariationSettings,
        real: textRunWidth(body),
        atOpsz14: widthAtOpsz14(body),
      },
    };
  });

  expect(measured.figure, "no display-sized (>=24px) Inter figure on /expenses").not.toBeNull();
  expect(measured.body, "no real ~16px body text on /expenses at phone width").not.toBeNull();

  // The figure cleared the root's text-cut pin, so it is free to resolve to
  // the display cut under `font-optical-sizing: auto` (proven for real
  // letters by the test above).
  expect(measured.figure!.fontVariationSettings,
    `"${measured.figure!.text}" (${measured.figure!.fontSize}) still carries an explicit opsz pin`)
    .toBe("normal");

  // The body text never cleared anything, so the cascade must have delivered
  // the root's pin to it by inheritance.
  expect(measured.body!.fontVariationSettings,
    `"${measured.body!.text}" (${measured.body!.fontSize}) did not inherit the root's text-cut pin`)
    .toBe('"opsz" 14');
  expect(Math.abs(measured.body!.real - measured.body!.atOpsz14),
    `"${measured.body!.text}" (${measured.body!.fontSize}) drifted off the text cut toward display (#948)`)
    .toBeLessThan(3);
});
