// Typed wrappers over the Cluckwork JSON API (mirrors the endpoint DTOs).
import { apiDelete, apiGet, apiGetBlob, apiPost, apiPut, apiPutBytes, STEP_UP_HEADER } from "./client";

/**
 * #494 — who created a record and who last changed it, derived server-side
 * from the audit trail rather than stored on the record.
 *
 * `createdBy*` are null for a record created before #494 shipped: there is no
 * backfill, so its trail has no creation event to report.
 *
 * `lastChanged*` are null when nothing has happened since creation. That is the
 * SERVER's judgement, made from the audit event ids — do NOT re-derive it by
 * comparing the two timestamps, because two distinct events can share an
 * instant and that comparison would hide a real edit.
 */
export interface RecordHistory {
  createdByEmail: string | null;
  createdAtUtc: string | null;
  lastChangedByEmail: string | null;
  lastChangedAtUtc: string | null;
  // #494 — when the record became official. Only resources with a promotion
  // step send it (daily entries submit, sales orders confirm), hence optional:
  // flocks, egg grades and expenses have no such moment.
  madeOfficialAtUtc?: string | null;
}

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
}

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

export interface SalesOrder extends RecordHistory {
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
  body: {
    productId: string; quantity: number; unit?: string; unitPriceMinorUnits?: number;
    // #445 — the eggs-per-unit factor the UI previewed while the quantity was
    // entered; the server refuses the write (422 SalesOrder.UnitDefinitionChanged)
    // if the definition changed in between, so the recorded QuantityBase can
    // never silently differ from the previewed one. Omit when nothing was shown.
    expectedEggsPerUnit?: number;
  },
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

// The full §4.5 localization set. currencyCode/currencyMinorUnit keep their
// names and meaning from before #123 — every money field parses with them.
export interface Account {
  id: string;
  name: string;
  currencyCode: string;
  currencyMinorUnit: number;
  currencySymbol: string;
  timeZoneId: string;
  locale: string;
  unitSystem: string;
  // Null means "follow the locale" rather than a day.
  firstDayOfWeek: string | null;
  dateFormatOverride: string | null;
  timeFormatOverride: string | null;
  // Optimistic-concurrency token — sent back on save; a mismatch is a 409.
  version: number;
  // Null when the farm has no logo: the chrome shows app branding and never
  // calls /account/logo. Otherwise it changes whenever the logo is replaced,
  // which is what makes the cached blob URL self-invalidating.
  logoContentHash: string | null;
  // Same contract as logoContentHash, for the post-login splash banner (#179):
  // null means no banner is set, so the splash is skipped entirely.
  bannerContentHash: string | null;
  // The farm's accent palette (#149) — farm-wide and admin-chosen, unlike the
  // light/night toggle, which stays a per-user device preference. The API is
  // the source of truth; localStorage only caches it for the pre-paint script.
  brand: string;
  // #444 — the farm-default Daily Entry stepper pack unit (an EggUnitConversion
  // code, e.g. "Tray"). A user's own Me.preferredStepperUnit overrides this.
  defaultStepperUnit: string;
  // #612 — role-agnostic on purpose: true only for a restricted plain Worker
  // under AllFarmFlocks, so the Sales screen can show the persistent generic
  // notice without exposing the raw policy (that lives only on
  // FarmSettings, admin-only) to every role.
  showFarmWideSaleAllocationNotice: boolean;
}

// Clients need the account currency to parse money input correctly — a JPY
// amount has 0 decimals, so assuming 2 silently multiplies costs by 100 — and
// the timezone to cap date inputs at the FARM's today rather than the
// browser's (#123). Readable by every authenticated role.
export const getAccount = () => apiGet<Account>("/account");

// The signed-in user's own profile (#45 GET /me). Distinct from the admin `User`
// list DTO: this carries the user's UI-language preference. Role is echoed for
// convenience; the JWT stays authoritative for gating.
export interface Me {
  id: string;
  email: string;
  name: string | null;
  role: string;
  language: string | null;
  // #444 — overrides Account.defaultStepperUnit for this user's Daily Entry
  // steppers. Null = follow the farm default.
  preferredStepperUnit: string | null;
}

export const getMe = () => apiGet<Me>("/me");

// PUT one absolute preference (#45). null clears it. apiPut mints a fresh
// Idempotency-Key per call. Returns void (204).
export const putMeLanguage = (language: string | null): Promise<void> =>
  apiPut<void>("/me/language", { language });

// #444 — same shape as putMeLanguage: one absolute preference, null clears it.
export const putMeStepperUnit = (unit: string | null): Promise<void> =>
  apiPut<void>("/me/stepper-unit", { unit });

export interface FarmSettings {
  settings: Account;
  // False once any sales order, payment or expense has recorded an amount:
  // §4.6 locks the currency for good. Surfaced so the screen can disable the
  // field with a reason instead of letting the user find out as a 422.
  canChangeCurrency: boolean;
  // The logo upload cap in bytes (#123). It is server CONFIG, so it rides here
  // rather than being duplicated as a client constant — a hardcoded copy would
  // silently diverge from the server the moment the config changed.
  logoMaxUploadBytes: number;
  // Same contract, for the banner (#179) — a separate, larger cap.
  bannerMaxUploadBytes: number;
  // #612 — the raw policy, admin-only (like canChangeCurrency above). Every
  // other role only ever sees the derived Account.showFarmWideSaleAllocationNotice.
  workerSaleAllocationPolicy: string;
}

export interface UpdateFarmSettings {
  name: string;
  timeZoneId: string;
  locale: string;
  currencyCode: string;
  unitSystem: string;
  firstDayOfWeek: string | null;
  dateFormatOverride: string | null;
  timeFormatOverride: string | null;
  brand: string;
  defaultStepperUnit: string;
  workerSaleAllocationPolicy: string;
  version: number;
}

// Admin-gated (the read too — it carries canChangeCurrency).
export const getFarmSettings = () => apiGet<FarmSettings>("/account/settings");

export const updateFarmSettings = (body: UpdateFarmSettings, key?: string) =>
  apiPut<void>("/account/settings", body, key);

// --- Farm logo (#123) ---

// What the server STORED, not what the browser sent: the upload is rewritten
// without metadata, so the type and byte length can both differ from the file.
export interface FarmLogo {
  contentType: string;
  contentHash: string;
  width: number;
  height: number;
  byteLength: number;
  updatedAt: string;
}

// The formats the sanitizer accepts. SVG is refused server-side (it is a
// script container); listing them here only spares the user a round trip.
export const LOGO_ACCEPT = "image/png,image/jpeg,image/webp";

// Fetched through the API client rather than pointed at by an <img src>: the
// endpoint is behind the Authorization header, which an <img> cannot send. The
// caller renders the blob and revokes the URL.
export const getFarmLogo = () => apiGetBlob("/account/logo");

export const uploadFarmLogo = (file: File, key?: string) =>
  apiPutBytes<FarmLogo>(
    "/account/logo",
    file,
    // A browser that could not guess the type still uploads: the server sniffs
    // the bytes and this header is never read.
    file.type || "application/octet-stream",
    key,
  );

export const removeFarmLogo = (key?: string) => apiDelete<void>("/account/logo", key);

// --- Farm banner (#179) ---
//
// A second, independent branding image: a wide/hero picture shown full-size
// on the post-login splash, distinct from the small sidebar logo above. Same
// contract shape (raw-body PUT, blob-fetched GET, own upload cap from server
// config), just its own route and its own larger default cap.

export interface FarmBanner {
  contentType: string;
  contentHash: string;
  width: number;
  height: number;
  byteLength: number;
  updatedAt: string;
}

export const BANNER_ACCEPT = "image/png,image/jpeg,image/webp";

export const getFarmBanner = () => apiGetBlob("/account/banner");

export const uploadFarmBanner = (file: File, key?: string) =>
  apiPutBytes<FarmBanner>(
    "/account/banner",
    file,
    file.type || "application/octet-stream",
    key,
  );

export const removeFarmBanner = (key?: string) => apiDelete<void>("/account/banner", key);

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

// --- Users (#73, admin-only endpoints) ---

export interface User {
  id: string;
  email: string;
  displayName: string | null;
  role: string; // "Admin" | "Worker"
  // #356 — ISO timestamp of when this user was disabled, or null while active.
  // The SPA renders the row muted with a "Disabled" badge when this is set.
  disabledAt: string | null;
}

export const listUsers = () => apiGet<User[]>("/users");

export interface FlockAssignment {
  id: string;
  flockId: string | null;
}

export const listFlockAssignments = (userId: string) =>
  apiGet<FlockAssignment[]>(`/users/${userId}/flock-assignments`);

// #606 — stepUpToken is required by the server UNCONDITIONALLY: every
// interactive assignment or removal changes durable account access,
// regardless of the target's role.
export const assignFlock = (userId: string, flockId: string, key?: string, stepUpToken?: string) =>
  apiPost<Created>(
    `/users/${userId}/flock-assignments`, { flockId }, key,
    stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

export const unassignFlock = (userId: string, assignmentId: string, key?: string, stepUpToken?: string) =>
  apiDelete<void>(
    `/users/${userId}/flock-assignments/${assignmentId}`, key,
    stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #308/#360 — stepUpToken is required by the server UNCONDITIONALLY: every
// interactive user creation establishes a durable login, regardless of the
// created role. Callers get it from api/client.ts's stepUp().
export const createUser = (body: {
  email: string; password: string; role: string; name?: string;
}, key?: string, stepUpToken?: string) => apiPost<Created>(
  "/users", body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #163 — edit a user's display name. `name: null` clears it back to "—".
export const updateUser = (id: string, body: { name: string | null }, key?: string) =>
  apiPut<void>(`/users/${id}`, body, key);

// #165 — an Owner sets a user's password without knowing the current one. The
// server signs that user out of every device.
//
// #308/#360 — stepUpToken is required by the server UNCONDITIONALLY: every
// administrative reset replaces an authenticator, regardless of the target's
// current role.
export const setUserPassword = (
  id: string, body: { newPassword: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/password`, body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #355 — promote/demote an existing user's role. The server signs that user
// out of every device.
//
// #308/#360 — stepUpToken is required by the server UNCONDITIONALLY: every
// role change mutates a durable authorization set, including no-ops and
// apparent demotions. Server also refuses self-targeting (400
// Users.CannotChangeOwnRole) — surfaced as an ordinary ApiError, not
// special-cased client-side.
export const changeUserRole = (
  id: string, body: { role: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/role`, body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

export const changeUserEmail = (
  id: string, body: { email: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/email`, body, key,
  stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined,
);

// #356/#360 — disable a user: revokes every session and refuses further
// sign-in. Its step-up proof is unconditional like create/reset/role: every
// disable requires stepUpToken regardless of the target's role. Server also
// refuses self-targeting (400 Users.CannotDisableSelf), surfaced as an ordinary
// ApiError like every other domain refusal.
export const disableUser = (
  id: string, body: { reason: string | null }, key?: string, stepUpToken?: string,
) => apiPost<void>(
  `/users/${id}/disable`, body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #356/#360 — re-enable a disabled user. No body: there is no free-text field,
// only the route id and the step-up header, required unconditionally like
// create/reset/role and disable.
export const enableUser = (
  id: string, key?: string, stepUpToken?: string,
) => apiPost<void>(
  `/users/${id}/enable`, undefined, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// Formats minor units per the order's snapshotted currency (JPY has 0 decimals).
export function formatMoney(minorUnits: number, currencyCode: string, minorUnit: number): string {
  const value = minorUnits / 10 ** minorUnit;
  return `${value.toFixed(minorUnit)} ${currencyCode}`;
}

// Inverse of formatMoney: parse a user-entered decimal amount into integer minor
// units at the currency's scale. The `Math.round` absorbs binary-float artifacts
// (e.g. 0.29 * 100 = 28.999… which would truncate to 28 without it). Returns NaN
// for non-numeric input and may return a negative — callers apply their own
// finite/non-negative guard and context-specific error message.
export function parseMoneyToMinorUnits(text: string, minorUnit: number): number {
  return Math.round(parseFloat(text) * 10 ** minorUnit);
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
  /** Hand-graded remainder: total − cracked − dirty − discarded (#394). */
  sellable: number;
  /**
   * #396 — eggs that became stock WITHOUT being hand-graded: the cracked and
   * dirty counters, but only where that entry resolved the condition to a
   * grade. Separate from `sellable` on purpose; the two answer different
   * questions and were only ever equal while conditions were always losses.
   */
  fromCounts: number;
  deaths: number;
  henDays: number;
  henDayPct: number | null;
}

export interface ProductionReport {
  days: ProductionDay[];
  totalEggs: number;
  totalSellable: number;
  totalFromCounts: number;
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

export interface Expense extends RecordHistory {
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
