import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { glossaryEntry } from "../routes/helpGlossary";
import type { GlossaryKey } from "../routes/helpGlossary";

// #657 — a small "?" beside a column header or label that carries a glossary
// term, linking to the term's own anchor on the Help page. The accessible
// name says which term, so a screen reader hears "What does Withdrawal
// restriction mean?" rather than a bare question mark.
export function GlossaryLink({ term }: { term: GlossaryKey }) {
  const { t } = useTranslation(["common", "help"]);
  const entry = glossaryEntry(term);
  const label = t("common:whatDoesTermMean", { term: t(`help:${entry.termKey}`) });
  return (
    <Link className="help-link" to={`/help#${entry.id}`} aria-label={label} title={label}>?</Link>
  );
}
