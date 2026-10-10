// #1186 — a pull request that adds user-facing strings must also touch the
// docs that explain them: specs/product/GLOSSARY.md or an en.ts `help.*` key.
// A body line `Docs-impact: none — <reason>` lets it through; the maintainer
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
    const findings = newStringFindings({
      baseEn: await loadCatalog(read(opts.base, EN), dir, "base"),
      headEn: await loadCatalog(read(opts.head, EN), dir, "head"),
      glossaryChanged: read(opts.base, SPEC) !== read(opts.head, SPEC),
    });
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
