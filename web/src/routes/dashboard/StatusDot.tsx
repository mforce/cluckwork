import { Box, Typography } from "@mui/material";

// A hollow dot marks states without a semantic colour.
const STATUS_DOT_COLOR: Record<string, string> = {
  active: "var(--success)", submitted: "var(--success)", confirmed: "var(--success)",
  saleable: "var(--success)", paid: "var(--success)",
  locked: "var(--stat-accent)",
  manageradjusted: "var(--warn)", adjusted: "var(--warn)", partial: "var(--warn)",
  voided: "var(--error)", cancelled: "var(--error)", inactive: "var(--error)", denied: "var(--error)",
};

export function StatusDot({ status, label, forceColor }: { status?: string; label?: string; forceColor?: string }) {
  const color = forceColor ?? (status ? STATUS_DOT_COLOR[status.toLowerCase()] : undefined);
  return (
    <Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: 0.75 }}>
      <Box component="span" aria-hidden sx={color
        ? { width: 8, height: 8, borderRadius: "50%", bgcolor: color, flexShrink: 0 }
        : { width: 8, height: 8, borderRadius: "50%", border: "1.5px solid var(--muted)", boxSizing: "border-box", flexShrink: 0 }}
      />
      <Typography component="span" variant="body1">{label ?? status}</Typography>
    </Box>
  );
}
