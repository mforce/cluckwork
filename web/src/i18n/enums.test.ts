import { describe, it, expect } from "vitest";
import { en } from "./en";
import { STATUS_VALUES } from "../components/StatusBadge";
import { ENUMS, statusLabel } from "./enums";

// The enums module is the ONLY sanctioned way to turn a closed-vocabulary wire
// value into display text (#182, Task 4). These tests are the runtime backstop
// for the compile-time guarantees in enums.ts: they fail loudly if a union
// member ever points at a key that is missing from (or blank in) en.enums, and
// if StatusBadge's status vocabulary ever outgrows the labels that cover it.
//
// en.enums is a FLAT "family.RawValue" -> string map (keySeparator:false); the
// key maps carry the fully-qualified "enums:family.RawValue" i18next key, so we
// strip the leading "enums:" namespace to index en.enums directly.
const enums = en.enums as Record<string, string>;

function flatKey(qualified: string): string {
  return qualified.replace(/^enums:/, "");
}

describe("enums module (#182)", () => {
  // Every family, every union member -> a real, non-empty en.enums entry.
  for (const [family, def] of Object.entries(ENUMS)) {
    describe(`${family} family`, () => {
      it("maps every value to a real enums:* key present in en.ts", () => {
        for (const value of def.values) {
          const qualified = (def.keys as Record<string, string>)[value];
          expect(qualified, `${family}.${value} should have a key mapping`).toBeTruthy();
          expect(qualified.startsWith("enums:"), `${qualified} should be enums-namespaced`).toBe(
            true,
          );
          const key = flatKey(qualified);
          expect(
            Object.prototype.hasOwnProperty.call(enums, key),
            `en.enums should contain "${key}"`,
          ).toBe(true);
          expect(typeof enums[key], `en.enums["${key}"] should be a string`).toBe("string");
          expect(enums[key].length, `en.enums["${key}"] should be non-empty`).toBeGreaterThan(0);
        }
      });

      it("has no key mapping without a matching union value (no drift)", () => {
        const values = new Set<string>(def.values as readonly string[]);
        for (const value of Object.keys(def.keys)) {
          expect(values.has(value), `${family}.keys has stray "${value}"`).toBe(true);
        }
      });

      it("labels every value with its en.enums string", () => {
        for (const value of def.values) {
          const key = flatKey((def.keys as Record<string, string>)[value]);
          expect((def.label as (v: string) => string)(value)).toBe(enums[key]);
        }
      });
    });
  }

  // Coupling guard: StatusBadge decides which raw status values render as pills;
  // enums:status.* must label ALL of them. A new status added to STATUS_VALUES
  // without a label fails here (and typecheck) rather than rendering a raw key.
  describe("status coupling with StatusBadge", () => {
    it("covers every STATUS_VALUES member with an enums:status.* key", () => {
      for (const value of STATUS_VALUES) {
        const key = `status.${value}`;
        expect(
          Object.prototype.hasOwnProperty.call(enums, key),
          `en.enums should label status "${value}" ("${key}")`,
        ).toBe(true);
      }
    });

    it("statusLabel renders ManagerAdjusted as 'Adjusted' (raw != display)", () => {
      expect(statusLabel("ManagerAdjusted")).toBe("Adjusted");
    });
  });
});
