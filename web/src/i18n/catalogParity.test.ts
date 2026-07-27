import { describe, it, expect } from "vitest";
import { en } from "./en";
import { RESOURCES } from "./index";

// Completeness guard for every language pack (#182): a missing key silently
// falls back to English (fallbackLng), which is easy to miss in review, and an
// extra key is dead weight that will never be read. Recursing by namespace
// (not a single flat walk) keeps this correct even if a namespace ever nests
// further than one level.
function flattenKeys(obj: Record<string, unknown>, prefix = ""): string[] {
  return Object.entries(obj).flatMap(([key, value]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      return flattenKeys(value as Record<string, unknown>, path);
    }
    return [path];
  });
}

function flattenEntries(obj: Record<string, unknown>, prefix = ""): [string, unknown][] {
  return Object.entries(obj).flatMap(([key, value]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      return flattenEntries(value as Record<string, unknown>, path);
    }
    return [[path, value]] as [string, unknown][];
  });
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

const enKeys = new Set(flattenKeys(en));
const enEntries = new Map(flattenEntries(en));

// Iterate RESOURCES itself (not a hardcoded ["es", "tl"]) so a future pack
// added to RESOURCES (src/i18n/index.ts) is automatically parity-checked here
// without editing this file.
const otherPacks = Object.entries(RESOURCES).filter(([name]) => name !== "en") as [
  string,
  Record<string, unknown>,
][];

describe.each(otherPacks)("%s catalog parity with en (#182)", (_name, pack) => {
  const packKeys = new Set(flattenKeys(pack));

  it("has exactly the same key set as en (no missing, no extra)", () => {
    const missing = [...enKeys].filter((k) => !packKeys.has(k));
    const extra = [...packKeys].filter((k) => !enKeys.has(k));
    expect(missing).toEqual([]);
    expect(extra).toEqual([]);
  });

  it("has a non-empty string value for every key", () => {
    for (const [path, value] of flattenEntries(pack)) {
      expect(typeof value, `${path} should be a string`).toBe("string");
      expect((value as string).length, `${path} should be non-empty`).toBeGreaterThan(0);
    }
  });

  it("preserves every {{placeholder}} and HTML-ish tag from en, per key", () => {
    for (const [path, packValue] of flattenEntries(pack)) {
      const enValue = enEntries.get(path);
      if (typeof enValue !== "string" || typeof packValue !== "string") continue;

      const enPlaceholders = extractTokens(enValue, PLACEHOLDER_RE);
      const packPlaceholders = extractTokens(packValue, PLACEHOLDER_RE);
      expect(packPlaceholders, `${path} should have the same {{placeholders}} as en`).toEqual(
        enPlaceholders,
      );

      const enTags = extractTokens(enValue, TAG_RE);
      const packTags = extractTokens(packValue, TAG_RE);
      expect(packTags, `${path} should have the same tags as en`).toEqual(enTags);
    }
  });
});
