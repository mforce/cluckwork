import type { ReactNode } from "react";
import { Box, ButtonBase } from "@mui/material";

const PHONE_LIST_SX = {
  m: 0, p: 0, listStyle: "none",
  bgcolor: "var(--surface)",
  border: "1px solid var(--rule)",
  borderTop: "2px solid var(--ink)",
  borderRadius: "0 0 var(--r-panel) var(--r-panel)",
  "& > li": { borderBottom: "1px solid var(--rule)" },
  "& > li:last-of-type": { borderBottom: 0 },
};

const PHONE_ROW_SX = {
  display: "block", width: "100%", minHeight: 44, textAlign: "left", px: 1.5, py: 1,
  "&:focus-visible": { outlineOffset: "-2px" },
};

const DETAILS_FIELD_SX = {
  display: "grid", gridTemplateColumns: "96px minmax(0, 1fr)", gap: 1,
  py: "9px", fontSize: ".8125rem", borderBottom: "1px solid var(--rule)",
  "&:last-of-type": { borderBottom: 0 },
  "& dt": { color: "var(--muted)" },
  "& dd": { m: 0, fontWeight: 650, overflowWrap: "anywhere" },
};

export function PhoneLedgerList({ label, children }: { label: string; children: ReactNode }) {
  return <Box component="ul" aria-label={label} sx={PHONE_LIST_SX}>{children}</Box>;
}

export function PhoneLedgerRow({ onClick, date, primary, trailing, summary, muted = false }: {
  onClick: () => void;
  date: ReactNode;
  primary: ReactNode;
  trailing: ReactNode;
  summary: ReactNode;
  muted?: boolean;
}) {
  return <ButtonBase aria-haspopup="dialog" onClick={onClick}
    sx={{ ...PHONE_ROW_SX, color: muted ? "var(--muted)" : "inherit" }}>
    <Box sx={{ display: "flex", alignItems: "center", gap: 1, fontSize: ".8125rem", lineHeight: 1.35, whiteSpace: "nowrap" }}>
      <Box component="span" sx={{ fontWeight: 700, flexShrink: 0 }}>{date}</Box>
      <Box component="span" sx={{ flex: "1 1 auto", minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>{primary}</Box>
      <Box component="span" sx={{ flexShrink: 0, fontSize: ".6875rem" }}>{trailing}</Box>
    </Box>
    <Box sx={{
      mt: "2px", fontSize: ".6875rem", lineHeight: 1.35, color: "var(--muted)",
      whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis",
      "& strong": { fontWeight: 650, color: muted ? "inherit" : "var(--ink)" },
    }}>{summary}</Box>
  </ButtonBase>;
}

export function PhoneDetailsField({ label, children }: { label: string; children: ReactNode }) {
  return <Box sx={DETAILS_FIELD_SX}><dt>{label}</dt><dd>{children}</dd></Box>;
}
