import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { AtRule, Rule } from "postcss";

// #657 visual pass — the layout facts jsdom cannot see: the glossary is a
// term/definition grid with sticky group heads on desktop, and the rail is a
// pinned horizontal strip on a phone. Read from the parsed stylesheet, as
// styles.num.test.ts and components/styles.dialog.test.ts do.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);
const clean = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, "").trim();

function decls(selector: string, inMedia: string | null): Map<string, string> {
  const out = new Map<string, string>();
  const visit = (rule: Rule) => {
    if (!rule.selectors.map(clean).includes(selector)) return;
    rule.walkDecls((d) => { out.set(d.prop, d.value); });
  };
  if (inMedia === null) {
    root.each((node) => { if (node.type === "rule") visit(node); });
  } else {
    root.walkAtRules("media", (at: AtRule) => {
      if (clean(at.params) !== inMedia) return;
      at.walkRules(visit);
    });
  }
  return out;
}

describe("glossary grid (desktop)", () => {
  it("lays each entry out as term | definition", () => {
    expect(decls(".glossary-entry", null).get("display")).toBe("grid");
    expect(decls(".glossary-entry", null).get("grid-template-columns")).toMatch(/minmax/);
  });
  it("keeps the group heading in view while its entries scroll", () => {
    expect(decls(".glossary-group h4", null).get("position")).toBe("sticky");
  });
});

describe("rail on a phone", () => {
  const MQ = "(max-width: 900px)";
  it("pins the rail under the head as one horizontal strip", () => {
    expect(decls(".help-toc", MQ).get("position")).toBe("sticky");
    const ul = decls(".help-toc ul", MQ);
    expect(ul.get("flex-wrap")).toBe("nowrap");
    // flex-wrap means nothing unless the list IS a flex container — the
    // desktop rule sets it, so the check reads the base rule.
    expect(decls(".help-toc ul", null).get("display")).toBe("flex");
  });
  it("collapses the entries back to one row (no group columns)", () => {
    expect(decls(".help-toc-group", MQ).get("display")).toBe("none");
  });
  it("sticks the glossary group heading below the pinned rail, not behind it", () => {
    expect(decls(".glossary-group h4", MQ).get("top")).not.toBe("0");
    expect(decls(".glossary-group h4", MQ).get("top")).toMatch(/rem$/);
  });
});
