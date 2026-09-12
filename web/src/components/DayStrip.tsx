// web/src/components/DayStrip.tsx
import { useLayoutEffect, useRef, useState } from "react";
import type { DayStripData, DayStripSlot } from "../lib/dashboard";

// #654 → #777 → #780 — the Dashboard's "Last 14 days", one slot per day.
// Geometry arrives computed (lib/dashboard.ts dayStrip) so this stays a pure
// renderer, and every string arrives already formatted in the farm's locale
// (#650): `tip` turns a slot into the sentence shown for it.
//
// A STRIP of days, not a line. The line it replaces drew a zero day on the
// floor of its viewBox, never drew that floor or the top of the scale, and
// interpolated between days a daily count has no values between. Since #780 a
// recorded day that produced nothing keeps a stub at the baseline, a day nobody
// recorded draws nothing, and a day only some houses reported is marked as a
// floor rather than a figure.
//
// One tab stop, not fourteen. The slots are peers, so they behave like a
// toolbar: Tab enters the strip at the selected day (the first, initially) and
// the arrow keys move along it. Fourteen consecutive tab stops on the way past
// a dashboard panel is not keyboard support, it is a keyboard obstacle.
export function DayStrip({ data, label, title, peak, average, tip, from, to }: {
  data: DayStripData;
  label: string;
  title: string;
  peak: string;
  average: string | null;
  tip: (slot: DayStripSlot) => string;
  from: React.ReactNode;
  to: React.ReactNode;
}) {
  // The selected day is held by DATE, never as a snapshot of the slot. Holding
  // the object meant a refetch left the readout showing a figure the same day's
  // own accessible name had already replaced, and an index could point outside
  // a shortened array.
  const [activeDate, setActiveDate] = useState<string | null>(null);
  // Where the keyboard is, which is NOT the same as what is selected: a pointer
  // crossing the strip and leaving clears the selection without moving DOM
  // focus, and deriving the tab stop from the selection then snapped it back to
  // day 1 while focus sat on day 3 — the next ArrowRight moved backwards.
  const [focusDate, setFocusDate] = useState<string | null>(null);
  const dockRef = useRef<HTMLDivElement>(null);
  const tipRef = useRef<HTMLSpanElement>(null);
  const stripRef = useRef<HTMLDivElement>(null);

  const activeIndex = data.slots.findIndex((s) => s.date === activeDate);
  const active = activeIndex === -1 ? null : data.slots[activeIndex];
  // Tab lands where the keyboard last was, or the first day — never on nothing,
  // which is what an all-`-1` strip would give.
  const focusIndex = data.slots.findIndex((s) => s.date === focusDate);
  const stopIndex = focusIndex === -1 ? 0 : focusIndex;

  // Clamp the box inside the panel against MEASURED widths. It sizes to its own
  // text, which runs roughly 30% longer in tl than in en (#688), so nothing
  // here may assume a width. `tip(active)` is in the deps because the farm
  // locale resolves after mount, which changes the text's width without
  // changing which day is selected. The arrow is anchored to the slot element
  // instead (.day.on::after), never positioned by index arithmetic: the slots
  // carry a 4px gap plus the 9px week break, so (i + 0.5) / n is not where a
  // slot is.
  useLayoutEffect(() => {
    const dock = dockRef.current;
    const box = tipRef.current;
    // Measured from the SELECTED slot's own element, never from a remembered
    // number. A stored centre went stale: focus an early day, hover a later
    // one, move the pointer off the strip, and the restore put the box over the
    // hovered day while the arrow and ring stayed on the focused one. The arrow
    // is anchored to this same element, so deriving the box from it is the only
    // way the two cannot disagree.
    const slot = stripRef.current?.children[activeIndex];
    if (dock === null || box === null || !(slot instanceof HTMLElement)) return;
    const half = box.offsetWidth / 2;
    const centre = slot.offsetLeft + slot.offsetWidth / 2;
    const x = half * 2 >= dock.offsetWidth
      ? dock.offsetWidth / 2
      : Math.min(Math.max(centre, half), dock.offsetWidth - half);
    box.style.left = `${x}px`;
  }, [activeIndex, tip]);

  const select = (slot: DayStripSlot, keyboard = false) => {
    setActiveDate(slot.date);
    if (keyboard) setFocusDate(slot.date);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    const step = e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1
      : e.key === "Home" ? -data.slots.length : e.key === "End" ? data.slots.length : 0;
    if (step === 0) return;
    e.preventDefault();
    const next = Math.min(data.slots.length - 1, Math.max(0, stopIndex + step));
    const el = stripRef.current?.children[next];
    if (el instanceof HTMLElement) el.focus();
  };

  return (
    <figure className="trend">
      <figcaption className="trend-scale">
        <span>{title}</span>
        <span className="trend-figures">
          {average !== null && <span className="trend-avg">{average}</span>}
          <span className="trend-peak">{peak}</span>
        </span>
      </figcaption>
      {/* A reserved row, always present, so the box appearing reflows nothing.
          It costs 2.2rem of panel height permanently — the honest price of a
          readout that covers neither the bars it describes nor the Avg and Peak
          figures the reader compares them against. Placed inside the plot it
          covered six neighbouring bars at the peak.

          aria-hidden, because the selected slot's own accessible name is this
          exact sentence. A live region here announced it a second time, and a
          live region mounted together with its text is unreliably announced
          anyway (NamedEntityPicker keeps a permanently mounted one for that
          reason). */}
      <div className="tipdock" ref={dockRef} aria-hidden="true">
        {active !== null && <span className="tip" ref={tipRef}>{tip(active)}</span>}
      </div>
      <div
        className="daystrip"
        role="group"
        aria-label={label}
        ref={stripRef}
        // The pointer leaving hands the readout back to the KEYBOARD, if the
        // keyboard still has it. Clearing unconditionally took the readout and
        // the ring off a day that still held focus, with no blur to explain it
        // — a pointer wandering across the panel silently undid the selection a
        // keyboard user had made.
        onMouseLeave={() => setActiveDate(
          stripRef.current?.contains(document.activeElement) === true ? focusDate : null,
        )}
        onKeyDown={onKeyDown}
      >
        {data.slots.map((s, i) => (
          <button
            key={s.date}
            type="button"
            className={[s.date === activeDate ? "day on" : "day", `day-${s.kind}`,
              s.weekBreak ? "day-week" : ""].filter(Boolean).join(" ")}
            aria-label={tip(s)}
            tabIndex={i === stopIndex ? 0 : -1}
            aria-current={s.date === activeDate}
            onClick={() => select(s, true)}
            onMouseEnter={() => select(s)}
            onFocus={() => select(s, true)}
          >
            <Bar slot={s} />
          </button>
        ))}
        {/* Last, so it reads ACROSS the bars rather than behind them — a
            reference the eye cannot follow over the tall days is not one. */}
        {data.averagePct !== null && (
          <span className="avgline" style={{ bottom: `${data.averagePct}%` }} aria-hidden="true" />
        )}
      </div>
      <div className="trend-rule">
        <span>{from}</span>
        <span>{to}</span>
      </div>
    </figure>
  );
}

// Narrowing on `kind` is what makes an unrecorded day undrawable: there is no
// height to read on that arm. A fourth state fails to compile here rather than
// silently rendering a bar.
function Bar({ slot }: { slot: DayStripSlot }) {
  switch (slot.kind) {
    case "none":
    case "unrecorded":
      return null;
    case "partial":
    case "recorded":
      return <i style={{ height: `${slot.heightPct}%` }} />;
    default: {
      const _exhaustive: never = slot;
      return _exhaustive;
    }
  }
}
