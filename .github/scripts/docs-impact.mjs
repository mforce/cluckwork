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
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const EN = "web/src/i18n/en.ts";
const SPEC = "specs/product/GLOSSARY.md";
const IN_APP = "web/src/routes/helpGlossary.ts";
const HEADING = /^#{1,6}(?:\s|$)/;
const COVERAGE = "web/src/routes/glossaryCoverage.ts";

export const ESCAPE = /^Docs-impact:\s*none\s*(?:—|–|-{1,2})[ \t]+\S/;

// The waiver line the maintainer will actually see: text inside a fenced code
// block or an HTML comment is an example or a template note, not a reason.
export function waiver(body) {
  const visible = body
    .replace(/<!--[\s\S]*?(?:-->|$)/g, "")
    .replace(/^ {0,3}(`{3,}|~{3,}).*$[\s\S]*?(?:^ {0,3}\1.*$|(?![\s\S]))/gm, "");
  return visible.split("\n").find((line) => ESCAPE.test(line)) ?? null;
}

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

// The same term rule as web/src/routes/glossaryCoverage.ts: a bold run that
// opens a paragraph (after a blank line or a heading), or an h3, with any
// parenthetical dropped. A term's text
// runs to the next term or heading, so a paragraph added under it counts.
export const normalise = (term) => term.replace(/\s*\([^)]*\)/g, "").trim().toLowerCase();
export function termSections(markdown) {
  const sections = new Map();
  let current = null;
  const lines = markdown.split("\n");
  lines.forEach((line, i) => {
    const m = /^\*\*([^*]+)\*\*|^### (.+)/.exec(line);
    if (m && (m[2] !== undefined || i === 0 || lines[i - 1].trim() === "" || HEADING.test(lines[i - 1]))) {
      current = normalise(m[1] ?? m[2]);
      sections.set(current, "");
    } else if (HEADING.test(line)) {
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
    const stale = group.map((e) => `help.glossary${e.key}Def`).filter((d) => baseEn.get(d) === headEn.get(d));
    if (stale.length === 0) return [];
    return [`${SPEC} changed "${term}", but these in-app definitions did not: ${stale.join(", ")} (and es/tl).`];
  });
}

// A count of NOT_YET rows cannot tell a removal from a swap, so the rows are
// compared by term with the base: a term deferred at head but not at the base
// is new debt. Not escapable by the body line; a real reason row is the way.
export function deferralFindings({ base, head }) {
  if (!base || !head) return [];
  const deferred = ({ SPEC_ONLY, NOT_YET }) => Object.keys(SPEC_ONLY).filter((term) => SPEC_ONLY[term] === NOT_YET);
  const before = new Set(deferred(base));
  return deferred(head).filter((term) => !before.has(term)).map((term) =>
    `"${term}" is newly deferred as NOT_YET in ${COVERAGE}. Give it an in-app glossary entry, or a SPEC_ONLY row whose reason says why users never meet it.`);
}

function git(...args) {
  return execFileSync("git", args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
}

// null only when the revision has no such file; any git failure throws.
function read(ref, path) {
  if (ref === undefined) return existsSync(path) ? readFileSync(path, "utf8") : null;
  return git("ls-tree", "--name-only", ref, "--", path).trim() === "" ? null : git("show", `${ref}:${path}`);
}

// en.ts carries no imports, so Node loads it as TypeScript straight from a
// temporary copy of any revision.
async function loadCatalog(source, dir, name) {
  const file = join(dir, `${name}.ts`);
  writeFileSync(file, source);
  return flatten((await import(pathToFileURL(file).href)).en);
}

// helpGlossary.ts and glossaryCoverage.ts carry no imports either. A revision
// that predates one of them yields null.
async function loadModule(ref, path, dir, name) {
  const source = read(ref, path);
  if (source === null) return null;
  const file = join(dir, `${name}.ts`);
  writeFileSync(file, source);
  return import(pathToFileURL(file).href);
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
  for (const ref of [opts.base, opts.head].filter(Boolean)) {
    try {
      git("rev-parse", "--verify", "--quiet", "--end-of-options", `${ref}^{commit}`);
    } catch {
      console.error(`${ref} is not a commit this checkout has. Fetch it first.`);
      return 2;
    }
  }
  const dir = mkdtempSync(join(tmpdir(), "docs-impact-"));
  try {
    const baseEn = await loadCatalog(read(opts.base, EN), dir, "base");
    const headEn = await loadCatalog(read(opts.head, EN), dir, "head");
    const baseSpec = read(opts.base, SPEC);
    const headSpec = read(opts.head, SPEC);
    const findings = [
      ...newStringFindings({ baseEn, headEn, glossaryChanged: baseSpec !== headSpec }),
      ...driftFindings({ baseEn, headEn, baseSpec, headSpec, entries: (await loadModule(opts.head, IN_APP, dir, "glossary"))?.GLOSSARY ?? [] }),
    ];
    const deferrals = deferralFindings({
      base: await loadModule(opts.base, COVERAGE, dir, "coverage-base"),
      head: await loadModule(opts.head, COVERAGE, dir, "coverage-head"),
    });
    if (deferrals.length > 0) {
      console.error(deferrals.join("\n"));
      return 1;
    }
    if (findings.length === 0) {
      console.log("docs-impact: nothing to flag.");
      return 0;
    }
    const body = opts["body-file"] ? readFileSync(opts["body-file"], "utf8") : "";
    const reason = waiver(body);
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
