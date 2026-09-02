// web/src/components/Sparkline.tsx
import type { SparklineData } from "../lib/dashboard";

// #654 — an inline SVG line, no chart library. Geometry arrives computed
// (lib/dashboard.ts sparkline) so this stays a pure renderer; the caption the
// screen puts beside it carries the figures as text, and `label` names the
// picture for a screen reader (role="img").
export function Sparkline({ data, label }: { data: SparklineData; label: string }) {
  return (
    <svg className="sparkline" role="img" aria-label={label} viewBox="0 0 100 32" preserveAspectRatio="none">
      <polyline points={data.points} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}
