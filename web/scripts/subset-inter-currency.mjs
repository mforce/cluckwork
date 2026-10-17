#!/usr/bin/env node
// #1322 — the currency symbols from fontsource's Inter latin-ext face, as their
// own small face. The precache keeps this one and leaves the 130 KiB latin-ext
// face to load on demand, so ₱ ₹ ₫ ₩ ₪ still draw in Inter offline.
//
// Regenerate after an @fontsource-variable/inter bump: `npm run fonts:subset`.
// `--check` exits 1 instead of writing when the committed file is stale;
// opticalSize.test.ts runs it.

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import subsetFont from "subset-font";

/** latin-ext's currency ranges in fontsource's `opsz.css`; € (U+20AC) is in the latin face. */
export const CURRENCY_UNICODE_RANGE = "U+20A0-20AB,U+20AD-20C0";

// Plain paths: Vite rewrites `new URL(…, import.meta.url)` into an asset URL
// when the unit tests import this file.
const web = join(dirname(fileURLToPath(import.meta.url)), "..");
export const SOURCE_FACE = join(web, "node_modules/@fontsource-variable/inter/files/inter-latin-ext-opsz-normal.woff2");
export const CURRENCY_FACE = join(web, "src/assets/fonts/inter-currency-opsz-normal.woff2");

export function subsetInterCurrency() {
  let text = "";
  for (const range of CURRENCY_UNICODE_RANGE.split(",")) {
    const [first, last] = range.slice(2).split("-").map((hex) => parseInt(hex, 16));
    for (let code = first; code <= last; code++) text += String.fromCodePoint(code);
  }
  return subsetFont(readFileSync(SOURCE_FACE), text, { targetFormat: "woff2" });
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const fresh = await subsetInterCurrency();
  if (!process.argv.includes("--check")) {
    writeFileSync(CURRENCY_FACE, fresh);
    console.log(`wrote ${CURRENCY_FACE}`);
  } else if (!fresh.equals(readFileSync(CURRENCY_FACE))) {
    console.error(`${CURRENCY_FACE} is stale: run \`npm run fonts:subset\` and commit it.`);
    process.exit(1);
  }
}
