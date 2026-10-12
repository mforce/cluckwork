import { apiGet, apiPost, apiPut } from "../client";
import type { Created, RecordHistory } from "./shared";

export interface Flock extends RecordHistory {
  id: string;
  farmId: string;
  houseId: string;
  name: string;
  breed: string;
  placementDate: string;
  initialCount: number;
  currentBirds: number;
  status: string;
}

export interface BirdMovement {
  id: string;
  flockId: string;
  date: string;
  type: string;
  quantity: number;
  note: string | null;
}

// #512 — the three wire values `GET /flocks` accepts for `eligibility` (exact,
// lowercase; unknown values 400 server-side). Omitted means active-and-depleted.
// `includeArchived` is its legacy alias: `true` means `all` ONLY when
// `eligibility` is absent, and the server rejects supplying both keys — so this
// client refuses to serialize the combination at all.
export type FlockEligibility = "active" | "active-and-depleted" | "all";

export const listFlocks = (params?: {
  limit?: number;
  offset?: number;
  search?: string;
  eligibility?: FlockEligibility;
  includeArchived?: boolean;
}, signal?: AbortSignal) => {
  const q = new URLSearchParams();
  if (params?.search) q.set("search", params.search);
  if (params?.eligibility) q.set("eligibility", params.eligibility);
  else if (params?.includeArchived) q.set("includeArchived", "true");
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<Flock[]>(`/flocks${q.toString() !== "" ? `?${q}` : ""}`, signal);
};

// #512 — exact resolution: the full flock or a 404. The picker adapters map a
// 404/denial to the page's explicit unavailable state; they never substitute
// the first discovery result.
export const getFlock = (id: string) => apiGet<Flock>(`/flocks/${id}`);

export const updateFlock = (id: string, body: {
  name: string;
  breed: string;
  placementDate: string;
  initialCount: number;
}, key?: string) => apiPut<void>(`/flocks/${id}`, body, key);

export const depleteFlock = (id: string, key?: string) =>
  apiPost<void>(`/flocks/${id}/deplete`, undefined, key);

export const listBirdMovements = (flockId: string, params?: { limit?: number; offset?: number }) => {
  const q = new URLSearchParams();
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<BirdMovement[]>(`/flocks/${flockId}/movements${q.size > 0 ? `?${q}` : ""}`);
};

export const recordBirdMovement = (flockId: string, body: {
  date: string;
  type: string;
  quantity: number;
  note?: string;
}, key?: string) => apiPost<Created>(`/flocks/${flockId}/movements`, body, key);

export const archiveFlock = (id: string, key?: string) =>
  apiPost<void>(`/flocks/${id}/archive`, undefined, key);

export const reactivateFlock = (id: string, key?: string) =>
  apiPost<void>(`/flocks/${id}/reactivate`, undefined, key);

export const createFlock = (body: {
  name: string;
  breed: string;
  placementDate: string;
  initialCount: number;
}, key?: string) => apiPost<Created>("/flocks", body, key);
