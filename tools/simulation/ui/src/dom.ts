// tools/simulation/ui/src/dom.ts — small locator helpers.

import { expect, type Locator, type Page } from "@playwright/test";

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

/**
 * Commit a #512 searchable named-entity picker (FlockPicker/CustomerPicker) —
 * NOT a native `<select>` — by typing a substring unique to the target's name
 * and selecting the sole matching result. Returns the committed entity's id,
 * parsed from the picker's own option element id (`{...}-opt-{entity.id}`):
 * unlike a `<select>`, the picker has no `<option value>` to read directly.
 *
 * `root` should be scoped tightly enough that `labelText` is unambiguous —
 * e.g. a dialog Locator, when the same label also names a page-level filter
 * picker outside it (the Sales page's own Customer filter, for one).
 *
 * Opens the picker first if it is currently closed (a field-sized trigger
 * button whose accessible name is "<label> <current value>" via
 * aria-labelledby — matched on the stable label prefix, since the current
 * value may already be a remembered/default entity rather than a blank
 * placeholder). Pickers that are always open (e.g. a dialog-owned picker)
 * skip that step.
 */
export async function commitNamedPicker(root: Locator | Page, labelText: string, needle: string): Promise<string> {
  let combobox = root.getByRole("combobox", { name: labelText });
  if ((await combobox.count()) === 0) {
    await root.getByRole("button", { name: new RegExp(`^${labelText} `) }).click();
    combobox = root.getByRole("combobox", { name: labelText });
  }
  await expect(combobox).toBeVisible();
  await combobox.fill(needle);

  const option = root.getByRole("option", { name: needle });
  await expect(
    option,
    `no picker option containing "${needle}" for "${labelText}" — the list did not load, the fixture `
      + `does not have it, or the name format changed`,
  ).toHaveCount(1);

  const optionId = await option.getAttribute("id");
  const match = optionId?.match(/-opt-(.+)$/);
  if (!match) throw new Error(`commitNamedPicker: could not parse an entity id from option id "${optionId}".`);
  await option.click();
  return match[1]!;
}
