import { Link as MuiLink } from "@mui/material";
import { Link as RouterLink } from "react-router";
import { useTranslation } from "react-i18next";
import { glossaryEntry } from "../routes/helpGlossary";
import type { GlossaryKey } from "../routes/helpGlossary";

// #657 — a small "?" beside a column header or label that carries a glossary
// term, linking to the term's own anchor on the Help page. The accessible
// name says which term, so a screen reader hears "What does Withdrawal
// restriction mean?" rather than a bare question mark.
//
// D2 pair 20: a small ring badge, not underlined text — the hover highlight
// is the ring's edge (--stat-accent), never a link underline.
export function GlossaryLink({ term }: { term: GlossaryKey }) {
  const { t } = useTranslation(["common", "help"]);
  const entry = glossaryEntry(term);
  const label = t("common:whatDoesTermMean", { term: t(`help:${entry.termKey}`) });
  return (
    <MuiLink
      component={RouterLink}
      to={`/help#${entry.id}`}
      aria-label={label}
      underline="none"
      sx={{
        display: "inline-flex", alignItems: "center", justifyContent: "center",
        width: "1.15rem", height: "1.15rem", ml: "0.35rem", border: "1px solid", borderColor: "divider",
        borderRadius: "var(--r-pill)", fontSize: "0.7rem", fontWeight: 700, lineHeight: 1,
        color: "text.secondary", verticalAlign: "middle",
        "&:hover": { color: "text.primary", borderColor: "info.main" },
      }}
    >
      ?
    </MuiLink>
  );
}
