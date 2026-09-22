import { describe, expect, it } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { extname, relative, resolve } from "node:path";
import postcss from "postcss";
import { parse } from "@babel/parser";

// #735 (codex review of #736) — the API serves this stylesheet under
// `img-src 'self' blob:` (SecurityHeaders.cs, pinned by SecurityHeadersTests),
// and CSP applies img-src to CSS background images. A `data:` glyph therefore
// renders on the Vite dev server (no CSP) and vanishes in production, and no
// DOM test can see it: jsdom loads no CSS and enforces no policy. So this
// walks EVERY url() token in every declaration of the one stylesheet under
// web/src (find web/src -name '*.css' → styles.css alone) and admits only
// same-origin references. Anything with a scheme (data:, https:, blob:) or a
// protocol-relative host fails here rather than shipping.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);

// CSS escapes are decoded by the browser (and re-emitted decoded by the
// bundler) BEFORE the URL is fetched, so `url(d\61 ta:x)` and `u\72l(data:x)`
// are the same blocked request as `url(data:x)`. Classify the decoded text,
// never the raw source (codex review of #736, round 3): hex escapes
// (`\61`, `\000061`, with one optional trailing whitespace) and single-char
// escapes (`\:`) are resolved here the way the tokenizer resolves them.
export const unescapeCss = (raw: string) =>
  raw.replace(/\\(?:([0-9a-f]{1,6})[ \t\n\f\r]?|(.))/gi, (_, hex: string | undefined, ch: string | undefined) =>
    hex !== undefined ? String.fromCodePoint(parseInt(hex, 16)) : (ch ?? ""));

// CSS function names are case-insensitive — `URL("data:…")` is valid and just
// as blocked — so the token match is /i (codex review of #736, round 2).
const urlToken = /url\(\s*(?:"([^"]*)"|'([^']*)'|([^)]*))\s*\)/gi;

// 'self' admits same-origin only. A reference is same-origin when it names no
// scheme and is not protocol-relative: `/icons/x.svg`, `icons/x.svg`,
// `./x.svg`, `#frag` pass; `data:…`, `https://…`, `blob:…` and `//cdn/x.svg`
// fail. Keyed on what CSP checks (scheme/host), not on a leading slash.
const hasScheme = /^[a-z][a-z0-9+.-]*:/i;
const protocolRelative = /^\/\//;
export const isSameOrigin = (target: string) => !hasScheme.test(target) && !protocolRelative.test(target);

describe("styles.css url() references satisfy the served CSP", () => {
  it("names no scheme — only same-origin paths or fragments", () => {
    const offenders: string[] = [];
    root.walkDecls((decl) => {
      for (const m of unescapeCss(decl.value).matchAll(urlToken)) {
        const target = (m[1] ?? m[2] ?? m[3] ?? "").trim();
        if (!isSameOrigin(target)) {
          offenders.push(`${decl.source?.start?.line ?? "?"}: ${decl.prop}: ${target.slice(0, 60)}`);
        }
      }
    });
    expect(offenders).toEqual([]);
  });
});

function productionStyleSources(): string[] {
  const root = resolve(process.cwd(), "src");
  const visit = (directory: string): string[] => readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) return entry.name === "test" ? [] : visit(path);
    return [".ts", ".tsx"].includes(extname(path)) && !entry.name.includes(".test.") ? [path] : [];
  });
  return visit(root).sort();
}

// Emotion accepts CSS url() values from any production TypeScript module, so
// this walk has the same source boundary as the class conversion census.
describe("production TypeScript styling url() references satisfy the served CSP", () => {
  it("uses only literal same-origin URLs", () => {
    const offenders: string[] = [];
    for (const file of productionStyleSources()) {
      const source = readFileSync(file, "utf8");
      const tree = parse(source, { sourceType: "module", plugins: ["typescript", ...(extname(file) === ".tsx" ? ["jsx" as const] : [])] });
      const visit = (value: unknown): void => {
        if (typeof value !== "object" || value === null) return;
        if (Array.isArray(value)) {
          for (const child of value) visit(child);
          return;
        }
        if (!("type" in value) || typeof value.type !== "string") return;
        const node = value;
        const line = "loc" in node && typeof node.loc === "object" && node.loc !== null
          && "start" in node.loc && typeof node.loc.start === "object" && node.loc.start !== null
          && "line" in node.loc.start && typeof node.loc.start.line === "number" ? node.loc.start.line : "?";
        if (node.type === "TemplateLiteral" && "quasis" in node && Array.isArray(node.quasis)) {
          const literal = node.quasis.map((quasi) => typeof quasi === "object" && quasi !== null && "value" in quasi
            && typeof quasi.value === "object" && quasi.value !== null && "cooked" in quasi.value
            && typeof quasi.value.cooked === "string" ? quasi.value.cooked : "").join("");
          const decoded = unescapeCss(literal);
          if (/url\(/i.test(decoded) && "expressions" in node
            && Array.isArray(node.expressions) && node.expressions.length > 0) {
            offenders.push(`${relative(process.cwd(), file)}:${line}: interpolated url()`);
          } else {
            for (const match of decoded.matchAll(urlToken)) {
              const target = (match[1] ?? match[2] ?? match[3] ?? "").trim();
              if (!isSameOrigin(target)) offenders.push(`${relative(process.cwd(), file)}:${line}: ${target.slice(0, 60)}`);
            }
          }
        } else if ((node.type === "StringLiteral" || node.type === "DirectiveLiteral")
          && "value" in node && typeof node.value === "string") {
          for (const match of unescapeCss(node.value).matchAll(urlToken)) {
            const target = (match[1] ?? match[2] ?? match[3] ?? "").trim();
            if (!isSameOrigin(target)) {
              offenders.push(`${relative(process.cwd(), file)}:${line}: ${target.slice(0, 60)}`);
            }
          }
        }
        for (const child of Object.values(node)) visit(child);
      };
      visit(tree.program);
    }
    expect(offenders).toEqual([]);
  });
});
