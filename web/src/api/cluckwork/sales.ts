import { apiDelete, apiGet, apiPost, apiPut } from "../client";
import type { Created, RecordHistory } from "./shared";

// --- Customers & sales (#23/#24) -------------------------------------------

export interface Customer {
  id: string;
  name: string;
  phone: string;
  email: string | null;
  address: string | null;
  note: string | null;
  version: number;
}

export interface OrderItem {
  id: string;
  productId: string;
  eggGradeId: string;
  eggGradeName: string;
  unit: string;
  baseUnitFactor: number;
  quantity: number;
  quantityBase: number;
  unitPriceMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
  // #720 — REQUIRED, not optional, and deliberately so: an optional field is
  // how a consumer silently forgets to render a state, and null here is a
  // state the screen must show, not an absent value. WHICH state it is comes
  // from listPriceBasis below, not from this field (#773): a null is no longer
  // uniformly "No list price".
  // Same currency and minor unit as unitPriceMinorUnits.
  listUnitPriceMinorUnits: number | null;
  // #773 — which kind of nothing a null listUnitPriceMinorUnits is. REQUIRED
  // for the same reason the field above is: the screen must branch on a real
  // value, and only PreDating means "we do not know". Rendered through
  // i18n/enums.ts (listPriceBasisLabel), never raw.
  listPriceBasis: string;
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
  // #512 US4 (T048/T050) — the customer's CURRENT name, carried by the row
  // so it renders independently of the page's capped picker results. Always
  // present, but the value can be null when the customer fell out of the
  // caller's tenant scope between reads (see contracts/http-api.md's
  // "defensive null on a non-null id").
  customerName: string | null;
  // #721 — why this order was sold below list. Both null together on an order
  // confirmed before that shipped (no backfill), which reads as "not recorded",
  // never as "no discount". The code is the enum member name; render it through
  // i18n/enums.ts (discountReasonLabel), never raw.
  discountReasonCode: string | null;
  discountReasonNote: string | null;
  // #769 — what this order still owes: confirmed total − non-voided payments.
  // REQUIRED, not optional, for #720's reason: null is a state the screen must
  // render (an em dash), not an absent value. It has exactly two causes and the
  // screen cannot tell them apart, deliberately — the order is not Confirmed,
  // or the caller is outside the money tier (Owner/Manager/Sales), for whom the
  // server never computes it. Paid is `totalMinorUnits - outstandingMinorUnits`;
  // there is no second field.
  outstandingMinorUnits: number | null;
}

export const listCustomers = (params?: {
  limit?: number;
  offset?: number;
  search?: string;
}) => {
  const q = new URLSearchParams();
  if (params?.search) q.set("search", params.search);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<Customer[]>(`/customers${q.toString() !== "" ? `?${q}` : ""}`);
};

// #512 — exact resolution: the full customer or a 404 (SalesFlow-authorized).
// Same unavailable-state contract as getFlock.
export const getCustomer = (id: string) => apiGet<Customer>(`/customers/${id}`);

export const createCustomer = (body: {
  name: string;
  phone: string;
  email?: string;
  address?: string;
  note?: string;
}, key?: string) => apiPost<Created>("/customers", body, key);

export const updateCustomer = (id: string, body: {
  version: number;
  name: string;
  phone: string;
  email?: string;
  address?: string;
  note?: string;
}, key?: string) => apiPut<void>(`/customers/${id}`, body, key);

export const listOrders = (params?: {
  status?: string; customerId?: string; from?: string; to?: string;
  limit?: number; offset?: number;
  // #769 — server-side: "confirmed orders whose non-voided payments come to
  // less than the total", evaluated across the whole result set rather than
  // the current page. Emitted only when true; the server 403s a caller outside
  // the money tier that sends it, so SalesPage never sends it for one.
  unpaid?: boolean;
}) => {
  const q = new URLSearchParams();
  if (params?.status) q.set("status", params.status);
  if (params?.customerId) q.set("customerId", params.customerId);
  if (params?.unpaid) q.set("unpaid", "true");
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
    expectedListUnitPriceMinorUnits?: number;
    expectedListPriceIsUnset?: boolean;
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

// #721 — the discount reason rides on the confirm itself, so a below-list order
// cannot be confirmed without one. Omitting the body entirely is still the right
// call for an order with nothing below list: the server refuses a reason there.
export const confirmOrder = (
  orderId: string,
  body?: { discountReasonCode: string; discountReasonNote?: string },
  key?: string,
) => apiPost<{ orderId: string; status: string }>(`/sales/${orderId}/confirm`, body, key);

// Undo of a mistaken confirm (#60): stock returns to its source lots.
export const voidOrder = (orderId: string, reason: string, key?: string) =>
  apiPost<{ salesOrderId: string; status: string }>(`/sales/${orderId}/void`, { reason }, key);

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
