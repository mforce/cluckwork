// Walk rendered Field Console tables, regardless of route or row label.
// A row's own colour/weight must reach its cells; MUI variants otherwise mask it.
export function ledgerCellStyles(root: ParentNode) {
  const violations: string[] = [];
  const rows = root.querySelectorAll("[data-field-console] .MuiTableRow-root");
  const weight = (value: string) => value === "normal" ? "400" : value === "bold" ? "700" : value;
  for (const row of rows) {
    const parent = row.parentElement;
    if (!parent) continue;
    const rowStyle = getComputedStyle(row);
    const parentStyle = getComputedStyle(parent);
    for (const property of ["color", "fontWeight"] as const) {
      const normalize = property === "fontWeight" ? weight : (value: string) => value;
      const expected = normalize(rowStyle[property]);
      if (expected === normalize(parentStyle[property])) continue;
      for (const cell of row.querySelectorAll(":scope > .MuiTableCell-root")) {
        const actual = normalize(getComputedStyle(cell)[property]);
        if (actual !== expected) {
          violations.push(`${cell.textContent}: row ${property} ${expected}, cell ${actual}`);
        }
      }
    }
  }
  const footers = root.querySelectorAll("[data-field-console] .MuiTableCell-footer");
  for (const cell of footers) {
    const style = getComputedStyle(cell);
    if (style.color !== "var(--ink)" || weight(style.fontWeight) !== "600") {
      violations.push(`${cell.textContent}: footer must use var(--ink) / 600, got ${style.color} / ${style.fontWeight}`);
    }
  }
  return { rows: rows.length, footers: footers.length, violations };
}
