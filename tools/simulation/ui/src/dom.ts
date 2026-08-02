// tools/simulation/ui/src/dom.ts — small locator helpers.

import { expect, type Locator } from "@playwright/test";

/**
 * Pick a `<select>` option by a SUBSTRING of its visible text, and return the
 * value that was chosen.
 *
 * Playwright's `selectOption({ label })` matches the label EXACTLY, which is the
 * wrong tool for most of this app's dropdowns: the flock picker renders
 * `{name} ({breed})` plus a `dailyEntry:depletedFlockSuffix` for depleted flocks,
 * so an exact match on a flock's name silently finds nothing and the spec dies
 * 45 seconds later as "did not find some options" — a message that says nothing
 * about the mismatch being one of formatting.
 *
 * Requiring EXACTLY ONE match is the point of the helper, not a detail: two
 * flocks called "Sim House A" and "Sim House A (old)" would otherwise resolve to
 * whichever came first, and the spec would keep passing against the wrong row.
 */
export async function selectOptionContaining(select: Locator, needle: string): Promise<string> {
  const options = select.locator("option").filter({ hasText: needle });

  // Wait for the options to render before counting — these dropdowns are
  // populated by a fetch, so a bare count() races the load and reports 0.
  await expect(
    options,
    `no <option> containing "${needle}" — the list did not load, or the label format changed`,
  ).toHaveCount(1);

  const value = await options.getAttribute("value");
  if (value === null) throw new Error(`The <option> matching "${needle}" has no value attribute.`);
  await select.selectOption(value);
  return value;
}
