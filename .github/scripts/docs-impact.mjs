// #1186 — a pull request that adds user-facing strings must also touch the
// docs that explain them: specs/product/GLOSSARY.md or an en.ts `help.*` key.
// And a GLOSSARY.md term whose text changes must change its in-app definition
// too, when it has one (#588 updated the spec and left the app's copy stale).
// A body line `Docs-impact: none — <reason>` lets either through; the maintainer
// reads that line at merge. Whether a change is user-visible is a judgment,
// so this is a trigger for that judgment, not a verdict.
//
// Usage, from the repository root:
//   node .github/scripts/docs-impact.mjs --base <ref> [--head <ref>] [--body-file <file>]
// Without --head it reads the working tree.

import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const EN = "web/src/i18n/en.ts";
const SPEC = "specs/product/GLOSSARY.md";
const IN_APP = "web/src/routes/helpGlossary.ts";

export const ESCAPE = /^Docs-impact:\s*none\s*(?:—|–|-{1,2})\s*\S/;

// en.ts as `namespace.key` → string. Namespaces nest one level; a key may
// itself contain dots ("recordHistory.createdBy"), which is fine for a set.
export function flatten(catalog) {
  const keys = new Map();
  for (const [namespace, entries] of Object.entries(catalog)) {
    for (const [key, value] of Object.entries(entries)) keys.set(`${namespace}.${key}`, value);
  }
  return keys;
}

export function newStringFindings({ baseEn, headEn, glossaryChanged }) {
  const added = [...headEn.keys()].filter((k) => !baseEn.has(k) && !k.startsWith("help."));
  const helpChanged = [...new Set([...baseEn.keys(), ...headEn.keys()])]
    .some((k) => k.startsWith("help.") && baseEn.get(k) !== headEn.get(k));
  if (added.length === 0 || helpChanged || glossaryChanged) return [];
  return [
    `New user-facing strings with no Help or glossary change: ${added.join(", ")}.\n`
    + `  Update ${SPEC} and the in-app Help (en/es/tl \`help.*\`), or add \`Docs-impact: none — <reason>\` to the PR body.`,
  ];
}

// The same term rule as web/src/routes/helpGlossary.test.ts: a bold run that
// opens a paragraph, or an h3, with any parenthetical dropped. A term's text
// runs to the next term or heading, so a paragraph added under it counts.
export const normalise = (term) => term.replace(/\s*\([^)]*\)/g, "").trim().toLowerCase();
export function termSections(markdown) {
  const sections = new Map();
  let current = null;
  const lines = markdown.split("\n");
  lines.forEach((line, i) => {
    const m = /^\*\*([^*]+)\*\*|^### (.+)/.exec(line);
    if (m && (m[2] !== undefined || i === 0 || lines[i - 1].trim() === "")) {
      current = normalise(m[1] ?? m[2]);
      sections.set(current, "");
    } else if (line.startsWith("#")) {
      current = null;
    }
    if (current !== null) sections.set(current, sections.get(current) + line + "\n");
  });
  return sections;
}

export function driftFindings({ baseEn, headEn, baseSpec, headSpec, entries }) {
  const before = termSections(baseSpec);
  const after = termSections(headSpec);
  const keysByTerm = Map.groupBy(entries, (e) => normalise(e.spec));
  return [...keysByTerm].flatMap(([term, group]) => {
    if (!before.has(term) || before.get(term) === after.get(term)) return [];
    const defs = group.map((e) => `help.glossary${e.key}Def`);
    if (defs.some((d) => baseEn.get(d) !== headEn.get(d))) return [];
    return [`${SPEC} changed "${term}", but its in-app definition did not: ${defs.join(", ")} (and es/tl).`];
  });
}

function git(...args) {
  return execFileSync("git", args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
}

function read(ref, path) {
  return ref === undefined ? readFileSync(path, "utf8") : git("show", `${ref}:${path}`);
}

// en.ts carries no imports, so Node loads it as TypeScript straight from a
// temporary copy of any revision.
async function loadCatalog(source, dir, name) {
  const file = join(dir, `${name}.ts`);
  writeFileSync(file, source);
  return flatten((await import(pathToFileURL(file).href)).en);
}

// helpGlossary.ts carries no imports either; a revision before it existed has
// no in-app entries to drift from.
async function loadEntries(ref, dir) {
  let source;
  try {
    source = read(ref, IN_APP);
  } catch {
    return [];
  }
  const file = join(dir, "glossary.ts");
  writeFileSync(file, source);
  return (await import(pathToFileURL(file).href)).GLOSSARY;
}

async function main(argv) {
  const opts = {};
  for (let i = 0; i < argv.length; i += 2) {
    if (!["--base", "--head", "--body-file"].includes(argv[i]) || !argv[i + 1]) {
      console.error("Usage: node .github/scripts/docs-impact.mjs --base <ref> [--head <ref>] [--body-file <file>]");
      return 2;
    }
    opts[argv[i].slice(2)] = argv[i + 1];
  }
  if (!opts.base) {
    console.error("--base is required.");
    return 2;
  }
  const dir = mkdtempSync(join(tmpdir(), "docs-impact-"));
  try {
    const baseEn = await loadCatalog(read(opts.base, EN), dir, "base");
    const headEn = await loadCatalog(read(opts.head, EN), dir, "head");
    const baseSpec = read(opts.base, SPEC);
    const headSpec = read(opts.head, SPEC);
    const findings = [
      ...newStringFindings({ baseEn, headEn, glossaryChanged: baseSpec !== headSpec }),
      ...driftFindings({ baseEn, headEn, baseSpec, headSpec, entries: await loadEntries(opts.head, dir) }),
    ];
    if (findings.length === 0) {
      console.log("docs-impact: nothing to flag.");
      return 0;
    }
    const body = opts["body-file"] ? readFileSync(opts["body-file"], "utf8") : "";
    const reason = body.split("\n").find((line) => ESCAPE.test(line));
    if (reason) {
      console.log(`docs-impact: flagged, and the PR body says why not:\n  ${reason}`);
      return 0;
    }
    console.error(findings.join("\n"));
    return 1;
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await main(process.argv.slice(2));
}
