import { screen } from "@testing-library/react";

// #557 — the cheap way to get a table row in a screen test.
//
// The obvious spelling, `ByRole("row", { name: /…/ })`, makes Testing Library
// compute the ACCESSIBLE NAME of every row in the document: a recursive walk of
// each row's subtree, concatenating its text. On a page-size list (100 rows ×
// several cells) that is thousands of node visits per attempt, and `findBy*`
// re-runs the whole query on every DOM mutation until it matches — so a paged
// load, which re-renders the table repeatedly, multiplies it again. Measured on
// CustomersPage's paging suite: 616ms for the role-name spelling against 71ms
// for this one, on the same paging dance. Under a loaded CI runner the former
// intermittently overran the 5000ms default and failed the build.
//
// So: match ONE cell's text (a single indexed text query) and walk up to its
// row. Same row, same assertions afterwards, a fraction of the work.
//
// The selector is `td, td a, td time` — a bare cell, a link inside one (#512
// US5: Customer/Dashboard names became real Sales links), or a <time> inside
// one (#650: list dates carry their ISO value in datetime) — so a table whose first
// column is a `<th scope="row">` — a legitimate a11y spelling none of this
// app's tables uses yet — will not be found by these, and the failure reads
// as "text not present" rather than "wrong selector". Widen the selector
// here rather than reaching back for the role query if that day comes.

function rowOf(cell: HTMLElement, text: string): HTMLElement {
  const row = cell.closest("tr");
  if (row === null) {
    throw new Error(`Found a cell with the text "${text}", but it has no <tr> ancestor.`);
  }
  return row as HTMLTableRowElement;
}

// The row whose cell reads exactly `text`, once it is on screen. Use when the
// row arrives from an async load, in place of `await screen.findByRole("row", …)`.
export async function findRowByCellText(text: string): Promise<HTMLElement> {
  return rowOf(await screen.findByText(text, { selector: "td, td a, td time" }), text);
}

// The row whose cell reads exactly `text`, which must already be rendered. Use
// in place of `screen.getByRole("row", …)`.
export function getRowByCellText(text: string): HTMLElement {
  return rowOf(screen.getByText(text, { selector: "td, td a, td time" }), text);
}
