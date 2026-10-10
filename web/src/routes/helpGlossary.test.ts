import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { GLOSSARY, GLOSSARY_GROUPS, glossaryEntry } from "./helpGlossary";
import { en } from "../i18n/en";
import { es } from "../i18n/es";
import { tl } from "../i18n/tl";

// #657 — the in-app glossary is data, not JSX: one entry per term, grouped,
// each naming the specs/product/GLOSSARY.md term it is the curated subset of.
// These guards walk everything and exclude nothing: a catalog row with no
// entry, an entry with no catalog row, a group nobody uses, a spec term that
// was renamed — each goes red here rather than shipping as a silent gap.

type Catalog = { help: Record<string, unknown> };
const packs: [string, Catalog][] = [["en", en as Catalog], ["es", es as Catalog], ["tl", tl as Catalog]];

// GLOSSARY.md's terms are the bold run that opens a paragraph, sometimes an
// h3. A parenthetical is commentary — "(#531)", "(spec §4.5)", "(egg unit
// conversion)" — and is not part of the term.
const normalise = (term: string) => term.replace(/\s*\([^)]*\)/g, "").trim().toLowerCase();
const specTerms = new Set(
  [...readFileSync(resolve(process.cwd(), "../specs/product/GLOSSARY.md"), "utf8").matchAll(/^(?:\*\*([^*]+)\*\*|### (.+))/gm)]
    .map((m) => normalise(m[1] ?? m[2])),
);

// #1186 — every GLOSSARY.md term either has an in-app entry above or a row
// here saying why it does not. A term is the bold run that opens a paragraph,
// or an h3. NOT_YET rows are the terms that had no entry when this check
// landed; give one an entry, then delete its row. Never add a NOT_YET row for
// a new term.
const NOT_YET = "Not yet in the in-app glossary when #1186 landed.";
const INTERNAL = "An internal mechanism; no screen names it.";
const OPERATOR = "A run-then-exit operator command, not part of the app.";
const SPEC_ONLY: Record<string, string> = {
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
  "money": "How amounts are stored (minor units); screens show formatted money, never the term.",
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
  "break-glass reset": OPERATOR,
  "export": NOT_YET,
  "dialog": NOT_YET,
  "confirmation": NOT_YET,
  "void reason": NOT_YET,
};
const specLines = readFileSync(resolve(process.cwd(), "../specs/product/GLOSSARY.md"), "utf8").split("\n");
const paragraphTerms = specLines.flatMap((line, i) => {
  const m = /^\*\*([^*]+)\*\*|^### (.+)/.exec(line);
  return m && (m[2] !== undefined || i === 0 || specLines[i - 1].trim() === "") ? [normalise(m[1] ?? m[2])] : [];
});

describe("help glossary data", () => {
  it("carries every glossary term the en catalog has, and nothing else", () => {
    const catalogTerms = Object.keys(en.help).filter((k) => /^glossary[A-Z]\w*Term$/.test(k)).sort();
    const dataTerms = GLOSSARY.map((e) => `glossary${e.key}Term`).sort();
    expect(dataTerms).toEqual(catalogTerms);
  });

  it("gives every entry a unique key and a stable kebab-case anchor id", () => {
    const ids = GLOSSARY.map((e) => e.id);
    expect(new Set(ids).size).toBe(GLOSSARY.length);
    for (const e of GLOSSARY) {
      expect(e.id).toMatch(/^glossary-[a-z0-9]+(-[a-z0-9]+)*$/);
    }
    expect(glossaryEntry("EggLot").id).toBe("glossary-egg-lot");
    expect(glossaryEntry("UiLanguage").id).toBe("glossary-ui-language");
  });

  it.each(packs)("%s carries a term and a definition for every entry and a label for every group", (_name, pack) => {
    for (const e of GLOSSARY) {
      expect(typeof pack.help[`glossary${e.key}Term`], `glossary${e.key}Term`).toBe("string");
      expect(typeof pack.help[`glossary${e.key}Def`], `glossary${e.key}Def`).toBe("string");
    }
    for (const group of GLOSSARY_GROUPS) {
      expect(typeof pack.help[group.labelKey], group.labelKey).toBe("string");
    }
  });

  it("uses every group at least once, and every entry names a declared group", () => {
    const declared = new Set(GLOSSARY_GROUPS.map((group) => group.key));
    for (const entry of GLOSSARY) expect(declared.has(entry.group), entry.key).toBe(true);
    for (const group of GLOSSARY_GROUPS) {
      expect(GLOSSARY.some((entry) => entry.group === group.key), group.key).toBe(true);
    }
  });

  it("marks every definition that carries <strong> as rich, so <Trans> renders the tag", () => {
    for (const e of GLOSSARY) {
      const def = en.help[`glossary${e.key}Def`] as string;
      if (/<strong>/.test(def)) expect(e.rich, `${e.key} carries <strong> but is not rich`).toBe(true);
      // A rich entry with no tag at all is a <Trans> nobody needs; FarmCode's
      // "<code>" is literal URL text, which is why the check is for any tag.
      if (e.rich) expect(/</.test(def), `${e.key} is rich but carries no tag`).toBe(true);
    }
  });

  it("has an in-app entry, or a SPEC_ONLY row with a reason, for every GLOSSARY.md term", () => {
    expect(paragraphTerms.length).toBeGreaterThan(100);
    const inApp = new Set(GLOSSARY.map((e) => normalise(e.spec)));
    const missing = paragraphTerms.filter((term) => !inApp.has(term) && !(term in SPEC_ONLY));
    expect(missing, "add an in-app glossary entry (helpGlossary.ts + en/es/tl), or a SPEC_ONLY row with a reason").toEqual([]);
  });

  it("keeps SPEC_ONLY to current GLOSSARY.md terms that have no in-app entry", () => {
    const inApp = new Set(GLOSSARY.map((e) => normalise(e.spec)));
    for (const [term, reason] of Object.entries(SPEC_ONLY)) {
      expect(paragraphTerms.includes(term), `SPEC_ONLY "${term}" is no longer a GLOSSARY.md term`).toBe(true);
      expect(inApp.has(term), `SPEC_ONLY "${term}" now has an in-app entry; delete the row`).toBe(false);
      expect(reason.trim(), term).not.toBe("");
    }
  });

  it("only ever loses NOT_YET rows", () => {
    // Lower this when a NOT_YET term gets its entry. A new term needs an entry
    // or a real reason, never NOT_YET.
    expect(Object.values(SPEC_ONLY).filter((reason) => reason === NOT_YET)).toHaveLength(45);
  });

  it("names, for every entry, a term that exists in specs/product/GLOSSARY.md", () => {
    expect(specTerms.size).toBeGreaterThan(50);
    for (const e of GLOSSARY) {
      expect(specTerms.has(normalise(e.spec)), `${e.key} → "${e.spec}"`).toBe(true);
    }
  });
});
