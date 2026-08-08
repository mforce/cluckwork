import type { EggUnitConversion } from "../api/cluckwork";

// #444 — the Daily Entry stepper's base increment: the user's own preference
// wins, else the farm's default, else "Individual" (today's plain +1/-1,
// EggUnit's always-active member — see EggUnitConversion.cs). Falls back to 1
// whenever the resolved unit has no ACTIVE conversion on this farm (a farm
// mid-edit of its catalog, or a stale cached preference pointing at a unit
// since deactivated) — the stepper never breaks, it just stops accelerating.
export function resolveStepperUnitSize(
  farmDefault: string | undefined,
  userPreferred: string | null | undefined,
  conversions: EggUnitConversion[],
): number {
  const unitCode = userPreferred ?? farmDefault ?? "Individual";
  const conversion = conversions.find((c) => c.unitCode === unitCode && c.active);
  return conversion?.eggsPerUnit ?? 1;
}
