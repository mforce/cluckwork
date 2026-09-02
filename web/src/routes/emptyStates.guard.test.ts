import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import path from "node:path";

// #655 — 4a classified all 43 `<p className="muted">{t(...)}</p>` sites under
// web/src/routes/ (as of this PR) and found 13 that are genuine empty
// states — rendered IN PLACE of a list/table when it has no rows. The other
// 30 are inline hints, per-row detail, or loading text and correctly stay a
// bare muted paragraph; converting them would have been wrong. This guard
// pins ONLY those 13 sites — it is not a blanket "no muted paragraph exists"
// check, and must not be read as one.
const EMPTY_STATE_SITES: { file: string; key: string }[] = [
  { file: "CustomersPage.tsx", key: "noCustomersMessage" },
  { file: "Dashboard.tsx", key: "noFlocksMessage" },
  { file: "Dashboard.tsx", key: "noStockMessage" },
  { file: "Dashboard.tsx", key: "noOrdersMessage" },
  { file: "FlocksPage.tsx", key: "noFlocksMessage" },
  { file: "ProductsPage.tsx", key: "noProductsMessage" },
  { file: "StockPage.tsx", key: "noStockMessage" },
  { file: "StockPage.tsx", key: "noLotsMessage" },
  { file: "HistoryPage.tsx", key: "noEntriesMatch" },
  { file: "FeedPage.tsx", key: "noRecordsMatch" },
  { file: "WaterPage.tsx", key: "noRecordsMatch" },
  { file: "ExpensesPage.tsx", key: "noExpensesMessage" },
  { file: "SalesPage.tsx", key: "noOrdersMatch" },
];

describe("classified empty-state sites render through EmptyState, not a bare muted paragraph", () => {
  it.each(EMPTY_STATE_SITES)("$file's $key", ({ file, key }) => {
    const source = readFileSync(path.join(__dirname, file), "utf8");
    const bareShape = new RegExp(`<p className="muted">\\{t\\("${key}"\\)\\}</p>`);
    expect(source).not.toMatch(bareShape);
    expect(source).toMatch(/import \{ EmptyState \} from "\.\.\/components\/EmptyState";/);
  });
});
