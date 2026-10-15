import { expect, it } from "vitest";
import { screen } from "@testing-library/react";
import { renderWithProviders } from "../../test/renderWithProviders";
import { PeriodChange } from "./PeriodChange";

it("normalizes a tiny decrease to unsigned zero after rounding", () => {
  renderWithProviders(<PeriodChange current={9999} previous={10000} days={30} />);
  expect(screen.getByText("0.0% vs previous 30 days")).toBeInTheDocument();
});
