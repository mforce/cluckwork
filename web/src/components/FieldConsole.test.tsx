import { Table, TableBody, TableCell, TableFooter, TableRow } from "@mui/material";
import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { FieldConsole, LedgerTableContainer } from "./FieldConsole";
import { ledgerCellStyles } from "../test/ledgerCellStyles";

it("keeps numeric ledger values on one line while prose can wrap", () => {
  render(<FieldConsole><LedgerTableContainer><Table><TableBody><TableRow>
    {["12/3/4", "12 kg", "100.5 → 175.25"].map((value) => <TableCell key={value} align="right">{value}</TableCell>)}
    <TableCell>A note that can wrap</TableCell>
  </TableRow></TableBody></Table></LedgerTableContainer></FieldConsole>);
  for (const value of ["12/3/4", "12 kg", "100.5 → 175.25"]) {
    expect(screen.getByRole("cell", { name: value })).toHaveStyle({ whiteSpace: "nowrap" });
  }
  expect(screen.getByRole("cell", { name: "A note that can wrap" })).not.toHaveStyle({ whiteSpace: "nowrap" });
});

it("walks arbitrary rows and footer variants, rejecting emphasis masked by MUI cells", () => {
  const view = render(<FieldConsole><Table>
    <TableBody>
      <TableRow sx={{ color: "var(--muted)", fontWeight: 600 }}><TableCell>Masked</TableCell></TableRow>
      <TableRow sx={{ color: "var(--muted)", fontWeight: 600, "& .MuiTableCell-root": { color: "inherit", fontWeight: "inherit" } }}><TableCell>Inherited</TableCell></TableRow>
    </TableBody>
    <TableFooter><TableRow><TableCell>Total</TableCell></TableRow></TableFooter>
  </Table></FieldConsole>);
  const result = ledgerCellStyles(view.container);
  // Unmount the deliberately broken fixture before the suite-wide guard runs.
  view.unmount();
  expect(result.rows).toBe(3);
  expect(result.footers).toBe(1);
  expect(result.violations).toEqual([
    "Masked: row color var(--muted), cell rgba(0, 0, 0, 0.87)",
    "Masked: row fontWeight 600, cell 400",
  ]);
});
