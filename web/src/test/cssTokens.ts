/// <reference types="node" />
// Test-only CSS reader for the design tokens (#149). Lives under src/test/ so
// it is excluded from the coverage gate.
//
// The node reference above is explicit on purpose: this file reads the
// stylesheet from disk (node:fs / node:url), but it sits under the browser
// tsconfig.app, whose lib is DOM-only. TypeScript 7 no longer auto-resolves the
// `node:` builtins here without it (TS2591), so name the dependency rather than
// lean on @types auto-inclusion.
//
// Why this exists rather than getComputedStyle: jsdom does not resolve custom
// property chains, so `--focus: var(--stat-accent)` comes back as the literal
// string "var(--stat-accent)" and never as a colour. The palette guard tests
// need real resolved colours, so the cascade is rebuilt here.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// The relative path is held in a variable rather than passed as an inline
// string literal: Vite's import-analysis plugin statically pattern-matches
// `new URL("literal", import.meta.url)` and rewrites it into a dev-server
// asset URL (e.g. "http://localhost:3000/src/styles.css") under the jsdom
// test environment, which fileURLToPath() then rejects as "not scheme file".
// A variable first-argument isn't literal text, so the transform doesn't
// match and the real WHATWG URL resolution (against the real file:// module
// URL) runs instead.
const CSS_REL = "../styles.css";
const CSS_PATH = fileURLToPath(new URL(CSS_REL, import.meta.url));

export type Mode = "light" | "dark";

// Only the root token blocks — NOT descendant rules like
// `:root[data-theme="dark"] .badge-accent`, which are component styling.
const ROOT_BLOCK = /^:root(\[data-brand="[a-z]+"\])?(\[data-theme="dark"\])?$/;

interface Block {
  selector: string;
  brand: string | null;
  dark: boolean;
  specificity: number;
  order: number;
  decls: Map<string, string>;
}

function stripComments(css: string): string {
  // Comments carry colons and stray semicolons ("Light 6.71/6.71 rest"), which
  // would otherwise be parsed as declarations.
  return css.replace(/\/\*[\s\S]*?\*\//g, "");
}

function stripAtBlocks(css: string): string {
  // Drop @media/@supports bodies entirely. styles.css has a CONDITIONAL
  // `:root { --tabbar-h: ... }` nested in `@media (max-width: 900px)`, and a
  // naive scan treats it as part of the unconditional root cascade — this
  // function resolves what applies with no media condition in force, so a
  // conditional block must not leak in. (It is on a `:root` selector rather
  // than bare in the @media because a loose custom-property declaration there
  // is invalid and drops the following rule — see the comment at that block.)
  let out = "";
  for (let i = 0; i < css.length; i += 1) {
    if (css[i] !== "@") { out += css[i]; continue; }
    const open = css.indexOf("{", i);
    if (open === -1) { out += css.slice(i); break; }
    let depth = 1;
    let j = open + 1;
    while (j < css.length && depth > 0) {
      if (css[j] === "{") depth += 1;
      else if (css[j] === "}") depth -= 1;
      j += 1;
    }
    i = j - 1;
  }
  return out;
}

function parseBlocks(css: string): Block[] {
  const blocks: Block[] = [];
  // Token blocks contain no nested braces, so a non-greedy body match is safe.
  const re = /(:root[^{}]*?)\s*\{([^{}]*)\}/g;
  let m: RegExpExecArray | null;
  let order = 0;
  while ((m = re.exec(css)) !== null) {
    const selector = m[1].trim();
    if (!ROOT_BLOCK.test(selector)) continue;
    const decls = new Map<string, string>();
    for (const decl of m[2].split(";")) {
      const i = decl.indexOf(":");
      if (i === -1) continue;
      const prop = decl.slice(0, i).trim();
      if (!prop.startsWith("--")) continue;
      decls.set(prop, decl.slice(i + 1).trim().replace(/\s+/g, " "));
    }
    const brandMatch = /\[data-brand="([a-z]+)"\]/.exec(selector);
    const dark = selector.includes('[data-theme="dark"]');
    blocks.push({
      selector,
      brand: brandMatch === null ? null : brandMatch[1],
      dark,
      // :root is (0,1,0); each attribute selector adds one.
      specificity: 1 + (brandMatch === null ? 0 : 1) + (dark ? 1 : 0),
      order: order++,
      decls,
    });
  }
  return blocks;
}

let cache: Block[] | null = null;
function allBlocks(): Block[] {
  cache ??= parseBlocks(stripAtBlocks(stripComments(readFileSync(CSS_PATH, "utf8"))));
  return cache;
}

function applies(block: Block, brand: string | null, mode: Mode): boolean {
  if (block.dark && mode !== "dark") return false;
  if (block.brand !== null && block.brand !== brand) return false;
  return true;
}

/** Custom properties a SINGLE block declares — no cascade, for the required-set check. */
export function declaredKeys(brand: string | null, mode: Mode): Set<string> {
  const block = allBlocks().find(
    (b) => b.brand === brand && b.dark === (mode === "dark"));
  return new Set(block === undefined ? [] : block.decls.keys());
}

function deref(map: Map<string, string>, value: string, seen: Set<string>): string {
  // Substitutes EVERY var() occurrence, not only a whole-value reference. A
  // bare `var(--stat-accent)` becomes a colour, and the var(--lavender) buried
  // at the end of the --auth-bg gradient is substituted in place. Doing only
  // the bare case would leave a typo like var(--canavs) inside a gradient
  // permanently invisible to these tests.
  return value.replace(/var\((--[a-z0-9-]+)\)/g, (_, name: string) => {
    if (seen.has(name)) throw new Error(`circular var() chain at ${name}`);
    const next = map.get(name);
    if (next === undefined) throw new Error(`unresolved var(${name})`);
    return deref(map, next, new Set([...seen, name]));
  });
}

/** The cascaded, var()-resolved token set for one data-brand x data-theme combination. */
export function resolveTokens(brand: string | null, mode: Mode): Map<string, string> {
  const raw = new Map<string, string>();
  const winning = allBlocks()
    .filter((b) => applies(b, brand, mode))
    // Equal specificity is decided by source order — which is exactly what the
    // "light palette blocks BEFORE the dark base" ordering relies on.
    .sort((a, b) => a.specificity - b.specificity || a.order - b.order);
  for (const block of winning)
    for (const [k, v] of block.decls) raw.set(k, v);

  const resolved = new Map<string, string>();
  for (const [k, v] of raw) resolved.set(k, deref(raw, v, new Set([k])));
  return resolved;
}

function channel(c: number): number {
  const s = c / 255;
  return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
}

export function luminance(hex: string): number {
  const h = hex.trim().replace("#", "");
  const full = h.length === 3 ? h.split("").map((c) => c + c).join("") : h;
  if (!/^[0-9a-fA-F]{6}$/.test(full)) throw new Error(`not a hex colour: ${hex}`);
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16));
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

export function contrast(a: string, b: string): number {
  const [la, lb] = [luminance(a), luminance(b)];
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}
