import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { FilterBar, FilterDateField } from "./FilterBar";

describe("FilterBar", () => {
  it("renders every child control, each reachable by its own label", () => {
    render(
      <FilterBar>
        <FilterDateField label="From" value="2026-01-01" onChange={() => {}} />
        <FilterDateField label="To" value="2026-01-31" onChange={() => {}} />
      </FilterBar>,
    );
    expect(screen.getByLabelText("From", { exact: true })).toHaveValue("2026-01-01");
    expect(screen.getByLabelText("To", { exact: true })).toHaveValue("2026-01-31");
  });

  it("lays the row out as a wrapping flex row, never a column, at rest", () => {
    render(
      <FilterBar>
        <FilterDateField label="From" value="2026-01-01" onChange={() => {}} />
      </FilterBar>,
    );
    const paper = screen.getByLabelText("From").closest(".MuiPaper-root");
    expect(paper).not.toBeNull();
    expect(paper).toHaveClass("MuiPaper-outlined");
    const stack = paper!.querySelector(":scope > .MuiStack-root");
    expect(stack).not.toBeNull();
    expect(stack).toHaveStyle({ flexWrap: "wrap" });
  });

  it("a date field reports the value change the caller's onChange receives", () => {
    const onChange = vi.fn();
    render(
      <FilterBar>
        <FilterDateField label="From" value="2026-01-01" onChange={onChange} />
      </FilterBar>,
    );
    fireEvent.change(screen.getByLabelText("From", { exact: true }), { target: { value: "2026-02-01" } });
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it("still applies a caller's function-form sx alongside the bounded-width default", () => {
    render(
      <FilterBar>
        <FilterDateField
          label="From"
          value="2026-01-01"
          onChange={() => {}}
          sx={() => ({ color: "rgb(1, 2, 3)" })}
        />
      </FilterBar>,
    );
    const field = screen.getByLabelText("From", { exact: true }).closest(".MuiFormControl-root");
    expect(field).not.toBeNull();
    expect(field).toHaveStyle({ color: "rgb(1, 2, 3)" });
  });
});
