import { apiGet, apiPost } from "../client";

export interface StockRow {
  eggGradeId: string;
  gradeName: string;
  available: number;
  restricted: number;
  /** #911 — the grade's floor in eggs, `null` when it has none. */
  lowStockFloor: number | null;
  /** #911 — server-computed from `available` alone; restricted eggs cannot be sold. */
  belowFloor: boolean;
}

export const getStock = () => apiGet<StockRow[]>("/stock");

// --- Egg movement ledger (#101) ---

export interface EggLotRow {
  id: string;
  eggGradeId: string;
  productionDate: string;
  quantityProduced: number;
  quantityAvailable: number;
  restrictedUntil: string | null;
  dailyEntryId: string | null;
}

export interface EggMovementRow {
  id: string;
  movementType: string;
  quantityDelta: number;
  referenceType: string;
  referenceId: string;
  reason: string | null;
  createdAtUtc: string;
}

export const listEggLots = (params?: {
  gradeId?: string; from?: string; to?: string; limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.gradeId) q.set("gradeId", params.gradeId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<EggLotRow[]>(`/stock/lots${q.size > 0 ? `?${q}` : ""}`);
};

export const listEggLotMovements = (lotId: string) =>
  apiGet<EggMovementRow[]>(`/stock/lots/${lotId}/movements`);

// #406 — standalone stock correction against one lot (Owner/Manager only).
// quantityDelta is signed: negative for Discard/InternalUse, either sign for
// Reconciliation. The response carries the lot's new balance so the screen
// can report it without a second read.
export interface EggLotMovementResult {
  movementId: string;
  eggLotId: string;
  movementType: string;
  quantityDelta: number;
  reason: string | null;
  createdAtUtc: string;
  quantityAvailable: number;
  version: number;
}

export const recordEggLotMovement = (lotId: string, body: {
  movementType: string; quantityDelta: number; reason: string;
}, key?: string) => apiPost<EggLotMovementResult>(`/stock/lots/${lotId}/movements`, body, key);
