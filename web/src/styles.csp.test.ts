import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";

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
