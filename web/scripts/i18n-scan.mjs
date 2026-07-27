#!/usr/bin/env node
// Pragmatic hardcoded-string scanner for the #182 i18n sweep. See the
// "Hardcoded-string scan" section of CONTRIBUTING-i18n.md for how batches use
// this.
//
// WHAT THIS IS: a grep-level heuristic, NOT an AST-perfect analyzer. It flags
// two shapes known to cover the vast majority of un-externalized user-facing
// copy in this codebase:
//   1. JSX text nodes — literal text sitting between an opening/self-closing
//      tag's `>` and the next `<`/`{`/`}`, that contains a letter.
//   2. String-literal values on the five attributes most likely to carry
//      user-facing copy: placeholder=, aria-label=, title=, alt=, label=.
// A hit is EXCLUDED when it is already wrapped in `t(...)`, `i18n.t(...)`, or
// `<Trans>` — the two forms this scan can't see through are handled directly:
//   - `{t("ns:key")}` / `{i18n.t(...)}` as a JSX child or an attribute value
//     never matches either pattern above IN THE FIRST PLACE: it's an
//     expression container (`{...}`), not literal text or a quoted literal,
//     so nothing extra is needed to skip it.
//   - `<Trans>...</Trans>` children ARE literal JSX text (that's the whole
//     point of Trans), so they get a dedicated mask pass before scanning.
//
// USAGE
//   node scripts/i18n-scan.mjs [path ...]
//   npm run i18n:scan -- [path ...]
// Defaults to `src/routes src/components` (the SPA's screen + shared-widget
// code). Each path may be a directory (walked recursively for `.tsx` files,
// skipping `*.test.tsx`) or a single file (scanned regardless of extension —
// e.g. a future full-tree pass can add `index.html` for the brand `<title>`).
//
// ALLOWLIST: scripts/i18n-scan-allowlist.txt lists intentional survivors
// (data values, brand strings, server-rendered text) that would otherwise
// show up as hits. See that file's header for the line format.
//
// EXIT CODE is always 0. This is a reporting / monotonic-count aid for
// comparing batch-to-batch progress ("did the sweep's un-externalized count
// go down"), not a CI gate — a false positive shouldn't fail a build.
//
// KNOWN LIMITATIONS (accepted for a grep-level tool; see CONTRIBUTING-i18n.md)
//   - No real parser: this is a handful of regexes plus light masking, tuned
//     against the current tree. Two rare shapes can still slip past the
//     masking and produce a false positive:
//       (a) a bare `<`/`>` relational operator (not `=>`/`>=`/`<=`, which ARE
//           neutralized) sitting inside a tag's OWN multi-line attribute
//           expression, e.g. `<button disabled={x > y}>` — the stray `>`
//           can be misread as that tag's closing bracket.
//       (b) plain code that immediately follows a self-closing tag with no
//           `<`, `{`, `}`, or `;` before the next real tag, e.g. an early
//           `return <Foo />;` followed by another bare `return ...;`.
//     Eyeball a big delta before trusting it; these are rare (single digits
//     across the whole SPA at last check) and always visible in the printed
//     `file:line: <snippet>` output.
//   - Text that trails a CLOSING or self-closing sibling tag before the next
//     tag (e.g. the " world" in `<p>a <b>b</b> world</p>`) is not detected —
//     only text right after an opening/self-closing tag is scanned. This
//     avoids a much noisier false-positive class (ternary/JSX-conditional
//     code sitting between a closing tag and the next sibling), at the cost
//     of missing that rarer "tail text after a nested child" shape.
//   - Only the 5 named attributes are checked, and only quoted string
//     LITERALS — `placeholder={t("x")}` is an expression, correctly ignored.
//     A hardcoded literal fallback buried in plain JS (e.g. `defaultValue:
//     "Some text"`, `throw new Error("...")`) is out of scope: those aren't
//     JSX text or one of the 5 attributes, matching the brief's narrower
//     definition of "likely user-facing string literal."
//   - `.test.tsx` files are always excluded, even if passed directly: test
//     fixtures/assertions aren't shipped copy, and their routine churn would
//     make the batch-to-batch count noisy for reasons unrelated to the sweep.
//   - `//` and `/* */` comments are masked out (so a comment that MENTIONS
//     JSX, like `// keep the <span> for its title`, doesn't get scanned as
//     if it were real markup). This assumes `//` never appears inside a
//     quoted literal in the scanned files — true today (checked); if that
//     ever changes, that literal's tail could be masked away too.

import { readFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = fileURLToPath(new URL(".", import.meta.url));
const ALLOWLIST_PATH = join(SCRIPT_DIR, "i18n-scan-allowlist.txt");
const CWD = process.cwd();

const DEFAULT_PATHS = ["src/routes", "src/components"];
const ATTR_NAMES = ["placeholder", "aria-label", "title", "alt", "label"];

// --- masking passes (applied in this order, each keeping length/newlines so
// downstream line numbers stay correct) ------------------------------------

const BLOCK_COMMENT_RE = /\/\*[\s\S]*?\*\//g;
const LINE_COMMENT_RE = /\/\/[^\n]*/g;
// HTML comments only matter for a non-.tsx file passed directly (index.html —
// see the Trans/generic notes below and the usage note above); harmless on a
// .tsx file, which never contains this token.
const HTML_COMMENT_RE = /<!--[\s\S]*?-->/g;
// A TS generic type argument list, e.g. `useState<Foo | null>`, `Record<string,
// unknown>`. Excluding `/` from the allowed interior chars is deliberate: it
// stops this from also swallowing "word</tag>" (real JSX text immediately
// followed by its own closing tag with no space) — a real generic's argument
// list never contains a literal slash.
const GENERIC_RE = /\b[A-Za-z_$][\w$]*<[^<>/]*>/g;

function maskLengthPreserving(content, re) {
  return content.replace(re, (m) => m.replace(/[^\n]/g, " "));
}

// Masks every `<Trans ...>` usage, self-closing or paired, wholesale — its
// children are literal JSX text BY DESIGN (that's what Trans is for: text
// interleaved with real JSX like `<strong>`), so nothing inside should be
// scanned as a hit.
//
// This is a tiny hand-rolled scan rather than a single regex because a
// self-closing `<Trans ... components={{ strong: <strong /> }} />` nests
// ANOTHER tag's `<`/`>` inside a `{{ }}` prop value — a naive
// `/<Trans\b[^>]*\/>/` regex latches onto the FIRST `/>` it finds, which is
// `<strong />`'s, not the real one. Tracking `{}` depth and only treating
// `/>` or a bare `>` as the tag's own end while depth is back at 0 fixes
// that: braces inside props are transparent, so the inner tag's brackets are
// invisible to the "where does THIS Trans end" question.
function maskTransBlocks(content) {
  let out = "";
  let i = 0;
  while (i < content.length) {
    const idx = content.indexOf("<Trans", i);
    if (idx < 0) {
      out += content.slice(i);
      break;
    }
    const charAfter = content[idx + 6] ?? " ";
    if (!/[\s/>]/.test(charAfter)) {
      // "<Transxyz" — a different component name, not this one.
      out += content.slice(i, idx + 6);
      i = idx + 6;
      continue;
    }
    let depth = 0;
    let j = idx;
    let end = -1;
    while (j < content.length) {
      const c = content[j];
      if (c === "{") depth++;
      else if (c === "}") depth--;
      else if (depth === 0 && c === "/" && content[j + 1] === ">") {
        end = j + 2; // self-closing: <Trans ... />
        break;
      } else if (depth === 0 && c === ">") {
        const closeIdx = content.indexOf("</Trans>", j);
        end = closeIdx >= 0 ? closeIdx + "</Trans>".length : j + 1;
        break;
      }
      j++;
    }
    if (end < 0) end = content.length; // unterminated — mask to EOF rather than throw
    out += content.slice(i, idx);
    out += content.slice(idx, end).replace(/[^\n]/g, " ");
    i = end;
  }
  return out;
}

// Replaces every char in `re`'s matches with " ", NEWLINES EXCEPTED, so a
// masked region still occupies exactly the same lines/columns as the source —
// every later line-number lookup stays accurate without re-deriving offsets.
function maskAll(content) {
  let out = maskLengthPreserving(content, BLOCK_COMMENT_RE);
  out = maskLengthPreserving(out, LINE_COMMENT_RE);
  out = maskLengthPreserving(out, HTML_COMMENT_RE);
  out = maskTransBlocks(out);
  out = maskLengthPreserving(out, GENERIC_RE);
  // Arrow tokens (`=>`) and `>=`/`<=` carry a `>`/`<` that isn't a tag
  // boundary; swap the operator's own bracket char for a space (same length,
  // so offsets are untouched) so it can't be mistaken for one.
  out = out.replace(/=>/g, "= ").replace(/>=/g, " =").replace(/<=/g, "= ");
  return out;
}

// --- tag-aware JSX text-node scan ------------------------------------------

// Matches a JSX tag: `<div ...>`, `</div>`, self-closing `<input ... />`, or a
// fragment `<>`/`</>`. Requires the char right after `<` (and after an
// optional `/`) to be a letter (or, for fragments, nothing) — this is what
// keeps a bare comparison like `a < b` (space before the operand) from ever
// looking like a tag start.
const TAG_RE = /<(\/)?([A-Za-z][^<>]*)?>/g;

function lineIndex(content) {
  const starts = [0];
  for (let i = 0; i < content.length; i++) if (content[i] === "\n") starts.push(i + 1);
  return (offset) => {
    let lo = 0, hi = starts.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (starts[mid] <= offset) lo = mid; else hi = mid - 1;
    }
    return lo + 1;
  };
}

// Finds hardcoded JSX text nodes: the run of plain characters right after an
// opening or self-closing tag, up to the next `<` (a sibling/child tag),
// `{` or `}` (a JSX expression container). Only opening/self-closing tags
// start a scan — a CLOSING tag's `>` is skipped on purpose (see the "Known
// limitations" header note on tail text after a nested child).
function findTextNodeHits(content, lineOf) {
  const hits = [];
  for (const m of content.matchAll(TAG_RE)) {
    const isClosing = m[1] === "/";
    if (isClosing) continue;
    const tagEnd = m.index + m[0].length;
    let j = tagEnd;
    while (j < content.length && !"<{}".includes(content[j])) j++;
    const raw = content.slice(tagEnd, j);
    const value = raw.replace(/\s+/g, " ").trim();
    if (!value || !/[A-Za-z]/.test(value)) continue;
    hits.push({ line: lineOf(tagEnd), value, kind: "text" });
  }
  return hits;
}

// Finds string-literal values on the watched attributes. Deliberately only
// matches `name="literal"` / `name='literal'` (no whitespace around `=`) —
// `name={expr}` is an expression (already-translated or otherwise dynamic)
// and JS's space-separated `name = "literal"` (object/destructuring default)
// isn't JSX attribute syntax at all, so both are correctly left alone.
function findAttrHits(content, lineOf) {
  const hits = [];
  const re = new RegExp(`\\b(${ATTR_NAMES.join("|")})=(?:"([^"]*)"|'([^']*)')`, "g");
  for (const m of content.matchAll(re)) {
    const value = m[2] ?? m[3] ?? "";
    if (!value.trim() || !/[A-Za-z]/.test(value)) continue;
    hits.push({ line: lineOf(m.index), value: `${m[1]}="${value}"`, kind: "attr" });
  }
  return hits;
}

// --- allowlist ---------------------------------------------------------

// Format: one `<path>:<substring>` entry per line, where `<path>` is the file
// path AS PRINTED by this script (relative to the cwd it was run from — i.e.
// relative to `web/` for the default `npm run i18n:scan`), and `<substring>`
// is matched against the hit's value/snippet with plain `.includes()` (not a
// regex). Blank lines and lines starting with `#` are ignored.
function loadAllowlist() {
  if (!existsSync(ALLOWLIST_PATH)) return [];
  const lines = readFileSync(ALLOWLIST_PATH, "utf8").split("\n");
  const entries = [];
  for (const raw of lines) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const sep = line.indexOf(":");
    if (sep < 0) continue; // malformed line — skip rather than crash a reporting tool
    entries.push({ file: line.slice(0, sep).trim(), substring: line.slice(sep + 1).trim() });
  }
  return entries;
}

function isAllowlisted(entries, relFile, value) {
  return entries.some((e) => e.file === relFile && value.includes(e.substring));
}

// --- file collection -----------------------------------------------------

function collectFiles(inputPath) {
  const abs = join(CWD, inputPath);
  if (!existsSync(abs)) {
    console.error(`[i18n-scan] path not found, skipping: ${inputPath}`);
    return [];
  }
  const st = statSync(abs);
  if (!st.isDirectory()) return [inputPath];

  const out = [];
  (function walk(dir) {
    for (const entry of readdirSync(join(CWD, dir))) {
      if (entry === "node_modules" || entry === "dist") continue;
      const relPath = join(dir, entry);
      const s = statSync(join(CWD, relPath));
      if (s.isDirectory()) walk(relPath);
      else if (entry.endsWith(".tsx") && !entry.endsWith(".test.tsx")) out.push(relPath);
    }
  })(inputPath);
  return out;
}

// --- main ------------------------------------------------------------------

const args = process.argv.slice(2);
const targets = args.length > 0 ? args : DEFAULT_PATHS;
const allowlist = loadAllowlist();

const files = targets.flatMap(collectFiles).filter((f) => !f.endsWith(".test.tsx"));

let total = 0;
let allowed = 0;
for (const relFile of files) {
  const content = readFileSync(join(CWD, relFile), "utf8");
  const masked = maskAll(content);
  const lineOf = lineIndex(masked);

  const hits = [...findAttrHits(masked, lineOf), ...findTextNodeHits(masked, lineOf)]
    .sort((a, b) => a.line - b.line);

  for (const hit of hits) {
    if (isAllowlisted(allowlist, relFile, hit.value)) {
      allowed++;
      continue;
    }
    total++;
    const snippet = hit.value.length > 100 ? `${hit.value.slice(0, 100)}…` : hit.value;
    console.log(`${relFile}:${hit.line}: ${snippet}`);
  }
}

console.log(
  `\n${files.length} file(s) scanned, ${allowed} allowlisted match(es) excluded.\nCOUNT: ${total}`,
);
process.exit(0);
