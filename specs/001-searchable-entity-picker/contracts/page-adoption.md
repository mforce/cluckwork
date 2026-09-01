# Page Adoption Contract

Eleven picker instances are adopted across seven pages. Other native selectors remain unchanged.

| Page / picker | Discovery | Blank/default | Exact/lifecycle rules | Row display |
|---|---|---|---|---|
| Daily Entry capture | Active + Depleted | First Active, then first Depleted | Atomic deep link beats remembered ID, which beats default; all retargets retain `retarget()` grading disarm; create success hydrates returned ID by GET-only retry | `DailyEntry.flockName` and `flockStatus` |
| History filter | All statuses | Blank = All | External filter ID resolves exactly; Archived remains admissible | Rows use `flockName`; editability uses row `flockStatus` |
| Feed capture | Active + Depleted | First Active, then first Depleted | Preserve current mount/lifecycle behavior | `FeedUsage.flockName` |
| Feed filter | All statuses | Blank = All | Existing mount-time URL behavior remains; URL ownership is not expanded here | `FeedUsage.flockName` |
| Water capture | Active + Depleted | First Active, then first Depleted | Existing edit exact-resolves and retains Archived while picker is disabled; reset starts a new default transition | `WaterUsage.flockName` |
| Water filter | All statuses | Blank = All | Existing mount-time URL behavior remains | `WaterUsage.flockName` |
| Users assignment | Active only | First Active after assignments load and dialog successfully opens | Dialog open/close/reset uses existing dialog generation; Depleted/Archived existing rows remain display-only; assignment write/step-up scope remains page-owned | nullable `FlockAssignment.flockName`; null = farm-wide |
| Sales new order | All customers | First customer | Default is explicit; active order heading uses row-owned customer | `SalesOrder.customerName` |
| Sales filter | All customers | Blank = All | Canonical `customerId` URL is sole truth; malformed absent; well-formed unavailable Retry/Clear; preserve other query keys; hide old rows/headings synchronously | `SalesOrder.customerName` |
| Expenses record | All statuses | Blank = None | Archived may be selected | nullable `Expense.flockName` |
| Expenses edit | All statuses | Blank = None | Existing Archived selection exact-resolves and remains named | nullable `Expense.flockName` |

## Customer Links

- Customer table names are real links to `/sales?customerId=<canonical-id>`.
- Authorized Dashboard recent-sales customer names use `SalesOrder.customerName` and the same link.
- Dashboard no longer loads a 500-customer catalog solely to name Sales rows.
- Same-page links from Sales rows and a generalized entity-link component remain out of scope.

## Sales URL Rules

- Validate the canonical 8-4-4-4-12 GUID shape before making requests; normalize accepted IDs to lowercase.
- On select, clone the current `URLSearchParams`, set `customerId`, and preserve unrelated keys.
- On clear, remove only `customerId`.
- Back/Forward and direct navigation are full selection transitions.
- A malformed value behaves as absent and is not rewritten merely to clean the URL.
- A well-formed missing/inaccessible value blocks filtered list fetch and presents unavailable Retry/Clear.
- URL identity changes synchronously key or clear Sales rows, active order, and row-derived heading before any effect/debounce/request.

## Preserved Native Selectors and Out-of-Scope Surfaces

- Feed inventory item.
- Sales product, selling unit, payment method, and status.
- Expense category and month.
- Water source and unit.
- User role.
- Dashboard/Flocks list ceilings, catalogs, grades, products, inventory items, and table/ledger paging.

## Documentation Contract

Help and in-app glossary in en/es/tl must cover:

- type-to-search and 50-row progressive discovery;
- Arrow/Enter/Escape interaction and the distinction between exploration and commit;
- Load more, Retry, unavailable identities, and keyboard-only recovery;
- Customer/Dashboard name links into URL-filtered Sales.

`specs/product/GLOSSARY.md` receives the matching searchable-picker concept because the constitution requires product-glossary synchronization for user-visible concepts.
