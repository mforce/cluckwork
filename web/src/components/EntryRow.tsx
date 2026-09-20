import { Box, Typography } from "@mui/material";
import type { ReactNode } from "react";
import { remainderDropProps } from "./GradingChip";
export const STEPPER_SX = {
  "& .numfield": { width: "100%", justifyContent: "space-between" },
  "& .numfield-step": {
    width: { xs: 48, md: 36 }, height: { xs: 48, md: 36 },
    borderRadius: "var(--r-input)",
  },
  "&&& .numfield input": {
    fontSize: { xs: "1.75rem", md: "1.5rem" },
    lineHeight: { xs: "2rem", md: "1.75rem" },
    fontWeight: 500, textAlign: "right",
    width: "calc(5ch + 1.5rem)",
  },
} as const;
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
