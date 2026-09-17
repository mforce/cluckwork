import { Box, Typography } from "@mui/material";
import type { ReactNode } from "react";
import { remainderDropProps } from "./GradingChip";

// #830/#831 — shared between Daily entry's capture form and History's adjust
// dialog: both render the identical two-step egg-counts/grading layout (the
// dialog IS that form, per History's own copy), so the alignment fix below
// has to live in one place or drift the moment either screen's rows change.
//
// #830 (owner's screenshot review of #888) — the stepper row's 48px squares
// (mockup: docs/designs/864-visual-language/daily-entry.html) are an sx
// override on NumberField's OWN classes (`.numfield-step`), never an edit to
// NumberField.tsx or its base CSS block (styles.css, #828's): those stay
// exactly as #828 left them, and this override reaches only rows rendered
// through EntryRow. Every part NumberField renders is a FIXED size at a given
// breakpoint — the two step buttons, and the input's own ch-width — so
// `.numfield`'s overall footprint is constant across every row; that
// constancy is what the grid below leans on to line the minus/plus buttons
// up without touching NumberField itself.
export const STEPPER_SX = {
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
  htmlFor, label, caption, groupLabel, armed = false, dropProps, children,
}: {
  htmlFor: string;
  label: string;
  caption?: string;
  groupLabel?: string;
  armed?: boolean;
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
        gap: 2, minHeight: { xs: 52, md: 44 }, py: 1,
        borderBottom: "1px solid var(--rule)",
        ...(armed ? {
          outline: "1px dashed var(--stat-accent)", outlineOffset: "4px",
          borderRadius: "var(--r-input)",
        } : {}),
        ...STEPPER_SX,
      }}
    >
      <Box component="label" htmlFor={htmlFor}
        sx={{ minWidth: 0, overflow: "hidden", cursor: "pointer" }}
      >
        <Typography component="span" sx={{
          fontWeight: 500, display: "block",
          overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap",
        }}
        >{label}</Typography>
        {caption && (
          <Typography component="span" variant="caption" className="muted" sx={{
            display: "block", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap",
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
