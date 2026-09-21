import type { ReactNode } from "react";
import { Box, TableContainer, Typography } from "@mui/material";
import { useTranslation } from "react-i18next";

export const CONSOLE_PANEL_SX = {
  border: "1px solid var(--rule)",
  borderRadius: "var(--r-panel)",
  p: 2,
  bgcolor: "var(--surface)",
  minWidth: 0,
};
export const CONSOLE_LINK_SX = {
  minWidth: 0, px: 0, color: "var(--link)", fontWeight: 700,
  textDecoration: "underline", textDecorationColor: "var(--link-rule)", textUnderlineOffset: "3px",
  "&:hover": { textDecoration: "underline" },
};
export const CONSOLE_PAPER_HEAD_SX = {
  display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "15px",
  mx: -2, mt: -2, mb: 2, px: 2, py: "14px", borderBottom: "1px solid var(--rule)",
  "&& h3": { m: 0 },
};

export function ConsoleSubhead({ title, caption }: { title: string; caption: string }) {
  return <Box sx={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 2, mt: "22px", mb: 1, "&& h3": { m: 0 } }}>
    <Typography component="h3" variant="h3">{title}</Typography>
    <Typography component="span" sx={{ color: "text.secondary", fontSize: ".75rem", textAlign: "right" }}>{caption}</Typography>
  </Box>;
}

export const CONSOLE_SPLIT_SX = {
  display: "grid",
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, 1.6fr) minmax(240px, .8fr)" },
  gap: 2,
  my: 2,
};
export const CONSOLE_FORM_SX = {
  display: "grid",
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "repeat(2, minmax(0, 1fr))" },
  gap: 2,
  alignItems: "end",
  alignContent: "start",
  "& > *": { minWidth: 0, width: "100%", maxWidth: "100%" },
};
export const CONSOLE_TICKET_SX = {
  ...CONSOLE_PANEL_SX,
  display: "grid",
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, 1fr) minmax(230px, .55fr)" },
  p: 0,
  my: 2,
};
export const CONSOLE_TICKET_FORM_SX = {
  ...CONSOLE_FORM_SX,
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "repeat(4, minmax(0, 1fr))" },
  gap: 1.5,
};
export const CONSOLE_TICKET_CHECK_SX = {
  p: 2,
  minWidth: 0,
  bgcolor: "var(--surface-2)",
  borderTop: { xs: "1px solid var(--rule)", md: 0 },
  borderLeft: { md: "1px solid var(--rule)" },
};

export function ConsoleSummary({ label, items }: {
  label: string;
  items: { label: string; value: ReactNode }[];
}) {
  return (
    <Box component="dl" aria-label={label} sx={{
      display: { xs: "grid", md: "flex" }, gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
      flexWrap: "wrap", gap: "8px 24px", my: 2, py: 1.25,
      borderTop: "1px solid var(--rule)", borderBottom: "1px solid var(--rule)",
    }}>
      {items.map((item) => <Box key={item.label} sx={{ minWidth: 0, display: "flex", gap: .75, flexWrap: "wrap", alignItems: "baseline" }}>
        <Typography component="dt" variant="body2" sx={{ fontSize: ".7rem", color: "text.secondary" }}>{item.label}</Typography>
        <Typography component="dd" variant="body2" sx={{ m: 0, fontSize: ".8rem", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{item.value}</Typography>
      </Box>)}
    </Box>
  );
}

export const CONSOLE_RAIL_SX = {
  ...CONSOLE_PANEL_SX,
  bgcolor: "#2c2429",
  color: "#fff",
  "& .MuiTypography-root": { color: "inherit" },
};

export function FieldConsole({ children }: { children: ReactNode }) {
  return (
    <Box component="section" sx={{
      minWidth: 0,
      "--link": "var(--brand)",
      "&& h2": { typography: "h2", mb: 1 },
      "&& h3": { typography: "h3", mt: 2.5, mb: 1 },
      "& > p": { fontSize: ".8125rem", lineHeight: 1.45 },
      "& aside p.muted": { fontSize: ".75rem", lineHeight: 1.45 },
      "& aside h3": { mt: 0 },
      "& .MuiTableCell-root": { fontSize: "0.75rem", py: 1.25, px: 1 },
      "& .MuiTableCell-head": {
        fontSize: "0.625rem", letterSpacing: ".06em", textTransform: "uppercase",
      },
    }}>
      {children}
    </Box>
  );
}

export function LedgerTableContainer({ children }: { children: ReactNode }) {
  const { t } = useTranslation("common");
  return (
    <Box sx={{ minWidth: 0, borderTop: "2px solid var(--ink)", borderBottom: "1px solid var(--rule)" }}>
      <Typography component="p" variant="body2" sx={{
        display: { xs: "block", md: "none" },
        m: 0, py: .5, px: 1,
        fontSize: ".65rem",
        textAlign: "right",
        bgcolor: "var(--surface-2)",
        color: "text.secondary",
      }}>
        {t("swipeColumns")}
      </Typography>
      <TableContainer>{children}</TableContainer>
    </Box>
  );
}
