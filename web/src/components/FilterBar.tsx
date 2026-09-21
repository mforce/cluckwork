import type { ReactNode } from "react";
import { Paper, Stack, TextField } from "@mui/material";
import type { TextFieldProps } from "@mui/material";

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

export function FilterDateField({ sx, slotProps, ...props }: TextFieldProps) {
  return (
    <TextField
      type="date"
      size="small"
      {...props}
      slotProps={{ ...slotProps, inputLabel: { shrink: true, ...slotProps?.inputLabel } }}
      // Arrays preserve callback and array sx values that object spread drops.
      sx={[{ maxWidth: { md: DATE_FIELD_MAX_WIDTH } }, ...(Array.isArray(sx) ? sx : sx ? [sx] : [])]}
    />
  );
}
