import type { TFunction } from "i18next";
import type { DailyEntry, EggGrade, Flock } from "../../api/cluckwork";
import type { usePagedList } from "../../components/usePagedList";
import { useFormat } from "../../farm/useFormat";

export type PagedEntries = ReturnType<typeof usePagedList<DailyEntry>>;

type Format = ReturnType<typeof useFormat>;

export const correctable = (e: DailyEntry) =>
  e.status === "Submitted" || e.status === "Locked" || e.status === "ManagerAdjusted";

// #396 — how many of this day's cracked/dirty eggs became stock. Read from
// the ENTRY's snapshot, never from the current grade catalog: a farm that
// switched Cracked off after recording a day must still see that day as it
// was recorded, and one that switched it on must not see past losses
// retroactively turn into stock.
//
// A draft has resolved nothing yet, so it shows an em dash rather than 0 —
// "not decided" and "decided to be a loss" are different facts, and 0 would
// state the second while the first is true.
export const conditionStock = (e: DailyEntry, fmt: Format) => {
  if (e.status === "Draft") return "—";
  return fmt.count((e.crackedGradeId ? e.crackedEggs : 0) + (e.dirtyGradeId ? e.dirtyEggs : 0));
};

const gradeName = (id: string, grades: EggGrade[]) => grades.find((g) => g.id === id)?.name ?? id.slice(0, 8);

export const gradeList = (e: DailyEntry, grades: EggGrade[], fmt: Format) =>
  e.grades.length === 0
    ? "—"
    : e.grades.map((g) => `${gradeName(g.eggGradeId, grades)} ${fmt.count(g.quantity)}`).join(", ");

// #512 US4 (T051) — a row's own name, independent of the picker/catalog
// entirely: the row-owned `flockName` the endpoint's scoped bulk read
// already resolved, or the translated unavailable label. Never the capped
// `flocks` list and never an id fragment (contracts/http-api.md: "Required
// names are never replaced with identifier fragments").
export const rowFlockName = (
  e: { flockId: string; flockName?: string | null },
  flocks: Flock[],
  t: TFunction<"history">,
) => {
  if (e.flockName !== undefined)
    return e.flockName ?? t("rowFlockUnavailable");
  return flocks.find((f) => f.id === e.flockId)?.name ?? t("rowFlockUnavailable");
};
