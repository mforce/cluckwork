import type { ReactNode } from "react";
import { Box, Paper, Typography } from "@mui/material";
import type { remainderDropProps } from "../../components/GradingChip";

// #830 (owner's screenshot review of #888) — the stepper row's 48px squares
// (mockup: docs/designs/864-visual-language/daily-entry.html) are an sx
// override on NumberField's OWN classes (`.numfield-step`), never an edit to
// NumberField.tsx or its base CSS block (styles.css L1030-1093, #828's):
// those stay exactly as #828 will find them, and this override reaches only
// rows rendered by THIS page. Every part NumberField renders is a FIXED size
// at a given breakpoint — the two step buttons, and the input's own ch-width
// — so `.numfield`'s overall footprint is constant across every row; that
// constancy is what EntryRow's grid below leans on to line the minus/plus
// buttons up without touching NumberField itself.
const STEPPER_SX = {
  "& .numfield": { width: "100%", justifyContent: "space-between" },
  "& .numfield-step": {
    width: { xs: 48, md: 36 }, height: { xs: 48, md: 36 },
    borderRadius: "var(--r-input)",
  },
  "& .numfield input": {
    // The row numeral size (FarmThemeProvider's `h2`/title scale, DIRECTION.md
    // — 24/28 desktop, 28/32 phone), not a bespoke size: the readout is the
    // biggest thing in the row and reads as one more title-weight figure
    // beside the others this screen shows (the sellable value, the grading
    // count), right-aligned and tabular so a column of them lines up by digit.
    fontSize: { xs: "1.75rem", md: "1.5rem" },
    lineHeight: { xs: "2rem", md: "1.75rem" },
    fontWeight: 500, textAlign: "right",
    // Wide enough for a 4-digit count (a flock's daily total can run into the
    // low thousands) with room to spare — measured against "430" clipping to
    // "43" at a tighter "4ch" on desktop (Playwright capture, #830).
    width: { xs: "5.5ch", md: "6ch" },
  },
} as const;

export const captionIdFor = (fieldId: string) => `${fieldId}-caption`;

// #830 (owner's screenshot review of #888) — one ruled GRID row: label (+
// optional caption, e.g. "deactivated") in a flexible truncating column,
// stepper in a fixed-content column, per the mockup's `.row`. The row used to
// be a flex `justify-content: space-between` pair, which reads as aligned
// only until a label overflows: a flex item shrinks by default, so "Total
// eggs" wrapping onto two lines squeezed the stepper beside it by a different
// amount on every row — the owner's screenshot review of #888 caught this as
// each row's minus button sitting at a different x. A grid's second column
// sizes to its own max-content and does NOT shrink to make room for an
// overflowing sibling; pairing that with `minmax(0, 1fr)` + an ellipsis on
// the label (never wrap) is what makes the fix structural rather than a
// pinned width. `groupLabel` names a grade row as an `aria-label`ed group
// (mirrors Dashboard's TodayRow `role="group"` pattern) — the drop target the
// test suite locates by name instead of a class, and `armed` draws the F134
// "taking" outline the same rows carried before, now an inline sx state
// instead of a shared `.taking` class.
export function EntryRow({
  htmlFor, label, caption, groupLabel, armed = false, variant = "standard", dropProps, children,
}: {
  htmlFor: string;
  label: string;
  caption?: string;
  groupLabel?: string;
  armed?: boolean;
  variant?: "standard" | "primary" | "flock-event";
  dropProps?: ReturnType<typeof remainderDropProps>;
  children: ReactNode;
}) {
  return (
    <Box
      role={groupLabel ? "group" : undefined}
      aria-label={groupLabel}
      {...dropProps}
      sx={{
        display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto",
        alignItems: "center",
        gap: 2, minHeight: { xs: 64, md: 44 }, px: 2, py: 1,
        borderBottom: "1px solid var(--rule)",
        ...(variant === "primary" ? {
          minHeight: { xs: 76, md: 68 },
          backgroundColor: "var(--tint-accent)",
          "& .numfield-step": { width: { xs: 48, md: 42 }, height: { xs: 48, md: 42 } },
        } : {}),
        ...(variant === "flock-event" ? {
          mt: 1,
          background: "linear-gradient(90deg, var(--tint-warn), transparent)",
          borderTop: "1px dashed var(--warn)",
        } : {}),
        ...(armed ? {
          outline: "1px dashed var(--stat-accent)", outlineOffset: "4px",
          borderRadius: "var(--r-input)",
        } : {}),
        ...STEPPER_SX,
      }}
    >
      <Box sx={{ minWidth: 0, overflow: "hidden" }}>
        <Box component="label" htmlFor={htmlFor} sx={{ display: "block", cursor: "pointer" }}>
          <Typography component="span" sx={{
            fontWeight: variant === "primary" ? 700 : 500,
            fontSize: variant === "primary" ? "1.125rem" : undefined,
            display: "block",
            overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap",
          }}>
            {label}
          </Typography>
        </Box>
        {caption && (
          <Typography id={captionIdFor(htmlFor)} component="span" variant="caption" className="muted" sx={{
            display: "block", overflow: "hidden", textOverflow: "ellipsis",
            whiteSpace: variant === "standard" ? "nowrap" : "normal",
            lineHeight: 1.25,
          }}
          >
            {caption}
          </Typography>
        )}
      </Box>
      <Box sx={{ display: "flex", alignItems: "center", gap: 1, justifyContent: "flex-end" }}>
        {children}
      </Box>
    </Box>
  );
}

export function WorkbenchPanel({ title, caption, children, summary }: {
  title: string;
  caption: string;
  children: ReactNode;
  summary: ReactNode;
}) {
  return (
    <Paper component="section" variant="outlined" sx={{
      minWidth: 0,
      overflow: "hidden",
      borderRadius: "var(--r-panel)",
      borderColor: "var(--rule-strong)",
      boxShadow: "none",
    }}>
      <Box sx={{
        display: "flex", alignItems: "baseline", justifyContent: "space-between",
        flexWrap: "wrap", gap: 1, px: 2, py: 1.5,
        backgroundColor: "#2c2429", color: "#fff",
      }}>
        <Typography component="h3" sx={{ fontSize: "1.125rem", lineHeight: 1.3, fontWeight: 700 }}>
          {title}
        </Typography>
        <Typography component="span" variant="caption" sx={{ color: "#d8cfd4" }}>
          {caption}
        </Typography>
      </Box>
      {children}
      {summary}
    </Paper>
  );
}

export function SummaryStat({ label, value, live = false }: { label: string; value: string; live?: boolean }) {
  return (
    <Box role={live ? "status" : undefined} sx={{ minWidth: 0 }}>
      <Typography component="span" variant="caption" className="muted" sx={{ display: "block" }}>
        {label}
      </Typography>
      <Typography component="strong" sx={{
        display: "block", fontSize: "1.375rem", lineHeight: 1.3,
        fontWeight: 700, fontVariantNumeric: "tabular-nums",
        // #835/#948 — one of the stat figures; clears the root's text-cut pin.
        fontVariationSettings: "normal",
      }}>
        {value}
      </Typography>
    </Box>
  );
}
