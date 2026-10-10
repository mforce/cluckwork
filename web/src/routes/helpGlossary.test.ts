import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { GLOSSARY, GLOSSARY_GROUPS, glossaryEntry } from "./helpGlossary";
import { NOT_YET, SPEC_ONLY, glossaryTerms, normalise } from "./glossaryCoverage";
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

const specTerms = glossaryTerms(readFileSync(resolve(process.cwd(), "../specs/product/GLOSSARY.md"), "utf8"));


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
    expect(specTerms.length).toBeGreaterThan(100);
    const inApp = new Set(GLOSSARY.map((e) => normalise(e.spec)));
    const missing = specTerms.filter((term) => !inApp.has(term) && !(term in SPEC_ONLY));
    expect(missing, "add an in-app glossary entry (helpGlossary.ts + en/es/tl), or a SPEC_ONLY row with a reason").toEqual([]);
  });

  it("keeps SPEC_ONLY to current GLOSSARY.md terms that have no in-app entry", () => {
    const inApp = new Set(GLOSSARY.map((e) => normalise(e.spec)));
    for (const [term, reason] of Object.entries(SPEC_ONLY)) {
      expect(specTerms.includes(term), `SPEC_ONLY "${term}" is no longer a GLOSSARY.md term`).toBe(true);
      expect(inApp.has(term), `SPEC_ONLY "${term}" now has an in-app entry; delete the row`).toBe(false);
      expect(reason, `SPEC_ONLY "${term}" needs a reason, not a placeholder`).not.toMatch(/^\s*(?:todo|tbd|fixme)?[\s.:!?-]*$/i);
    }
  });

  it("only ever loses NOT_YET rows", () => {
    // Lower this when a NOT_YET term gets its entry. A new term needs an entry
    // or a real reason, never NOT_YET.
    expect(Object.values(SPEC_ONLY).filter((reason) => reason === NOT_YET)).toHaveLength(46);
  });

  it("names, for every entry, a term that exists in specs/product/GLOSSARY.md", () => {
    expect(specTerms.length).toBeGreaterThan(100);
    for (const e of GLOSSARY) {
      expect(specTerms.includes(normalise(e.spec)), `${e.key} → "${e.spec}"`).toBe(true);
    }
  });
});
