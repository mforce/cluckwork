// web/src/components/StockBar.test.tsx
import { describe, it, expect } from "vitest";
import { render } from "@testing-library/react";
import { StockBar } from "./StockBar";

const widths = (root: HTMLElement) =>
  Array.from(root.querySelectorAll(".meter-stack > span")).map((s) => [(s as HTMLElement).style.width, (s as HTMLElement).style.opacity]);

describe("StockBar (#654)", () => {
  it("renders one segment per grade with its exact width and opacity, hidden from the a11y tree, and follows a re-render", () => {
    const { container, rerender } = render(<StockBar data={{
      segments: [
        { eggGradeId: "g1", gradeName: "Large", available: 1240, pct: 79.5, opacity: 1 },
        { eggGradeId: "g2", gradeName: "Medium", available: 320, pct: 20.5, opacity: 0.87 },
      ],
      totalAvailable: 1560, totalRestricted: 0,
    }} />);
    expect(container.querySelector(".meter-stack")).toHaveAttribute("aria-hidden", "true");
    expect(widths(container)).toEqual([["79.5%", "1"], ["20.5%", "0.87"]]);

    rerender(<StockBar data={{
      segments: [{ eggGradeId: "g9", gradeName: "Jumbo", available: 5, pct: 100, opacity: 1 }],
      totalAvailable: 5, totalRestricted: 0,
    }} />);
    expect(widths(container)).toEqual([["100%", "1"]]);
  });
  it("renders an empty track when there are no segments", () => {
    const { container } = render(<StockBar data={{ segments: [], totalAvailable: 0, totalRestricted: 0 }} />);
    expect(container.querySelector(".meter-stack")).toBeInTheDocument();
    expect(container.querySelectorAll(".meter-stack > span")).toHaveLength(0);
  });
});
