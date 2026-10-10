// Self-tests for the docs-impact check (#1186). Run with
// `node --test .github/scripts/docs-impact.test.mjs`.

import test from "node:test";
import assert from "node:assert/strict";

import { ESCAPE, newStringFindings } from "./docs-impact.mjs";

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
