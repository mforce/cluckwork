// #1186 — the coverage contract between specs/product/GLOSSARY.md and the
// in-app glossary in helpGlossary.ts. helpGlossary.test.ts enforces it, and
// the Docs impact CI check (.github/scripts/docs-impact.mjs) reads this file
// at a PR's base and head to refuse a newly deferred term. It has no imports,
// so Node can load any revision of it as it stands.

// A term is a bold run that opens a paragraph (after a blank line or a
// heading) or an h3. A parenthetical is commentary — "(#531)", "(spec §4.5)" —
// and not part of the term.
export const normalise = (term: string) => term.replace(/\s*\([^)]*\)/g, "").trim().toLowerCase();
export function glossaryTerms(markdown: string): string[] {
  const lines = markdown.split("\n");
  return lines.flatMap((line, i) => {
    const m = /^\*\*([^*]+)\*\*|^### (.+)/.exec(line);
    const opensParagraph = i === 0 || lines[i - 1].trim() === "" || lines[i - 1].startsWith("#");
    return m && (m[2] !== undefined || opensParagraph) ? [normalise(m[1] ?? m[2])] : [];
  });
}

// Every term either has an in-app entry or a row here saying why it does not.
// NOT_YET rows are the user-facing terms that had no entry when this check
// landed. Give one an entry and delete its row; never defer a new term (the
// Docs impact check compares these rows with the PR's base).
export const NOT_YET = "Not yet in the in-app glossary when #1186 landed.";
const INTERNAL = "An internal mechanism; no screen names it.";
const OPERATOR = "A run-then-exit operator command, not part of the app.";
export const SPEC_ONLY: Record<string, string> = {
  "flock": NOT_YET,
  "placement date": NOT_YET,
  "initial count": NOT_YET,
  "current birds": NOT_YET,
  "default flock": NOT_YET,
  "spent hen": NOT_YET,
  "daily entry steps": NOT_YET,
  "condition grade": NOT_YET,
  "condition": NOT_YET,
  "left to grade": NOT_YET,
  "entry footer": NOT_YET,
  "steppers": NOT_YET,
  "put all in…": NOT_YET,
  "editing draft": NOT_YET,
  "sellable cap": NOT_YET,
  "grade reconciliation": NOT_YET,
  "saleable": NOT_YET,
  "deactivated grade": NOT_YET,
  "customer": NOT_YET,
  "money": "The entry is about storage (minor units, the currency snapshot). Help's export section covers the part users meet.",
  "audit log": NOT_YET,
  "every event names an actor, and nothing can write one that does not.": "A rule about the audit log, not a term.",
  "record history": NOT_YET,
  "entity-scoped audit history": NOT_YET,
  "record-type filter": NOT_YET,
  "clearing filters": NOT_YET,
  "hen-day %": NOT_YET,
  "partly recorded day": NOT_YET,
  "production report": NOT_YET,
  "profit, basic": NOT_YET,
  "payment": NOT_YET,
  "expense category": NOT_YET,
  "expenses period filter, and what \"clear\" means there": NOT_YET,
  "account": NOT_YET,
  "account status — active / suspended": NOT_YET,
  "farm / house": "Ids only; no screen manages farms or houses yet.",
  "farm locale": NOT_YET,
  "idempotency key": INTERNAL,
  "account lockout": NOT_YET,
  "superseded-flight cookie revocation": INTERNAL,
  "credential epoch": INTERNAL,
  "version": "The concurrency token; users see a conflict message, never the term.",
  "users screen": NOT_YET,
  "password change": NOT_YET,
  "verified domain": NOT_YET,
  "disconnecting an app": NOT_YET,
  "allow connected apps": NOT_YET,
  "first-run admin provisioning": OPERATOR,
  "must-change-password gate": NOT_YET,
  "break-glass reset": NOT_YET,
  "export": NOT_YET,
  "dialog": NOT_YET,
  "confirmation": NOT_YET,
  "void reason": NOT_YET,
};
