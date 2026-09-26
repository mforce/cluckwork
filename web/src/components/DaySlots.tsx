// web/src/components/DaySlots.tsx
//
// The marks themselves: one slot per day, the three day states of #780, and
// the average reference line over them. Shared by the Dashboard card and the
// expanded chart (#941) so a day cannot be drawn two ways; the behaviour
// behind them is `useDayStrip`.
import type { DayStripData, DayStripSlot } from "../lib/dashboard";
import type { useDayStrip } from "./useDayStrip";

export function DaySlots({ data, label, tip, variant, strip }: {
  data: DayStripData;
  label: string;
  tip: (slot: DayStripSlot) => string;
  // The card's strip fills its panel; the expanded chart's holds a fixed 22px
  // slot and scrolls. Two names rather than a className prop, so the class
  // manifest (#824 G1) can read both out of the JSX.
  variant: "card" | "expanded";
  strip: ReturnType<typeof useDayStrip>;
}) {
  return (
    <div
      className={variant === "expanded" ? "bigstrip" : "daystrip"}
      role="group"
      aria-label={label}
      ref={strip.stripRef}
      onMouseLeave={strip.handleMouseLeave}
      onKeyDown={strip.handleKeyDown}
    >
      {data.slots.map((s, i) => (
        <button
          key={s.date}
          type="button"
          className={[s.date === strip.activeDate ? "day on" : "day", `day-${s.kind}`,
            s.weekBreak ? "day-week" : ""].filter(Boolean).join(" ")}
          aria-label={tip(s)}
          tabIndex={i === strip.stopIndex ? 0 : -1}
          aria-current={s.date === strip.activeDate}
          onClick={() => strip.select(s, true)}
          onMouseEnter={() => strip.select(s)}
          onFocus={() => strip.select(s, true)}
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
