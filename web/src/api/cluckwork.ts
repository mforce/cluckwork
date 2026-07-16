// Typed wrappers over the Cluckwork JSON API (mirrors the endpoint DTOs).
import { apiGet, apiPost } from "./client";

export interface EggGrade {
  id: string;
  farmId: string;
  name: string;
  gradeType: string;
  sortOrder: number;
  isSaleable: boolean;
}

export interface Flock {
  id: string;
  farmId: string;
  houseId: string;
  name: string;
  breed: string;
  placementDate: string;
  initialCount: number;
  status: string;
}

export interface GradeLine {
  eggGradeId: string;
  quantity: number;
}

export interface DailyEntry {
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
  grades: GradeLine[];
}

export interface StockRow {
  eggGradeId: string;
  gradeName: string;
  available: number;
  restricted: number;
}

export interface Created {
  id: string;
}

export const listEggGrades = () => apiGet<EggGrade[]>("/egg-grades");

export const listFlocks = () => apiGet<Flock[]>("/flocks");

export const createFlock = (body: {
  name: string;
  breed: string;
  placementDate: string;
  initialCount: number;
}) => apiPost<Created>("/flocks", body);

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
}) => apiPost<Created>("/daily-entries", body);

export const submitDailyEntry = (id: string) =>
  apiPost<{ id: string; status: string; eggLotIds: string[] }>(`/daily-entries/${id}/submit`);

export const listDailyEntries = (params?: { flockId?: string; limit?: number }) => {
  const q = new URLSearchParams();
  if (params?.flockId) q.set("flockId", params.flockId);
  if (params?.limit) q.set("limit", String(params.limit));
  const qs = q.size > 0 ? `?${q}` : "";
  return apiGet<DailyEntry[]>(`/daily-entries${qs}`);
};

export const getStock = () => apiGet<StockRow[]>("/stock");
