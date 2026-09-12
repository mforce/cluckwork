// web/src/components/StockBar.test.tsx
import { describe, it, expect } from "vitest";
import { render } from "@testing-library/react";
import { StockBar } from "./StockBar";

const bands = (root: HTMLElement) =>
  Array.from(root.querySelectorAll(".meter-stack > span")).map((s) => [(s as HTMLElement).style.width, s.className]);

describe("StockBar (#654, #777)", () => {
  it("renders one segment per grade with its exact width and grade hue, hidden from the a11y tree, and follows a re-render", () => {
    const { container, rerender } = render(<StockBar data={{
      segments: [
        { eggGradeId: "g1", gradeName: "Large", available: 1240, pct: 79.5, colorIndex: 1 },
        { eggGradeId: "g2", gradeName: "Medium", available: 320, pct: 20.5, colorIndex: 2 },
      ],
      totalAvailable: 1560, totalRestricted: 0,
    }} />);
    expect(container.querySelector(".meter-stack")).toHaveAttribute("aria-hidden", "true");
    expect(bands(container)).toEqual([["79.5%", "grade-1"], ["20.5%", "grade-2"]]);

    rerender(<StockBar data={{
      segments: [{ eggGradeId: "g9", gradeName: "Jumbo", available: 5, pct: 100, colorIndex: 3 }],
      totalAvailable: 5, totalRestricted: 0,
    }} />);
    expect(bands(container)).toEqual([["100%", "grade-3"]]);
  });
  it("renders an empty track when there are no segments", () => {
    const { container } = render(<StockBar data={{ segments: [], totalAvailable: 0, totalRestricted: 0 }} />);
    expect(container.querySelector(".meter-stack")).toBeInTheDocument();
    expect(container.querySelectorAll(".meter-stack > span")).toHaveLength(0);
  });
});
