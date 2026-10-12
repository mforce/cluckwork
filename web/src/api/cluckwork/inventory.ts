import { apiGet, apiPost, apiPut } from "../client";
import type { Created } from "./shared";

// --- Feed & inventory (#66) ---

export interface InventoryItem {
  id: string;
  farmId: string;
  name: string;
  category: string;
  unit: string;
  defaultCostMinorUnits: number | null;
  defaultCostCurrencyCode: string | null;
  defaultCostCurrencyMinorUnit: number | null;
  quantityOnHand: number;
  active: boolean;
}

export interface InventoryLot {
  id: string;
  inventoryItemId: string;
  receivedDate: string;
  lotNumber: string | null;
  expiryDate: string | null;
  quantityReceived: number;
  quantityAvailable: number;
  unitCostMinorUnits: number;
  unitCostCurrencyCode: string;
  unitCostCurrencyMinorUnit: number;
}

export interface InventoryMovement {
  id: string;
  inventoryItemId: string;
  inventoryLotId: string | null;
  date: string;
  type: string;
  quantityDelta: number;
  unit: string;
  flockId: string | null;
  note: string | null;
  referenceType: string | null;
  referenceId: string | null;
}

export const listInventoryItems = (params?: { includeInactive?: boolean }) =>
  apiGet<InventoryItem[]>(`/inventory/items${params?.includeInactive ? "?includeInactive=true" : ""}`);

export const createInventoryItem = (body: {
  name: string; category: string; unit: string; defaultUnitCostMinorUnits: number | null;
}, key?: string) => apiPost<Created>("/inventory/items", body, key);

export const updateInventoryItem = (id: string, body: {
  name: string; unit: string; defaultUnitCostMinorUnits: number | null;
}, key?: string) => apiPut<void>(`/inventory/items/${id}`, body, key);

export const activateInventoryItem = (id: string, key?: string) =>
  apiPost<void>(`/inventory/items/${id}/activate`, undefined, key);

export const deactivateInventoryItem = (id: string, key?: string) =>
  apiPost<void>(`/inventory/items/${id}/deactivate`, undefined, key);

export const recordInventoryPurchase = (itemId: string, body: {
  receivedDate: string; quantity: number; unitCostMinorUnits: number | null;
  lotNumber?: string; expiryDate?: string; note?: string;
}, key?: string) => apiPost<{ lotId: string }>(`/inventory/items/${itemId}/purchases`, body, key);

export const listInventoryLots = (itemId: string) =>
  apiGet<InventoryLot[]>(`/inventory/items/${itemId}/lots`);

export const recordFeedUsage = (itemId: string, body: {
  flockId: string; date: string; quantity: number; note?: string;
}, key?: string) => apiPost<{
  feedUsageId: string; quantityUsed: number; estimatedCostMinorUnits: number; currencyCode: string;
}>(`/inventory/items/${itemId}/usage`, body, key);

// #446 — the feed-usage history the server has always exposed but no screen
// read until the /feed page. dailyEntryId is best-effort record-time
// provenance (null when the day's entry didn't exist yet; never backfilled).
export interface FeedUsage {
  id: string;
  flockId: string;
  inventoryItemId: string;
  date: string;
  quantity: number;
  unit: string;
  estimatedCostMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
  note: string | null;
  dailyEntryId: string | null;
  // #512 US4 (T046/T050) — the flock's CURRENT name, carried by the row so
  // it renders independently of the page's capped picker results. Always
  // present in the response, but the value itself can be null when the
  // referenced flock fell out of the caller's tenant/flock scope between the
  // page read and the bulk reference read (see contracts/http-api.md's
  // "defensive null on a non-null id").
  flockName: string | null;
}

export const listFeedUsage = (params?: {
  flockId?: string; from?: string; to?: string; limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.flockId) q.set("flockId", params.flockId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  const qs = q.size > 0 ? `?${q}` : "";
  return apiGet<FeedUsage[]>(`/inventory/usage${qs}`);
};

// Correction path: compensating ledger row against a specific lot. Type
// "Adjustment" (signed) or "Discard" (negative write-off); reason required.
export const recordInventoryAdjustment = (itemId: string, body: {
  inventoryLotId: string; date: string; type: string; quantityDelta: number; reason: string;
}, key?: string) => apiPost<{ movementId: string }>(`/inventory/items/${itemId}/adjustments`, body, key);

// --- Water usage (#67) ---

export interface WaterUsage {
  id: string;
  flockId: string;
  // #512 US4 (T046/T050) — row-owned flock name — survives beyond the capped
  // flock page load, so corrections of historic/archived flocks show the
  // real name, not an ID. Always present, but the VALUE can be null (a
  // flock that left the caller's tenant/flock scope between reads — see
  // contracts/http-api.md's "defensive null on a non-null id"); the wire
  // record itself is `string?`, so the frontend type must not claim
  // non-null.
  flockName: string | null;
  date: string;
  quantity: number;
  unit: string;
  source: string;
  meterStart: number | null;
  meterEnd: number | null;
  note: string | null;
  version: number;
  // #446 — best-effort record-time provenance; corrections never change it.
  dailyEntryId: string | null;
}

export const listWaterUsage = (params?: {
  flockId?: string; from?: string; to?: string; limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.flockId) q.set("flockId", params.flockId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  const qs = q.size > 0 ? `?${q}` : "";
  return apiGet<WaterUsage[]>(`/water-usage${qs}`);
};

export const recordWaterUsage = (body: {
  flockId: string; date: string; quantity?: number; unit?: string; source: string;
  meterStart?: number; meterEnd?: number; note?: string;
}, key?: string) => apiPost<Created>("/water-usage", body, key);

// version = the base Version the row was loaded with; a stale one gets a 409
// instead of silently overwriting someone else's correction.
export const updateWaterUsage = (id: string, body: {
  version: number; quantity?: number; unit?: string; source: string;
  meterStart?: number; meterEnd?: number; note?: string;
}, key?: string) => apiPut<void>(`/water-usage/${id}`, body, key);

export const listInventoryMovements = (itemId: string, params?: { limit?: number; offset?: number }) => {
  const q = new URLSearchParams();
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  const qs = q.size > 0 ? `?${q}` : "";
  return apiGet<InventoryMovement[]>(`/inventory/items/${itemId}/movements${qs}`);
};
