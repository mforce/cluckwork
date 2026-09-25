// #941 — the expanded Lay rate chart's custom-range date fields, at phone width.
//
// A native `input[type="date"]` lays its own date text and its picker icon out
// inside one content box, in a UA shadow tree nothing here can reach: the icon
// has no queryable box, and `scrollWidth` stays equal to `clientWidth` however
// narrow the field gets, because Chromium compresses its internal fields
// rather than reporting overflow. Measured at 390 on aa9d700, the To field was
// 142px holding a 92px date plus 28px of padding plus the icon, and the date
// ended 3 columns short of it while every DOM-level number looked healthy.
//
// So this one spec DOES assert pixels, which phone.spec.ts deliberately does
// not. It is not a visual regression check — no baseline, no byte diff. It
// screenshots the inside of each field, asks which COLUMNS carry ink, and
// requires clear background between the last column of the date and the first
// column of the icon. That is the claim the defect broke, and the only place
// it can be read.
//
// English only, and that is not a gap: `input[type="date"]` renders its date in
// the BROWSER's locale, never the app's, so the text this measures is the same
// string under es and tl. What does change per locale is the field's label,
// which sits in the outline's notch above the input and cannot reach the icon.
import { expect, type Locator, type Page } from "../src/fixtures";
import { test } from "../src/fixtures";
import { readmeFarmOwner } from "../src/cast";
import { tEn } from "../src/i18n";

// Clear background columns between the date and the icon. Well over the 1-2
// columns an inter-glyph gap inside the date itself measures, and well under
// the 84 the roomy From field had on the same build.
const MIN_CLEAR_PX = 8;

// The outlined border's own pixel bleeds into the first and last column of the
// input's box, and read as ink it makes every column ink.
const BORDER_INSET_PX = 2;

interface FieldScan { clear: number; width: number; runs: string[] }

/**
 * Clear columns between the rightmost ink run (the picker icon) and the ink
 * before it (the end of the date). Zero when the two touch, because a merged
 * run leaves only an inter-glyph gap to measure back to.
 */
async function scanField(page: Page, input: Locator): Promise<FieldScan> {
  const box = await input.boundingBox();
  if (box === null) throw new Error("the date field has no box to scan");
  const png = (await page.screenshot({
    animations: "disabled",
    clip: {
      x: box.x + BORDER_INSET_PX, y: box.y,
      width: box.width - BORDER_INSET_PX * 2, height: box.height,
    },
  })).toString("base64");

  return page.evaluate(async (data) => {
    // Decoded from the bytes rather than fetched from a data: URL: the app
    // serves a strict CSP and `connect-src 'self'` refuses that fetch.
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const bitmap = await createImageBitmap(new Blob([bytes], { type: "image/png" }));
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    const context = canvas.getContext("2d")!;
    context.drawImage(bitmap, 0, 0);
    const { data: px, width, height } = context.getImageData(0, 0, bitmap.width, bitmap.height);
    const pixel = (x: number, y: number): [number, number, number] => {
      const i = (y * width + x) * 4;
      return [px[i] ?? 0, px[i + 1] ?? 0, px[i + 2] ?? 0];
    };
    // The middle band: the date and the icon are both vertically centred, and
    // the field's own top and bottom rows carry chrome the scan must not read.
    const top = Math.round(height * 0.3);
    const bottom = Math.round(height * 0.7);
    // The most common colour in that band is the field's background, whatever
    // the theme. Sampled from a corner instead, it picked up the border.
    const tally = new Map<string, number>();
    for (let y = top; y < bottom; y += 1) {
      for (let x = 0; x < width; x += 1) {
        const key = pixel(x, y).join(",");
        tally.set(key, (tally.get(key) ?? 0) + 1);
      }
    }
    const ranked = [...tally.entries()].sort((a, b) => b[1] - a[1]);
    const [bgR = 0, bgG = 0, bgB = 0] = (ranked[0]?.[0] ?? "0,0,0").split(",").map(Number);
    const ink: boolean[] = [];
    for (let x = 0; x < width; x += 1) {
      let found = false;
      for (let y = top; y < bottom && !found; y += 1) {
        const [r, g, b] = pixel(x, y);
        // Generous, so an anti-aliased glyph edge does not read as background
        // and manufacture a gap that is not there.
        found = Math.abs(r - bgR) + Math.abs(g - bgG) + Math.abs(b - bgB) > 60;
      }
      ink.push(found);
    }

    const runs: string[] = [];
    let open = -1;
    for (let x = 0; x <= width; x += 1) {
      if (x < width && ink[x] && open === -1) open = x;
      else if ((x === width || !ink[x]) && open !== -1) { runs.push(`${open}-${x - 1}`); open = -1; }
    }

    let x = width - 1;
    while (x >= 0 && !ink[x]) x -= 1;   // trailing background
    if (x < 0) return { clear: width, width, runs };
    while (x >= 0 && ink[x]) x -= 1;    // the icon
    const gapEnd = x;
    while (x >= 0 && !ink[x]) x -= 1;   // the gap this spec is about
    return { clear: gapEnd - x, width, runs };
  }, png);
}

test.describe("Expanded Lay rate custom range at phone width (#941)", { tag: "@phone" }, () => {
  test("keeps each date clear of its own picker icon", async ({ page, signIn }) => {
    await signIn(readmeFarmOwner());
    await page.goto("/");
    await page.getByRole("button", { name: tEn("dashboard:expandAriaLabel") }).click();
    const chart = page.getByRole("dialog");
    await expect(chart).toBeVisible();
    await chart.getByLabel(tEn("dashboard:rangeLabel"), { exact: true }).selectOption("custom");

    for (const label of ["dashboard:rangeFromLabel", "dashboard:rangeToLabel"] as const) {
      const field = chart.getByLabel(tEn(label));
      await expect(field).toBeVisible();
      const scan = await scanField(page, field);
      expect(
        scan.clear,
        `${tEn(label)}: clear columns between the date and the picker icon `
          + `(field ${scan.width}px, ink runs ${scan.runs.join(" ")})`,
      ).toBeGreaterThanOrEqual(MIN_CLEAR_PX);
    }
  });
});
