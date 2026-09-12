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
// recorded day that produced nothing keeps a stub at the baseline while a day
// nobody recorded draws nothing, so the two no longer render alike.
//
// Each day is a real button rather than a hover-only region: the tooltip is
// the only place a single day's figure appears, and a keyboard user reaching
// the panel must be able to read it.

// The dock is a RESERVED row that is always present, so the box appearing
// reflows nothing. It costs 2.2rem of panel height permanently — the honest
// price of a readout that covers neither the bars it describes nor the Avg and
// Peak figures the reader compares them against. Placed inside the plot it
// covered six neighbouring bars at the peak.
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
  const [active, setActive] = useState<{ slot: DayStripSlot; centerPx: number } | null>(null);
  const dockRef = useRef<HTMLDivElement>(null);
  const tipRef = useRef<HTMLSpanElement>(null);

  // Clamp the box inside the panel against MEASURED widths. It sizes to its own
  // text, which runs roughly 30% longer in tl than in en (#688), so nothing
  // here may assume a width. The arrow is anchored to the slot element instead
  // (.day.on::after), never positioned by index arithmetic: the slots carry a
  // 4px gap plus the 9px week break, so (i + 0.5) / n is not where a slot is.
  useLayoutEffect(() => {
    const dock = dockRef.current;
    const box = tipRef.current;
    if (dock === null || box === null || active === null) return;
    const half = box.offsetWidth / 2;
    const x = half * 2 >= dock.offsetWidth
      ? dock.offsetWidth / 2
      : Math.min(Math.max(active.centerPx, half), dock.offsetWidth - half);
    box.style.left = `${x}px`;
  }, [active]);

  const show = (slot: DayStripSlot) => (e: { currentTarget: HTMLButtonElement }) => {
    const el = e.currentTarget;
    setActive({ slot, centerPx: el.offsetLeft + el.offsetWidth / 2 });
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
      <div className="tipdock" ref={dockRef}>
        {active !== null && (
          <span className="tip" ref={tipRef} role="status">{tip(active.slot)}</span>
        )}
      </div>
      <div
        className="daystrip"
        role="group"
        aria-label={label}
        onMouseLeave={() => setActive(null)}
      >
        {data.slots.map((s) => {
          const on = active !== null && active.slot.date === s.date;
          return (
            <button
              key={s.date}
              type="button"
              className={[on ? "day on" : "day", s.weekBreak ? "day-week" : ""].filter(Boolean).join(" ")}
              aria-label={tip(s)}
              onMouseEnter={show(s)}
              onFocus={show(s)}
              onBlur={() => setActive(null)}
            >
              <Bar slot={s} />
            </button>
          );
        })}
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
    case "unrecorded":
      return null;
    case "recorded":
      return <i style={{ height: `${slot.heightPct}%` }} />;
    default: {
      const _exhaustive: never = slot;
      return _exhaustive;
    }
  }
}
