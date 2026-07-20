// Typed wrappers over the Cluckwork JSON API (mirrors the endpoint DTOs).
import { apiDelete, apiGet, apiGetBlob, apiPost, apiPut } from "./client";

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

// version = the base an admin correction must send back (stale → 409);
// adjustedFrom = audit snapshot of the values the last adjust replaced.
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
  version: number;
  adjustReason: string | null;
  voidReason: string | null;
  lockedAtUtc: string | null;
  adjustedFrom: {
    totalEggs: number; crackedEggs: number; dirtyEggs: number;
    discardedEggs: number; mortalityCount: number; grades: GradeLine[];
  } | null;
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

export const listEggLots = (params?: { gradeId?: string; limit?: number; offset?: number }) => {
  const q = new URLSearchParams();
  if (params?.gradeId) q.set("gradeId", params.gradeId);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<EggLotRow[]>(`/stock/lots${q.size > 0 ? `?${q}` : ""}`);
};

export const listEggLotMovements = (lotId: string) =>
  apiGet<EggMovementRow[]>(`/stock/lots/${lotId}/movements`);

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
  productId: string;
  eggGradeId: string;
  unit: string;
  baseUnitFactor: number;
  quantity: number;
  quantityBase: number;
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
  body: { productId: string; quantity: number; unit?: string; unitPriceMinorUnits?: number },
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

// --- Account ---

export interface Account {
  id: string;
  name: string;
  currencyCode: string;
  currencyMinorUnit: number;
}

// Clients need the account currency to parse money input correctly — a JPY
// amount has 0 decimals, so assuming 2 silently multiplies costs by 100.
export const getAccount = () => apiGet<Account>("/account");

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

// Correction path: compensating ledger row against a specific lot. Type
// "Adjustment" (signed) or "Discard" (negative write-off); reason required.
export const recordInventoryAdjustment = (itemId: string, body: {
  inventoryLotId: string; date: string; type: string; quantityDelta: number; reason: string;
}, key?: string) => apiPost<{ movementId: string }>(`/inventory/items/${itemId}/adjustments`, body, key);

// --- Water usage (#67) ---

export interface WaterUsage {
  id: string;
  flockId: string;
  date: string;
  quantity: number;
  unit: string;
  source: string;
  meterStart: number | null;
  meterEnd: number | null;
  note: string | null;
  version: number;
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

// --- Users (#73, admin-only endpoints) ---

export interface User {
  id: string;
  email: string;
  displayName: string | null;
  role: string; // "Admin" | "Worker"
}

export const listUsers = () => apiGet<User[]>("/users");

export const createUser = (body: {
  email: string; password: string; role: string;
}, key?: string) => apiPost<Created>("/users", body, key);

// Formats minor units per the order's snapshotted currency (JPY has 0 decimals).
export function formatMoney(minorUnits: number, currencyCode: string, minorUnit: number): string {
  const value = minorUnits / 10 ** minorUnit;
  return `${value.toFixed(minorUnit)} ${currencyCode}`;
}

// --- Audit trail (#93, admin-only, read-only) ---

export interface AuditEvent {
  id: string;
  occurredAtUtc: string;
  actorEmail: string;
  action: string;
  entityType: string;
  entityId: string;
  reason: string | null;
  detailsJson: string | null;
}

export const listAuditEvents = (params?: {
  action?: string; entityId?: string; from?: string; to?: string;
  limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.action) q.set("action", params.action);
  if (params?.entityId) q.set("entityId", params.entityId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<AuditEvent[]>(`/audit${q.size > 0 ? `?${q}` : ""}`);
};

// --- Product catalog (#97) ---

export interface Product {
  id: string;
  name: string;
  productType: string;
  defaultUnit: string;
  defaultPriceMinorUnits: number | null;
  currencyCode: string;
  currencyMinorUnit: number;
  eggGradeId: string | null;
  notes: string | null;
  active: boolean;
  version: number;
}

export interface EggUnitConversion {
  id: string;
  unitCode: string;
  eggsPerUnit: number;
  active: boolean;
  version: number;
}

export const listProducts = (params?: { includeInactive?: boolean }) =>
  apiGet<Product[]>(`/products${params?.includeInactive ? "?includeInactive=true" : ""}`);

export const createProduct = (body: {
  name: string;
  productType: string;
  defaultUnit: string;
  defaultPriceMinorUnits: number | null;
  eggGradeId: string;
  notes: string | null;
}, key?: string) => apiPost<Created>("/products", body, key);

export const updateProduct = (id: string, body: {
  name: string;
  defaultUnit: string;
  defaultPriceMinorUnits: number | null;
  eggGradeId: string;
  notes: string | null;
}, key?: string) => apiPut<void>(`/products/${id}`, body, key);

export const deactivateProduct = (id: string, key?: string) =>
  apiPost<void>(`/products/${id}/deactivate`, undefined, key);

export const activateProduct = (id: string, key?: string) =>
  apiPost<void>(`/products/${id}/activate`, undefined, key);

export const listEggUnitConversions = () =>
  apiGet<EggUnitConversion[]>("/egg-unit-conversions");

export const updateEggUnitConversion = (id: string, body: {
  eggsPerUnit: number;
  active: boolean;
}, key?: string) => apiPut<void>(`/egg-unit-conversions/${id}`, body, key);

// --- Export / manual backup (#95) ---

// Must mirror the server's dataset list (ExportQueries.DatasetNames) — a
// missing entry here just hides a download button, nothing breaks (#84).
export const EXPORT_DATASETS = [
  "flocks", "bird-movements", "daily-entries", "daily-entry-grades",
  "egg-grades", "egg-lots", "customers", "sales-orders",
  "sales-order-items", "sales-order-allocations", "payments",
  "inventory-items", "inventory-lots", "inventory-movements",
  "feed-usages", "water-usages", "expense-categories", "expenses",
  "egg-inventory-movements", "audit-events",
] as const;

export const downloadExportCsv = (dataset: string) =>
  apiGetBlob(`/export/${dataset}`);

export const downloadFullBackup = () => apiGetBlob("/export/all");

// --- Reports (#91) ---

export interface ProductionDay {
  date: string;
  totalEggs: number;
  cracked: number;
  dirty: number;
  discarded: number;
  sellable: number;
  deaths: number;
  henDays: number;
  henDayPct: number | null;
}

export interface ProductionReport {
  days: ProductionDay[];
  totalEggs: number;
  totalSellable: number;
  totalDeaths: number;
  totalHenDays: number;
  periodHenDayPct: number | null;
  gradeTotals: { eggGradeId: string; name: string; quantity: number }[];
}

export interface SalesSummary {
  confirmedCount: number;
  revenueMinorUnits: number;
  paidMinorUnits: number;
  outstandingMinorUnits: number;
  voidedCount: number;
  currencyCode: string;
  currencyMinorUnit: number;
}

export interface ExpenseSummaryReport {
  categories: { expenseCategoryId: string; name: string; totalMinorUnits: number }[];
  grandTotalMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
}

export interface ProfitReport {
  revenueMinorUnits: number;
  expensesMinorUnits: number;
  profitMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
}

const rangeQuery = (from: string, to: string) => `?from=${from}&to=${to}`;

export const getProductionReport = (from: string, to: string) =>
  apiGet<ProductionReport>(`/reports/production${rangeQuery(from, to)}`);
export const getSalesSummary = (from: string, to: string) =>
  apiGet<SalesSummary>(`/reports/sales${rangeQuery(from, to)}`);
export const getExpenseSummary = (from: string, to: string) =>
  apiGet<ExpenseSummaryReport>(`/reports/expenses${rangeQuery(from, to)}`);
export const getProfitReport = (from: string, to: string) =>
  apiGet<ProfitReport>(`/reports/profit${rangeQuery(from, to)}`);

// --- Payments (#89, admin-only end to end — money data) ---

export interface Payment {
  id: string;
  salesOrderId: string;
  customerId: string;
  paymentDate: string;
  amountMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
  method: string;
  referenceNumber: string | null;
  note: string | null;
  voided: boolean;
  voidReason: string | null;
  version: number;
}

export interface OrderPayments {
  items: Payment[];
  paidMinorUnits: number;
  outstandingMinorUnits: number;
  totalMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
}

export interface CustomerBalance {
  customerId: string;
  confirmedTotalMinorUnits: number;
  paidMinorUnits: number;
  outstandingMinorUnits: number;
}

export interface CustomerBalances {
  items: CustomerBalance[];
  currencyCode: string;
  currencyMinorUnit: number;
}

export const listOrderPayments = (orderId: string) =>
  apiGet<OrderPayments>(`/sales/${orderId}/payments`);

export const recordPayment = (orderId: string, body: {
  paymentDate: string; amountMinorUnits: number; method: string;
  referenceNumber?: string | null; note?: string | null;
}, key?: string) => apiPost<Created>(`/sales/${orderId}/payments`, body, key);

export const voidPayment = (id: string, body: { version: number; reason: string }, key?: string) =>
  apiPost<Payment>(`/payments/${id}/void`, body, key);

export const listCustomerBalances = () =>
  apiGet<CustomerBalances>("/customers/balances");

// --- Expenses (#87, admin-only end to end — money data) ---

export interface ExpenseCategory {
  id: string;
  farmId: string;
  name: string;
  active: boolean;
}

export interface Expense {
  id: string;
  farmId: string;
  expenseCategoryId: string;
  date: string;
  description: string;
  amountMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
  flockId: string | null;
  note: string | null;
  version: number;
}

export interface ExpenseList {
  items: Expense[];
  totalMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
}

export const listExpenseCategories = (params?: { includeInactive?: boolean }) =>
  apiGet<ExpenseCategory[]>(`/expense-categories${params?.includeInactive ? "?includeInactive=true" : ""}`);

export const createExpenseCategory = (body: { name: string }, key?: string) =>
  apiPost<Created>("/expense-categories", body, key);

export const updateExpenseCategory = (id: string, body: {
  name: string; active: boolean;
}, key?: string) => apiPut<void>(`/expense-categories/${id}`, body, key);

export const listExpenses = (params?: {
  from?: string; to?: string; categoryId?: string; limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.categoryId) q.set("categoryId", params.categoryId);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<ExpenseList>(`/expenses${q.size > 0 ? `?${q}` : ""}`);
};

export const getExpense = (id: string) => apiGet<Expense>(`/expenses/${id}`);

export const createExpense = (body: {
  expenseCategoryId: string; date: string; description: string;
  amountMinorUnits: number; flockId?: string | null; note?: string | null;
}, key?: string) => apiPost<Created>("/expenses", body, key);

export const adjustExpense = (id: string, body: {
  version: number; expenseCategoryId: string; date: string; description: string;
  amountMinorUnits: number; flockId?: string | null; note?: string | null;
}, key?: string) => apiPut<Expense>(`/expenses/${id}`, body, key);
