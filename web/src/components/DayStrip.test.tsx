// web/src/components/DayStrip.test.tsx
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { DayStrip } from "./DayStrip";
import type { DayStripData } from "../lib/dashboard";

const slot = (date: string, value: number, heightPct: number, weekBreak = false) =>
  ({ date, value, heightPct, recorded: value > 0, weekBreak });

const data: DayStripData = {
  slots: [slot("2026-07-01", 10, 100), slot("2026-07-02", 0, 0), slot("2026-07-03", 5, 50, true)],
  min: 0, max: 10, last: 5,
};

describe("DayStrip (#654, #777)", () => {
  it("draws a slot for every day and a bar only for a day with a figure", () => {
    const { container } = render(
      <DayStrip data={data} label="Eggs per day" title="Eggs per day" peak="Peak 10" from="1 Jul" to="3 Jul" />,
    );
    // Three days in the window means three slots, including the one with nothing
    // recorded — that empty slot is the whole point of the strip.
    expect(container.querySelectorAll(".daystrip > .day")).toHaveLength(3);
    const bars = Array.from(container.querySelectorAll(".day > i")).map((b) => (b as HTMLElement).style.height);
    expect(bars).toEqual(["100%", "50%"]);
  });

  it("marks the week boundary on exactly the slot that carries it", () => {
    const { container } = render(
      <DayStrip data={data} label="Eggs per day" title="Eggs per day" peak="Peak 10" from="1 Jul" to="3 Jul" />,
    );
    const marked = Array.from(container.querySelectorAll(".daystrip > .day"))
      .map((d) => d.classList.contains("day-week"));
    expect(marked).toEqual([false, false, true]);
  });

  it("names the picture, the scale and both ends of the window, and follows a re-render", () => {
    const { rerender, container } = render(
      <DayStrip data={data} label="Eggs per day" title="Eggs per day" peak="Peak 10" from="1 Jul" to="3 Jul" />,
    );
    expect(screen.getByRole("img", { name: "Eggs per day" })).toBeInTheDocument();
    expect(screen.getByText("Peak 10")).toBeInTheDocument();
    expect(screen.getByText("1 Jul")).toBeInTheDocument();
    expect(screen.getByText("3 Jul")).toBeInTheDocument();

    rerender(
      <DayStrip
        data={{ slots: [slot("2026-08-01", 0, 0)], min: 0, max: 0, last: 0 }}
        label="Flat" title="Eggs per day" peak="Peak 0" from="1 Aug" to="1 Aug"
      />,
    );
    expect(screen.getByRole("img", { name: "Flat" })).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: "Eggs per day" })).not.toBeInTheDocument();
    expect(container.querySelectorAll(".day > i")).toHaveLength(0);
  });
});
