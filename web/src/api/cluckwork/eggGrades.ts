import { apiGet, apiPost, apiPut } from "../client";
import type { Created, RecordHistory } from "./shared";

export interface EggGrade extends RecordHistory {
  id: string;
  farmId: string;
  name: string;
  gradeType: string;
  sortOrder: number;
  isSaleable: boolean;
  /**
   * #396 — "Manual" | "Cracked" | "Dirty". Which Daily Entry input feeds this
   * grade. Not inferable from `name` (renameable) or `gradeType` (a farm can
   * have many Quality grades, only one of which is the Cracked counter's).
   *
   * Used to keep the two counter-fed grades out of the Grading pane. That is an
   * affordance only — the server refuses a manual line naming one regardless.
   */
  dailyEntryKind: string;
  active: boolean;
  /**
   * #911 — the point at which Stock and the Dashboard warn, in eggs. `null`
   * means this grade raises no low-stock warning. Owner-only to change.
   */
  lowStockFloor: number | null;
}

export const listEggGrades = (params?: { includeInactive?: boolean }) =>
  apiGet<EggGrade[]>(`/egg-grades${params?.includeInactive ? "?includeInactive=true" : ""}`);

// #911 — `lowStockFloor` is a full replacement, not a patch: the value sent is
// the floor the grade ends up with, and null clears it. A caller that is not an
// Owner must send the grade's CURRENT floor, which is what the Grades dialog
// does for a Manager; the API refuses a floor that moves.
export const createEggGrade = (body: {
  name: string;
  gradeType: string;
  sortOrder: number;
  isSaleable: boolean;
  lowStockFloor: number | null;
}, key?: string) => apiPost<Created>("/egg-grades", body, key);

export const updateEggGrade = (id: string, body: {
  name: string;
  sortOrder: number;
  isSaleable: boolean;
  lowStockFloor: number | null;
}, key?: string) => apiPut<void>(`/egg-grades/${id}`, body, key);

export const deactivateEggGrade = (id: string, key?: string) =>
  apiPost<void>(`/egg-grades/${id}/deactivate`, undefined, key);

export const activateEggGrade = (id: string, key?: string) =>
  apiPost<void>(`/egg-grades/${id}/activate`, undefined, key);
