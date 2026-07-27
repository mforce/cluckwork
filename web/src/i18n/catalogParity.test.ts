import { describe, it, expect } from "vitest";
import { en } from "./en";
import { RESOURCES, TRANSLATED_NAMESPACES } from "./index";

// Completeness guard (#182, English-first model): a missing key in a
// TRANSLATED namespace silently falls back to English (fallbackLng), which is
// easy to miss in review, and an extra key is dead weight that will never be
// read. Only namespaces in TRANSLATED_NAMESPACES are held to this — a screen
// added after the English-first cutover ships to en.ts ONLY, and es/tl are
// allowed to simply not have that namespace at all (see
// translations-status.ts).
//
// The catalog is exactly namespace -> FLAT string key (keySeparator:false in
// index.ts, see the "catalog shape" describe below), so within one namespace
// there is nothing to recurse into: Object.keys/Object.entries directly on
// the namespace object IS the full key set.
function namespaceKeys(catalog: Record<string, unknown>, ns: string): string[] {
  const nsValue = catalog[ns];
  if (nsValue == null || typeof nsValue !== "object") return [];
  return Object.keys(nsValue as Record<string, unknown>);
}

function namespaceEntries(catalog: Record<string, unknown>, ns: string): [string, unknown][] {
  const nsValue = catalog[ns];
  if (nsValue == null || typeof nsValue !== "object") return [];
  return Object.entries(nsValue as Record<string, unknown>);
}

// {{placeholder}} interpolation tokens and the handful of HTML-ish tags used by
// <Trans> (e.g. <strong>…</strong>). A translation that drops/renames
// "{{ref}}" or swaps <strong> for <b> would otherwise render silently wrong —
// interpolation just leaves the literal token in the string, and react-i18next
// only maps tags it's told to expect. fallbackLng masks neither case in
// review, so parity here is the only thing that catches it.
const PLACEHOLDER_RE = /\{\{\s*\w+\s*\}\}/g;
const TAG_RE = /<\/?[a-z]+>/g;

function extractTokens(value: string, re: RegExp): string[] {
  return [...value.matchAll(re)].map((m) => m[0]).sort();
}

// Iterate RESOURCES itself (not a hardcoded ["es", "tl"]) so a future pack
// added to RESOURCES (src/i18n/index.ts) is automatically parity-checked here
// without editing this file.
const otherPacks = Object.entries(RESOURCES).filter(([name]) => name !== "en") as [
  string,
  Record<string, unknown>,
][];

describe("TRANSLATED_NAMESPACES sanity (#182)", () => {
  it("every translated namespace actually exists (non-empty) in en", () => {
    for (const ns of TRANSLATED_NAMESPACES) {
      expect(namespaceKeys(en, ns).length, `en.${ns} should have keys`).toBeGreaterThan(0);
    }
  });
});

describe.each(otherPacks)("%s catalog parity with en (#182)", (_name, pack) => {
  describe.each(TRANSLATED_NAMESPACES)("%s namespace", (ns) => {
    const enKeys = new Set(namespaceKeys(en, ns));
    const enEntries = new Map(namespaceEntries(en, ns));

    it("has exactly the same key set as en (no missing, no extra)", () => {
      const packKeys = new Set(namespaceKeys(pack, ns));
      const missing = [...enKeys].filter((k) => !packKeys.has(k));
      const extra = [...packKeys].filter((k) => !enKeys.has(k));
      expect(missing).toEqual([]);
      expect(extra).toEqual([]);
    });

    it("has a non-empty string value for every key", () => {
      for (const [key, value] of namespaceEntries(pack, ns)) {
        expect(typeof value, `${ns}:${key} should be a string`).toBe("string");
        expect((value as string).length, `${ns}:${key} should be non-empty`).toBeGreaterThan(0);
      }
    });

    it("preserves every {{placeholder}} and HTML-ish tag from en, per key", () => {
      for (const [key, packValue] of namespaceEntries(pack, ns)) {
        const enValue = enEntries.get(key);
        if (typeof enValue !== "string" || typeof packValue !== "string") continue;

        const enPlaceholders = extractTokens(enValue, PLACEHOLDER_RE);
        const packPlaceholders = extractTokens(packValue, PLACEHOLDER_RE);
        expect(
          packPlaceholders,
          `${ns}:${key} should have the same {{placeholders}} as en`,
        ).toEqual(enPlaceholders);

        const enTags = extractTokens(enValue, TAG_RE);
        const packTags = extractTokens(packValue, TAG_RE);
        expect(packTags, `${ns}:${key} should have the same tags as en`).toEqual(enTags);
      }
    });
  });

  // English-first guard: es/tl must not carry a namespace that isn't in the
  // allowlist. Without this, a machine-drafted namespace for a
  // not-yet-translated (English-only) screen could slip in unreviewed and
  // silently go stale, since nothing else here would ever check it again.
  it("has no namespace outside TRANSLATED_NAMESPACES", () => {
    const allowed = new Set<string>(TRANSLATED_NAMESPACES);
    const extraNamespaces = Object.keys(pack).filter((ns) => !allowed.has(ns));
    expect(extraNamespaces).toEqual([]);
  });
});

// Shape guard (#182): the catalog must be exactly namespace -> flat string
// key. A nested object one level deeper wouldn't resolve at runtime at all
// (keySeparator:false means the whole post-namespace string is one literal
// key — react-i18next would render it as a raw missing key, not descend into
// it), so this must compare DIRECT own keys per namespace rather than a
// recursive flatten: a recursive flatten would happily walk into the nested
// object and hide the very bug this test exists to catch. Runs over every
// catalog (en included, and every namespace including ones outside
// TRANSLATED_NAMESPACES) since the flat-shape invariant applies regardless of
// translation status.
describe.each(Object.entries(RESOURCES) as [string, Record<string, unknown>][])(
  "%s catalog shape (#182)",
  (_name, catalog) => {
    it("has only direct string leaves under every namespace", () => {
      for (const [ns, nsValue] of Object.entries(catalog)) {
        expect(
          nsValue !== null && typeof nsValue === "object" && !Array.isArray(nsValue),
          `${ns} should be a namespace object`,
        ).toBe(true);

        for (const [key, value] of Object.entries(nsValue as Record<string, unknown>)) {
          expect(
            typeof value,
            `${ns}:${key} should be a direct string leaf, not a nested object`,
          ).toBe("string");
        }
      }
    });
  },
);
