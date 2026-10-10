// Self-tests for the docs-impact check (#1186). Run with
// `node --test .github/scripts/docs-impact.test.mjs`.

import test from "node:test";
import assert from "node:assert/strict";

import { ESCAPE, deferralFindings, driftFindings, newStringFindings, termSections, waiver } from "./docs-impact.mjs";

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
  assert.ok(!ESCAPE.test("Docs-impact: none --"));
  assert.ok(!ESCAPE.test("Docs-impact: none ---"));
  assert.ok(ESCAPE.test("Docs-impact: none -- test-only strings"));
});

test("a waiver inside a code fence or an HTML comment is not a waiver; a visible one is", () => {
  const hidden = [
    "Example:\n```text\nDocs-impact: none — example from another PR\n```\n",
    "~~~\nDocs-impact: none — tilde fence\n~~~",
    "<!--\nDocs-impact: none — old template reason\n-->",
    "<!-- never closed\nDocs-impact: none — still hidden",
    "```\nan unclosed fence hides the rest\nDocs-impact: none — hidden",
  ];
  for (const body of hidden) assert.equal(waiver(body), null, body);
  const nested = [
    "```markdown\n```text\nDocs-impact: none — example from another PR\n```\n```\n",
    "~~~markdown\n~~~text\nDocs-impact: none — example from another PR\n~~~\n~~~\n",
    "````\n```\nDocs-impact: none — a shorter fence does not close it\n````\n",
    "```\n~~~\nDocs-impact: none — the other character does not close it\n```\n",
  ];
  for (const body of nested) assert.equal(waiver(body), null, body);
  const closed = "```js\ncode\n````  \t\nDocs-impact: none — after a longer closing fence with trailing spaces\n";
  assert.equal(waiver(closed), "Docs-impact: none — after a longer closing fence with trailing spaces");
  const shown = "```\ncode\n```\n<!-- note -->\nDocs-impact: none — labels only, no new concept\n";
  assert.equal(waiver(shown), "Docs-impact: none — labels only, no new concept");
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

test("a coverage module present at the base but gone at head fails, so a rename cannot retire the check", () => {
  const [finding] = deferralFindings({ base: coverage(baseRows), head: null });
  assert.match(finding, /exists at the base but not at head/);
});

test("a wrapped line that starts with an issue number does not end a term's section", () => {
  const md = "**Low-stock floor (#911)** — a warning point.\nOnly an Owner sets one (farm configuration,\n#729); Managers read it. `Available < floor` lights it.\n";
  assert.match(termSections(md).get("low-stock floor"), /Available < floor/);
});

test("every unchanged definition of a changed term is reported, even when a sibling changed", () => {
  const lifecycle = [
    { key: "LockedEntry", spec: "Daily entry lifecycle" },
    { key: "VoidEntry", spec: "Daily entry lifecycle" },
  ];
  const baseSpec = "**Daily entry lifecycle** — locks after 7 days; a void vacates the day.\n";
  const headSpec = "**Daily entry lifecycle** — locks after 14 days; a void keeps the day.\n";
  const baseEn = catalog({ "help.glossaryLockedEntryDef": "Locks after 7 days.", "help.glossaryVoidEntryDef": "Vacates the day." });
  const headEn = new Map(baseEn).set("help.glossaryLockedEntryDef", "Locks after 14 days.");
  const [finding] = driftFindings({ baseEn, headEn, baseSpec, headSpec, entries: lifecycle });
  assert.match(finding, /help\.glossaryVoidEntryDef/);
  assert.doesNotMatch(finding, /LockedEntryDef/);
});
