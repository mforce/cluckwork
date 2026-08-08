import { describe, it, expect } from "vitest";
import { resolveStepperUnitSize } from "./stepperUnit";
import type { EggUnitConversion } from "../api/cluckwork";

const CONVERSIONS: EggUnitConversion[] = [
  { id: "c1", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 0 },
  { id: "c2", unitCode: "Dozen", eggsPerUnit: 12, active: true, version: 0 },
  { id: "c3", unitCode: "Tray", eggsPerUnit: 30, active: true, version: 0 },
  { id: "c4", unitCode: "Case", eggsPerUnit: 360, active: false, version: 0 },
];

// #444 — user override ?? farm default ?? Individual, and 1 whenever the
// resolved unit has no active conversion.
describe("resolveStepperUnitSize", () => {
  it("resolves the farm default when the user has no override", () =>
    expect(resolveStepperUnitSize("Tray", null, CONVERSIONS)).toBe(30));

  it("lets the user's own preference win over the farm default", () =>
    expect(resolveStepperUnitSize("Tray", "Dozen", CONVERSIONS)).toBe(12));

  it("falls back to Individual when neither is set", () =>
    expect(resolveStepperUnitSize(undefined, null, CONVERSIONS)).toBe(1));

  it("falls back to 1 when the resolved unit's conversion is inactive", () =>
    // Case exists but is deactivated — the stepper must not silently keep
    // counting by 360 off a unit the farm switched off.
    expect(resolveStepperUnitSize("Case", null, CONVERSIONS)).toBe(1));

  it("falls back to 1 when the conversions have not loaded yet", () =>
    expect(resolveStepperUnitSize("Tray", null, [])).toBe(1));
});
