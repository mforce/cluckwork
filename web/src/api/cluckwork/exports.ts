import { apiGetBlob } from "../client";

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
