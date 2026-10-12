import { apiGet, apiPost, apiPut } from "../client";
import type { Created, RecordHistory } from "./shared";

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
  flockName?: string | null;
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
