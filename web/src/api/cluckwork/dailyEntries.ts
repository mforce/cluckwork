import { apiGet, apiPost } from "../client";
import type { Created, RecordHistory } from "./shared";

export interface GradeLine {
  eggGradeId: string;
  quantity: number;
}

// version = the base an admin correction must send back (stale → 409);
// adjustedFrom = audit snapshot of the values the last adjust replaced.
export interface DailyEntry extends RecordHistory {
  id: string;
  farmId: string;
  houseId: string;
  flockId: string;
  date: string;
  status: string;
  totalEggs: number;
  crackedEggs: number;
  dirtyEggs: number;
  discardedEggs: number;
  mortalityCount: number;
  /**
   * #396 — which grade each condition counter resolved to when this entry
   * became official; null when that condition was a loss (and on any draft,
   * which has not resolved yet — tell the two apart by `status`).
   *
   * The only way to know whether a past day's cracked eggs became stock: the
   * current grade catalog cannot answer it, because the farm may have changed
   * a grade's saleability since.
   */
  crackedGradeId: string | null;
  dirtyGradeId: string | null;
  grades: GradeLine[];
  version: number;
  adjustReason: string | null;
  /**
   * #512 US4 — the flock's CURRENT name, carried by the entry itself so a
   * row renders independently of the page's capped picker results.
   * Additive: older servers omit it; callers fall back to their own
   * resolution.
   */
  flockName?: string | null;
  /** #512 US4 — the flock's CURRENT status (archived flocks are still named). */
  flockStatus?: string | null;
  voidReason: string | null;
  lockedAtUtc: string | null;
  adjustedFrom: {
    totalEggs: number; crackedEggs: number; dirtyEggs: number;
    discardedEggs: number; mortalityCount: number; grades: GradeLine[];
  } | null;
}

export const recordDailyEntry = (body: {
  farmId: string;
  houseId: string;
  flockId: string;
  date: string;
  totalEggs: number;
  crackedEggs: number;
  dirtyEggs: number;
  discardedEggs: number;
  mortalityCount: number;
  grades?: GradeLine[];
}, key?: string) => apiPost<Created>("/daily-entries", body, key);

export const submitDailyEntry = (id: string, key?: string) =>
  apiPost<{ id: string; status: string; eggLotIds: string[] }>(`/daily-entries/${id}/submit`, undefined, key);

export const getDailyEntry = (id: string) => apiGet<DailyEntry>(`/daily-entries/${id}`);

// Admin-only (#69/#73): correct a submitted/locked entry. Stock and the bird
// ledger reconcile server-side; version is the base the entry was loaded at.
export const adjustDailyEntry = (id: string, body: {
  version: number; totalEggs: number; crackedEggs: number; dirtyEggs: number;
  discardedEggs: number; mortalityCount: number; reason: string; grades?: GradeLine[];
}, key?: string) =>
  apiPost<{ id: string; status: string; version: number }>(`/daily-entries/${id}/adjust`, body, key);

// Admin-only (#69/#73): undo a whole submitted entry (refused once eggs sold).
export const voidDailyEntry = (id: string, body: { version: number; reason: string }, key?: string) =>
  apiPost<{ id: string; status: string; version: number }>(`/daily-entries/${id}/void`, body, key);

export const listDailyEntries = (params?: {
  flockId?: string; from?: string; to?: string; limit?: number; offset?: number;
}, signal?: AbortSignal) => {
  const q = new URLSearchParams();
  if (params?.flockId) q.set("flockId", params.flockId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  const qs = q.size > 0 ? `?${q}` : "";
  return apiGet<DailyEntry[]>(`/daily-entries${qs}`, signal);
};
