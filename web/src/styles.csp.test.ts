import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";

// #735 (codex review of #736) — the API serves this stylesheet under
// `img-src 'self' blob:` (SecurityHeaders.cs, pinned by SecurityHeadersTests),
// and CSP applies img-src to CSS background images. A `data:` glyph therefore
// renders on the Vite dev server (no CSP) and vanishes in production, and no
// DOM test can see it: jsdom loads no CSS and enforces no policy. So this
// walks EVERY url() token in every declaration and admits only same-origin
// references — a leading `/`, `./` or `../`, or a bare `#fragment`. Anything
// with a scheme (data:, https:, blob:) fails here rather than shipping.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);

const urlToken = /url\(\s*(?:"([^"]*)"|'([^']*)'|([^)]*))\s*\)/g;

describe("styles.css url() references satisfy the served CSP", () => {
  it("names no scheme — only same-origin paths or fragments", () => {
    const offenders: string[] = [];
    root.walkDecls((decl) => {
      for (const m of decl.value.matchAll(urlToken)) {
        const target = (m[1] ?? m[2] ?? m[3] ?? "").trim();
        const sameOrigin = /^(\/|\.\/|\.\.\/|#)/.test(target);
        if (!sameOrigin) {
          offenders.push(`${decl.source?.start?.line ?? "?"}: ${decl.prop}: ${target.slice(0, 60)}`);
        }
      }
    });
    expect(offenders).toEqual([]);
  });
});
