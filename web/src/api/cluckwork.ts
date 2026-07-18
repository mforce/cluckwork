// Typed wrappers over the Cluckwork JSON API (mirrors the endpoint DTOs).
import { apiDelete, apiGet, apiPost, apiPut } from "./client";

export interface EggGrade {
  id: string;
  farmId: string;
  name: string;
  gradeType: string;
  sortOrder: number;
  isSaleable: boolean;
  active: boolean;
}

export interface Flock {
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

export const listEggGrades = (params?: { includeInactive?: boolean }) =>
  apiGet<EggGrade[]>(`/egg-grades${params?.includeInactive ? "?includeInactive=true" : ""}`);

export const createEggGrade = (body: {
  name: string;
  gradeType: string;
  sortOrder: number;
  isSaleable: boolean;
}, key?: string) => apiPost<Created>("/egg-grades", body, key);

export const updateEggGrade = (id: string, body: {
  name: string;
  sortOrder: number;
  isSaleable: boolean;
}, key?: string) => apiPut<void>(`/egg-grades/${id}`, body, key);

export const deactivateEggGrade = (id: string, key?: string) =>
  apiPost<void>(`/egg-grades/${id}/deactivate`, undefined, key);

export const activateEggGrade = (id: string, key?: string) =>
  apiPost<void>(`/egg-grades/${id}/activate`, undefined, key);

export const listFlocks = (params?: { limit?: number; includeArchived?: boolean }) => {
  const q = new URLSearchParams();
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.includeArchived) q.set("includeArchived", "true");
  return apiGet<Flock[]>(`/flocks${q.size > 0 ? `?${q}` : ""}`);
};

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

export const listDailyEntries = (params?: {
  flockId?: string; from?: string; to?: string; limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.flockId) q.set("flockId", params.flockId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  const qs = q.size > 0 ? `?${q}` : "";
  return apiGet<DailyEntry[]>(`/daily-entries${qs}`);
};

export const getStock = () => apiGet<StockRow[]>("/stock");

// --- Customers & sales (#23/#24) -------------------------------------------

export interface Customer {
  id: string;
  name: string;
  phone: string;
  email: string | null;
  address: string | null;
  note: string | null;
}

export interface OrderItem {
  id: string;
  eggGradeId: string;
  quantity: number;
  unitPriceMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
}

export interface SalesOrder {
  id: string;
  customerId: string;
  referenceNumber: string;
  orderDate: string;
  status: string;
  totalMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
  voidReason: string | null;
  items: OrderItem[];
}

export const listCustomers = (params?: { limit?: number }) =>
  apiGet<Customer[]>(`/customers${params?.limit ? `?limit=${params.limit}` : ""}`);

export const createCustomer = (body: {
  name: string;
  phone: string;
  email?: string;
  address?: string;
  note?: string;
}, key?: string) => apiPost<Created>("/customers", body, key);

export const listOrders = (params?: {
  status?: string; customerId?: string; from?: string; to?: string;
  limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.status) q.set("status", params.status);
  if (params?.customerId) q.set("customerId", params.customerId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  const qs = q.size > 0 ? `?${q}` : "";
  return apiGet<SalesOrder[]>(`/sales${qs}`);
};

export const getOrder = (id: string) => apiGet<SalesOrder>(`/sales/${id}`);

export const createOrder = (body: { customerId: string; orderDate: string }, key?: string) =>
  apiPost<Created>("/sales", body, key);

export const addOrderItem = (
  orderId: string,
  body: { eggGradeId: string; quantity: number; unitPriceMinorUnits: number },
  key?: string,
) => apiPost<{ orderId: string; itemId: string }>(`/sales/${orderId}/items`, body, key);

export const updateOrderItem = (
  orderId: string,
  itemId: string,
  body: { quantity: number; unitPriceMinorUnits: number },
  key?: string,
) => apiPut<void>(`/sales/${orderId}/items/${itemId}`, body, key);

export const removeOrderItem = (orderId: string, itemId: string, key?: string) =>
  apiDelete<void>(`/sales/${orderId}/items/${itemId}`, key);

export const cancelOrder = (orderId: string, key?: string) =>
  apiPost<void>(`/sales/${orderId}/cancel`, undefined, key);

export const confirmOrder = (orderId: string, key?: string) =>
  apiPost<{ orderId: string; status: string }>(`/sales/${orderId}/confirm`, undefined, key);

// Undo of a mistaken confirm (#60): stock returns to its source lots.
export const voidOrder = (orderId: string, reason: string, key?: string) =>
  apiPost<{ salesOrderId: string; status: string }>(`/sales/${orderId}/void`, { reason }, key);

// Formats minor units per the order's snapshotted currency (JPY has 0 decimals).
export function formatMoney(minorUnits: number, currencyCode: string, minorUnit: number): string {
  const value = minorUnits / 10 ** minorUnit;
  return `${value.toFixed(minorUnit)} ${currencyCode}`;
}
