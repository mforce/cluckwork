// web/src/components/StockBar.tsx
import type { StockBarData } from "../lib/dashboard";

// #654 — the stock stacked bar: one span per grade on the `.meter-stack`
// track, widths precomputed (lib/dashboard.ts stockBar). Decorative: the
// legend the screen renders beside it is the text of record, so the track is
// hidden from the accessibility tree like StockPage's `.meter`.
//
// #777 — grade is carried by a categorical hue (`grade-N`, see GRADE_COLOURS),
// not by an opacity ramp that ran out at the sixth grade.
// Only the width-bearing segments: the ledger beside the bar is the text of
// record and names the empty grades this track cannot draw.
export function StockBar({ data }: { data: Pick<StockBarData, "segments"> }) {
  return (
    <div className="meter-stack" aria-hidden="true">
      {data.segments.map((s) => (
        <span key={s.eggGradeId} className={`grade-${s.colorIndex}`} style={{ width: `${s.pct}%` }} />
      ))}
    </div>
  );
}
