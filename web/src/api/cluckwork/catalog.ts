import { apiGet, apiPost, apiPut } from "../client";
import type { Created } from "./shared";

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
  version: number;
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
  version: number;
  eggsPerUnit: number;
  active: boolean;
}, key?: string) => apiPut<void>(`/egg-unit-conversions/${id}`, body, key);
