// web/src/components/DayStrip.test.tsx
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DayStrip } from "./DayStrip";
import type { DayStripData, DayStripSlot } from "../lib/dashboard";

const rec = (date: string, eggs: number, heightPct: number, weekBreak = false): DayStripSlot =>
  ({ kind: "recorded", date, eggs, heightPct, weekBreak });
const gap = (date: string, weekBreak = false): DayStripSlot => ({ kind: "unrecorded", date, weekBreak });

// Three days: one that laid, one that laid nothing and was recorded, one that
// nobody recorded. The middle and the last are the pair #780 exists to split.
const data: DayStripData = {
  slots: [rec("2026-07-01", 10, 100), rec("2026-07-02", 0, 2), gap("2026-07-03", true)],
  min: 0, max: 10, average: 5, averagePct: 50, last: null, recorded: 2, unrecorded: 1,
};

const tip = (s: DayStripSlot) =>
  s.kind === "recorded" ? `${s.date} – ${s.eggs} eggs` : `${s.date} – no entry`;

const strip = (d: DayStripData = data, label = "Eggs per day") => (
  <DayStrip
    data={d} label={label} title="Eggs per day" peak="Peak 10" average="Avg 5"
    tip={tip} from="1 Jul" to="3 Jul"
  />
);

describe("DayStrip (#654, #777, #780)", () => {
  it("draws a slot for every day, and a bar only for a day somebody recorded", () => {
    const { container } = render(strip());
    expect(container.querySelectorAll(".daystrip > .day")).toHaveLength(3);
    const bars = Array.from(container.querySelectorAll(".day > i")).map((b) => (b as HTMLElement).style.height);
    // The recorded zero keeps its 2% stub; the unrecorded day has no bar at all.
    expect(bars).toEqual(["100%", "2%"]);
  });

  it("places the average line at its share of the peak, and omits it when there is none", () => {
    const { container } = render(strip());
    expect((container.querySelector(".avgline") as HTMLElement).style.bottom).toBe("50%");
    const { container: none } = render(strip({ ...data, average: null, averagePct: null }));
    expect(none.querySelector(".avgline")).toBeNull();
  });

  it("marks the week boundary on exactly the slot that carries it", () => {
    const { container } = render(strip());
    const marked = Array.from(container.querySelectorAll(".daystrip > .day"))
      .map((d) => d.classList.contains("day-week"));
    expect(marked).toEqual([false, false, true]);
  });

  it("names the group, the scale and both ends of the window, and follows a re-render", () => {
    const { rerender } = render(strip());
    expect(screen.getByRole("group", { name: "Eggs per day" })).toBeInTheDocument();
    expect(screen.getByText("Peak 10")).toBeInTheDocument();
    expect(screen.getByText("Avg 5")).toBeInTheDocument();
    expect(screen.getByText("1 Jul")).toBeInTheDocument();

    rerender(strip({ slots: [gap("2026-08-01")], min: null, max: null, average: null, averagePct: null, last: null, recorded: 0, unrecorded: 1 }, "Flat"));
    expect(screen.getByRole("group", { name: "Flat" })).toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "Eggs per day" })).not.toBeInTheDocument();
  });

  // Every day carries its own sentence as the slot's accessible name, so the
  // readout is not pointer-only — the figure appears nowhere else on the panel.
  it("gives every slot its own name, saying 'no entry' rather than a count of zero", () => {
    render(strip());
    expect(screen.getByRole("button", { name: "2026-07-01 – 10 eggs" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "2026-07-02 – 0 eggs" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "2026-07-03 – no entry" })).toBeInTheDocument();
  });

  it("shows that day's readout on hover and marks the slot it describes", async () => {
    const user = userEvent.setup();
    const { container } = render(strip());
    expect(container.querySelector(".tip")).toBeNull();

    const days = Array.from(container.querySelectorAll(".daystrip > .day"));
    await user.hover(days[2]);
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-03 – no entry");
    // The SLOT is marked, not just the bar — the day it names has no bar.
    expect(days.map((d) => d.classList.contains("on"))).toEqual([false, false, true]);

    await user.hover(days[0]);
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-01 – 10 eggs");
    expect(days.map((d) => d.classList.contains("on"))).toEqual([true, false, false]);
  });

  it("opens the readout from the keyboard and closes it on blur", async () => {
    const user = userEvent.setup();
    const { container } = render(strip());
    await user.tab();
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-01 – 10 eggs");
    await user.tab();
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-02 – 0 eggs");
  });

  // The dock is reserved whether or not a day is selected, so the box appearing
  // reflows nothing below it.
  it("keeps the readout's row present with nothing selected", () => {
    const { container } = render(strip());
    expect(container.querySelector(".tipdock")).toBeInTheDocument();
  });
});
