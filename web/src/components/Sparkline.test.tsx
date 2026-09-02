// web/src/components/Sparkline.test.tsx
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { Sparkline } from "./Sparkline";

describe("Sparkline (#654)", () => {
  it("renders exactly the points and label it is given, and follows a re-render", () => {
    const { rerender } = render(
      <Sparkline data={{ points: "0,32 50,0 100,16", values: [0, 10, 5], min: 0, max: 10, last: 5 }} label="Eggs per day" />,
    );
    const svg = screen.getByRole("img", { name: "Eggs per day" });
    expect(svg).toHaveAttribute("viewBox", "0 0 100 32");
    expect(svg.querySelector("polyline")).toHaveAttribute("points", "0,32 50,0 100,16");

    rerender(<Sparkline data={{ points: "0,32 100,32", values: [0, 0], min: 0, max: 0, last: 0 }} label="Flat" />);
    expect(screen.getByRole("img", { name: "Flat" }).querySelector("polyline")).toHaveAttribute("points", "0,32 100,32");
    expect(screen.queryByRole("img", { name: "Eggs per day" })).not.toBeInTheDocument();
  });
});
