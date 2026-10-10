// Self-tests for the docs-impact check (#1186). Run with
// `node --test .github/scripts/docs-impact.test.mjs`.

import test from "node:test";
import assert from "node:assert/strict";

import { ESCAPE, deferralFindings, driftFindings, newStringFindings, termSections } from "./docs-impact.mjs";

const catalog = (entries) => new Map(Object.entries(entries));
const base = catalog({ "expenses.dateHeader": "Date", "help.expenses": "Expenses are…" });

test("a new user-facing string with no Help or glossary change is flagged, naming the key", () => {
  const headEn = catalog({ ...Object.fromEntries(base), "expenses.showAllTime": "Show all time" });
  const [finding] = newStringFindings({ baseEn: base, headEn, glossaryChanged: false });
  assert.match(finding, /expenses\.showAllTime/);
});

test("a changed help.* key, or a GLOSSARY.md change, answers it", () => {
  const headEn = catalog({ ...Object.fromEntries(base), "expenses.showAllTime": "Show all time" });
  const helped = new Map(headEn).set("help.expenses", "Expenses are… Show all time widens it.");
  assert.deepEqual(newStringFindings({ baseEn: base, headEn: helped, glossaryChanged: false }), []);
  assert.deepEqual(newStringFindings({ baseEn: base, headEn, glossaryChanged: true }), []);
});

test("a new help.* key alone, an edited string, or a removed one is not a new string", () => {
  const cases = [
    catalog({ ...Object.fromEntries(base), "help.more": "More help." }),
    catalog({ "expenses.dateHeader": "Day", "help.expenses": "Expenses are…" }),
    catalog({ "help.expenses": "Expenses are…" }),
  ];
  for (const headEn of cases) assert.deepEqual(newStringFindings({ baseEn: base, headEn, glossaryChanged: false }), []);
});

test("the escape needs the exact label, 'none' and a reason", () => {
  assert.ok(ESCAPE.test("Docs-impact: none — a label rename, no new concept"));
  assert.ok(ESCAPE.test("Docs-impact: none - test-only strings"));
  assert.ok(!ESCAPE.test("Docs-impact: none —"));
  assert.ok(!ESCAPE.test("Docs-impact: some — updated later"));
  assert.ok(!ESCAPE.test("> Docs-impact: none — quoted from another PR"));
});

const spec = [
  "# Glossary",
  "",
  "**Farm code (account slug, #531)** — the short code a farm signs in with.",
  "",
  "**Flock** — a group of birds.",
  "",
].join("\n");
const entries = [{ key: "FarmCode", spec: "Farm code" }];
const en = catalog({ "help.glossaryFarmCodeDef": "The code you type at sign-in." });

test("a term's text runs to the next term, so a paragraph added under it belongs to it", () => {
  const sections = termSections(spec.replace("**Flock**", "It can be prefilled from a link.\n\n**Flock**"));
  assert.match(sections.get("farm code"), /prefilled from a link/);
  assert.doesNotMatch(sections.get("flock"), /prefilled/);
});

test("#588's shape: a term's GLOSSARY.md text changes and its in-app definition does not", () => {
  const headSpec = spec.replace("signs in with.", "signs in with.\n\nA `?farm=` link prefills it.");
  const [finding] = driftFindings({ baseEn: en, headEn: en, baseSpec: spec, headSpec, entries });
  assert.match(finding, /"farm code".*help\.glossaryFarmCodeDef/);
  const updated = catalog({ "help.glossaryFarmCodeDef": "The code you type, or a link prefills it." });
  assert.deepEqual(driftFindings({ baseEn: en, headEn: updated, baseSpec: spec, headSpec, entries }), []);
});

test("a change to a term with no in-app entry, or a brand-new term, is not drift", () => {
  const other = spec.replace("a group of birds.", "a group of birds managed as one unit.");
  const added = `${spec}\n**Tray deposit** — a refundable charge.\n`;
  for (const headSpec of [other, added]) {
    assert.deepEqual(driftFindings({ baseEn: en, headEn: en, baseSpec: spec, headSpec, entries }), []);
  }
});

test("a bold term straight after a heading opens a section too", () => {
  const sections = termSections("## Packaging\n**Tray deposit** — a refundable charge.\n");
  assert.ok(sections.has("tray deposit"));
});

const NOT_YET = "Not yet in the in-app glossary when #1186 landed.";
const coverage = (rows) => ({ NOT_YET, SPEC_ONLY: rows });
const rest = { customer: NOT_YET, "credential epoch": "An internal mechanism." };
const baseRows = { flock: NOT_YET, ...rest };

test("a term deferred at head but not at the base is refused, even when the count stays the same", () => {
  const swapped = coverage({ ...rest, "tray deposit": NOT_YET });
  const [finding] = deferralFindings({ base: coverage(baseRows), head: swapped });
  assert.match(finding, /"tray deposit" is newly deferred/);
});

test("removing a NOT_YET row, or giving a new term a real reason, is fine; so is a base without the file", () => {
  const reasoned = coverage({ ...rest, "tray deposit": "Billing-side only; no screen shows it." });
  assert.deepEqual(deferralFindings({ base: coverage(baseRows), head: coverage(rest) }), []);
  assert.deepEqual(deferralFindings({ base: coverage(baseRows), head: reasoned }), []);
  assert.deepEqual(deferralFindings({ base: null, head: coverage(baseRows) }), []);
});
