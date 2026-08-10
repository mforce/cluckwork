import type { EggUnitConversion } from "../api/cluckwork";

// #444 — what the Daily Entry steppers count by, resolved from: the user's
// own preference if set, else the farm's default, else "Individual" (today's
// plain +1/-1, EggUnit's always-active member — see EggUnitConversion.cs).
export interface StepperUnit {
  /** The resolved unit's code (e.g. "Tray") — what the caption names. */
  unitCode: string;
  /** The stepper's base increment — what one tap of +/− moves by. */
  eggsPerUnit: number;
}

// Falls back to Individual/1 whenever the resolved unit has no ACTIVE
// conversion on this farm (a farm mid-edit of its catalog, or a stale cached
// preference pointing at a unit since deactivated) — the stepper never
// breaks, it just goes back to counting by one, and the caption disappears
// with it rather than naming a unit the taps no longer honor.
export function resolveStepperUnit(
  farmDefault: string | undefined,
  userPreferred: string | null | undefined,
  conversions: EggUnitConversion[],
): StepperUnit {
  const unitCode = userPreferred ?? farmDefault ?? "Individual";
  const conversion = conversions.find((c) => c.unitCode === unitCode && c.active);
  return conversion !== undefined
    ? { unitCode, eggsPerUnit: conversion.eggsPerUnit }
    : { unitCode: "Individual", eggsPerUnit: 1 };
}

export function resolveStepperUnitSize(
  farmDefault: string | undefined,
  userPreferred: string | null | undefined,
  conversions: EggUnitConversion[],
): number {
  return resolveStepperUnit(farmDefault, userPreferred, conversions).eggsPerUnit;
}
