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
    for (const g of GLOSSARY_GROUPS) {
      expect(typeof pack.help[g.labelKey], g.labelKey).toBe("string");
    }
  });

  it("uses every group at least once, and every entry names a declared group", () => {
    const declared = new Set(GLOSSARY_GROUPS.map((g) => g.key));
    for (const e of GLOSSARY) expect(declared.has(e.group), e.key).toBe(true);
    for (const g of GLOSSARY_GROUPS) {
      expect(GLOSSARY.some((e) => e.group === g.key), g.key).toBe(true);
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

  it("names, for every entry, a term that exists in specs/product/GLOSSARY.md", () => {
    expect(specTerms.size).toBeGreaterThan(50);
    for (const e of GLOSSARY) {
      expect(specTerms.has(normalise(e.spec)), `${e.key} → "${e.spec}"`).toBe(true);
    }
  });
});
