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

// CIE76 dE in Lab (#777). `contrast` above answers "can this be seen against
// that"; this answers "can these two be told apart", which is the question a
// categorical encoding actually asks and which sRGB distance and string
// identity both get wrong. CIE76 rather than CIE2000: it is a dozen lines
// instead of sixty, and the threshold it is used at is far from the region
// where the two disagree.
function lab(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16);
  const lin = (c: number) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  const [r, g, b] = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((v) => lin(v / 255));
  const x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
  const y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  const z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
  const f = (t: number) => (t > 0.008856 ? Math.cbrt(t) : 7.787 * t + 16 / 116);
  return [116 * f(y) - 16, 500 * (f(x) - f(y)), 200 * (f(y) - f(z))];
}

export function deltaE(a: string, b: string): number {
  const [la, aa, ba] = lab(a);
  const [lb, ab, bb] = lab(b);
  return Math.hypot(la - lb, aa - ab, ba - bb);
}

// Every CSS named colour (CSS Color 4), plus the keywords that carry no colour
// of their own. A named colour is the hole `color-mix(in oklab, red 7%, …)`
// walked through: the value contains a token, so a "does it mention var(--)"
// check passes it (#777, second review round).
const COLOUR_KEYWORDS = new Set(["inherit", "initial", "unset", "revert", "revert-layer", "none", "transparent", "currentcolor"]);
const NAMED_COLOURS = new Set(`aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue
blueviolet brown burlywood cadetblue chartreuse chocolate coral cornflowerblue cornsilk crimson cyan darkblue
darkcyan darkgoldenrod darkgray darkgreen darkgrey darkkhaki darkmagenta darkolivegreen darkorange darkorchid
darkred darksalmon darkseagreen darkslateblue darkslategray darkslategrey darkturquoise darkviolet deeppink
deepskyblue dimgray dimgrey dodgerblue firebrick floralwhite forestgreen fuchsia gainsboro ghostwhite gold
goldenrod gray green greenyellow grey honeydew hotpink indianred indigo ivory khaki lavender lavenderblush
lawngreen lemonchiffon lightblue lightcoral lightcyan lightgoldenrodyellow lightgray lightgreen lightgrey
lightpink lightsalmon lightseagreen lightskyblue lightslategray lightslategrey lightsteelblue lightyellow lime
limegreen linen magenta maroon mediumaquamarine mediumblue mediumorchid mediumpurple mediumseagreen
mediumslateblue mediumspringgreen mediumturquoise mediumvioletred midnightblue mintcream mistyrose moccasin
navajowhite navy oldlace olive olivedrab orange orangered orchid palegoldenrod palegreen paleturquoise
palevioletred papayawhip peachpuff peru pink plum powderblue purple rebeccapurple red rosybrown royalblue
saddlebrown salmon sandybrown seagreen seashell sienna silver skyblue slateblue slategray slategrey snow
springgreen steelblue tan teal thistle tomato turquoise violet wheat white whitesmoke yellow yellowgreen`
  .split(/\s+/).filter(Boolean));

// Functions that PRODUCE a colour. `color-mix` is deliberately absent: mixing
// tokens is the point, and its arguments are checked like any other value.
const COLOUR_FUNCTIONS = /\b(rgba?|hsla?|hwb|lab|lch|oklab|oklch|color|device-cmyk|light-dark)\s*\(/i;

// The literal colour in `value`, or null. `var(--x)` references are removed
// first, so only what a token did NOT supply is examined.
//
// Limit, stated rather than implied: both lists are finite and CSS Color keeps
// adding to them, so a colour syntax newer than this file slips through. That
// is why the caller ALSO requires a var(--) to be present — this narrows what
// may sit beside the token, it does not stand alone.
export function literalColourIn(value: string): string | null {
  // A var() FALLBACK renders whenever the token is undefined, so it is a real
  // colour and has to be inspected. Dropping it with the reference is how
  // `var(--day-fill, red)` passed: the whole expression vanished and there was
  // nothing left to look at. The fallback is unwrapped rather than deleted.
  //
  // One pass, not a loop. `[^()]*` cannot cross a paren, so the first replace
  // matches the INNERMOST `var(--x, <literal>)` and leaves that literal bare in
  // the same pass; later passes only tidy wrapper text, which carries no
  // colour. A repeat loop shipped here briefly: a mutation deleting it left
  // every test green, and comparing looped against single-pass over 42 nested
  // shapes found no value where the two reach different verdicts. The nested
  // cases are pinned directly in styles.test.ts either way.
  const bare = value
    .replace(/var\(\s*--[\w-]+\s*,([^()]*)\)/g, " $1 ")
    .replace(/var\(\s*--[\w-]+\s*\)/g, " ");
  const hex = bare.match(/#[0-9a-f]{3,8}\b/i);
  if (hex) return hex[0];
  const fn = bare.match(COLOUR_FUNCTIONS);
  if (fn) return fn[0].trim();
  for (const word of bare.toLowerCase().match(/[a-z][a-z-]*/g) ?? []) {
    if (COLOUR_KEYWORDS.has(word)) continue;
    if (NAMED_COLOURS.has(word)) return word;
  }
  return null;
}
