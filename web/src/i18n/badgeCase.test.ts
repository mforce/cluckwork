import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// #652 removed `text-transform: uppercase` from `.badge`. Two badge strings had
// been authored in lower case BECAUSE the stylesheet capitalised them, so they
// began rendering as "no entry" and "yes" — which reads as a typo, not as
// sentence case. A user saw it on the dashboard; no selector sweep or
// stylesheet walk can, because the defect is in the text, not the CSS.
//
// The convention this pins: a key ending in `Badge` is rendered inside a
// `.badge` pill, and a pill's label starts with a capital. Enumerated from the
// locale files rather than listed here, so a new badge string is covered the
// day it is added rather than the day someone remembers this test.
const LOCALES = ["en", "es", "tl"] as const;

function badgeStrings(locale: string): [string, string][] {
  const src = readFileSync(resolve(process.cwd(), `src/i18n/${locale}.ts`), "utf8");
  const out: [string, string][] = [];
  const re = /^\s*([A-Za-z0-9_]*Badge)\s*:\s*"([^"]*)"/gm;
  for (let m = re.exec(src); m !== null; m = re.exec(src)) out.push([m[1], m[2]]);
  return out;
}

describe.each(LOCALES)("%s: badge labels are capitalised", (locale) => {
  const entries = badgeStrings(locale);

  it("finds badge strings at all — a regex that matches nothing would pass every assertion below", () => {
    expect(entries.length).toBeGreaterThanOrEqual(4);
  });

  it.each(entries)("%s starts with a capital", (_key, value) => {
    // Interpolation placeholders and digits are legitimate openers; a lower-case
    // letter is the defect this guard exists for.
    expect(value[0]).not.toMatch(/\p{Ll}/u);
  });
});
