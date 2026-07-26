import { describe, it, expect } from "vitest";
import { en } from "./en";
import { es } from "./es";
import { tl } from "./tl";

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

const enKeys = new Set(flattenKeys(en));

describe.each([
  ["es", es],
  ["tl", tl],
])("%s catalog parity with en (#182)", (_name, pack) => {
  const packKeys = new Set(flattenKeys(pack as Record<string, unknown>));

  it("has exactly the same key set as en (no missing, no extra)", () => {
    const missing = [...enKeys].filter((k) => !packKeys.has(k));
    const extra = [...packKeys].filter((k) => !enKeys.has(k));
    expect(missing).toEqual([]);
    expect(extra).toEqual([]);
  });

  it("has a non-empty string value for every key", () => {
    for (const [path, value] of flattenEntries(pack as Record<string, unknown>)) {
      expect(typeof value, `${path} should be a string`).toBe("string");
      expect((value as string).length, `${path} should be non-empty`).toBeGreaterThan(0);
    }
  });
});
