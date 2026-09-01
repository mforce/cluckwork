import { describe, it, expect } from "vitest";
import { render, within } from "@testing-library/react";
import { findRowByCellText, getRowByCellText } from "./rows";

// #557 — these helpers exist to keep a row lookup cheap on a page-size table.
// `ByRole("row", { name })` makes Testing Library compute the accessible name of
// EVERY row (a full subtree walk each), and `findBy*` re-runs the whole query on
// every DOM mutation, so on a 100-row paged list the cost stacks up past the 5s
// default timeout on a loaded runner. Resolving the row from one cell's text is
// a single indexed text match plus a `closest("tr")`.

function Book({ names }: { names: string[] }) {
  return (
    <table>
      <tbody>
        {names.map((name) => (
          <tr key={name}>
            <td>{name}</td>
            <td>555-0100</td>
            <td>
              <button>edit</button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

describe("row lookup by cell text (#557)", () => {
  it("resolves the row that carries the named cell, not the whole table", async () => {
    render(<Book names={["Acme Eggs", "Zulu Farm"]} />);

    const row = await findRowByCellText("Zulu Farm");

    expect(row.tagName).toBe("TR");
    expect(within(row).getByText("Zulu Farm")).toBeInTheDocument();
    expect(within(row).queryByText("Acme Eggs")).not.toBeInTheDocument();
  });

  it("resolves a row already on screen without awaiting", () => {
    render(<Book names={["Acme Eggs", "Zulu Farm"]} />);

    const row = getRowByCellText("Acme Eggs");

    expect(row.tagName).toBe("TR");
    expect(within(row).getByRole("button", { name: "edit" })).toBeInTheDocument();
  });

  it("names the text it could not find when no row carries it", async () => {
    render(<Book names={["Acme Eggs"]} />);

    await expect(findRowByCellText("Zulu Farm")).rejects.toThrow(/Zulu Farm/);
    expect(() => getRowByCellText("Zulu Farm")).toThrow(/Zulu Farm/);
  });

  it("matches the cell's whole text, so a prefix of another customer is not confused for it", () => {
    render(<Book names={["Acme", "Acme Eggs"]} />);

    expect(within(getRowByCellText("Acme")).getByText("Acme")).toBeInTheDocument();
    expect(within(getRowByCellText("Acme Eggs")).getByText("Acme Eggs")).toBeInTheDocument();
  });

  it("reports the cell it found when that cell is not inside a row", () => {
    // A cell that matched but has no <tr> ancestor must not surface as a null
    // dereference three lines later in the calling test.
    render(
      <table>
        <tbody>
          <td>orphan cell</td>
        </tbody>
      </table>,
    );

    expect(() => getRowByCellText("orphan cell")).toThrow(/orphan cell/);
  });
});
