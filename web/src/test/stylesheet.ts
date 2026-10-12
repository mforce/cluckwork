/// <reference types="node" />
// Test-only reader for the app stylesheet (#1215). `styles.css` is a list of
// `@import`s in cascade order; this inlines them, so a guard reads the same
// rules, in the same order, that Vite bundles.
//
// The node reference above is explicit on purpose: this file reads the
// stylesheet from disk (node:fs / node:url), but it sits under the browser
// tsconfig.app, whose lib is DOM-only. TypeScript 7 no longer auto-resolves the
// `node:` builtins here without it (TS2591), so name the dependency rather than
// lean on @types auto-inclusion.
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// The relative path is held in a variable rather than passed as an inline
// string literal: Vite's import-analysis plugin statically pattern-matches
// `new URL("literal", import.meta.url)` and rewrites it into a dev-server
// asset URL (e.g. "http://localhost:3000/src/styles.css") under the jsdom
// test environment, which fileURLToPath() then rejects as "not scheme file".
// A variable first-argument isn't literal text, so the transform doesn't
// match and the real WHATWG URL resolution (against the real file:// module
// URL) runs instead.
const INDEX_REL = "../styles.css";
const INDEX_PATH = fileURLToPath(new URL(INDEX_REL, import.meta.url));

// Every `@import` statement, in any casing, so an unsupported form cannot be
// skipped silently. Only the one form below is inlined; anything else throws.
const ANY_IMPORT = /@import\b[^;]*;?\n?/gi;
const SUPPORTED = /^@import "(\.\.?\/[^"]+\.css)";\n?$/;

/** `path`'s CSS with each relative `@import` inlined, recursively and in order. */
export function inlineImports(path: string, importers: readonly string[] = []): string {
  if (importers.includes(path)) throw new Error(`@import cycle: ${[...importers, path].join(" -> ")}`);
  return readFileSync(path, "utf8").replace(ANY_IMPORT, (statement) => {
    const rel = SUPPORTED.exec(statement)?.[1];
    if (rel === undefined) throw new Error(`unsupported @import in ${path}: ${statement.trim()}`);
    return inlineImports(resolve(dirname(path), rel), [...importers, path]);
  });
}

/** The whole app stylesheet as one string, in cascade order. */
export function readStylesheet(): string {
  return inlineImports(INDEX_PATH);
}
