import type { ReactNode } from "react";
import { Paper, Stack, TextField } from "@mui/material";
import type { TextFieldProps } from "@mui/material";

/**
 * #831/#653 — the shared filter row every ledger screen (Sales, Stock,
 * Inventory, History, Expenses, Feed, Water, Reports) and Audit (#833) mount
 * above their table. `variant="outlined"` is load-bearing: #651 D1 measured
 * `--surface-2` against `--canvas` at 1.05:1-1.21:1 in every palette and
 * mode, too close to read as an edge without the hairline border.
 */
export function FilterBar({ children }: { children: ReactNode }) {
  return (
    <Paper
      variant="outlined"
      sx={{ bgcolor: "var(--surface-2)", p: "1rem 1.25rem", mt: "0.5rem", mb: "1.5rem" }}
    >
      <Stack
        direction="row"
        spacing={2}
        useFlexGap
        sx={{
          flexWrap: "wrap",
          alignItems: "flex-end",
          "& > *": { flex: { xs: "1 1 100%", md: "0 0 auto" } },
        }}
      >
        {children}
      </Stack>
    </Paper>
  );
}

const DATE_FIELD_MAX_WIDTH = "12rem";

/**
 * A date control sized for a FilterBar. Bounded at #653's 12rem from `md` up
 * (two ten-character dates do not need the row's full width); the FilterBar
 * itself widens it back to one control per line below that, per D3.3.
 */
export function FilterDateField({ sx, slotProps, ...props }: TextFieldProps) {
  return (
    <TextField
      type="date"
      size="small"
      {...props}
      slotProps={{ ...slotProps, inputLabel: { shrink: true, ...slotProps?.inputLabel } }}
      // An array, not a spread: `sx` may be a callback (a theme function) or
      // an array itself, and `{ ...sx }` on either silently drops it (spreads
      // no own enumerable properties). MUI merges an sx array by applying
      // each entry in order, so the caller's own sx — of any shape — still
      // applies after the bounded-width default.
      sx={[{ maxWidth: { md: DATE_FIELD_MAX_WIDTH } }, ...(Array.isArray(sx) ? sx : sx ? [sx] : [])]}
    />
  );
}
