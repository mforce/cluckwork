# Product: Entity-scoped audit history

## Problem

An admin looking at one Flock, Sales Order, Expense, Egg Grade, or Daily Entry has no way to ask "who touched this, and when" from that record itself. Today the only path is the global Audit page — a firehose of every event across every entity, newest-first, with an action-type filter that doesn't narrow by record. Finding one record's history means scrolling and guessing, or asking a coworker who remembers.

## Success metric

Successful (200) `GET /api/v1/audit` requests carrying an `entityId`, counted week over week starting at ship. The filter has existed server-side and on the SPA client since before this feature and has never been called with an `entityId` — baseline is a known, verifiable zero. This counts requests, not distinct users or sessions (the request logs carry no actor identity today, and adding one is out of scope) — read it as "is the affordance being exercised at all," not as a headcount of admins using it.

**Confirmed during Gate 2 code read: production request logs do NOT capture query strings today** — `UseSerilogRequestLogging` in `Program.cs` has no `EnrichDiagnosticContext`, so the completion line carries only `RequestPath`, never the query string. **Decided at Gate 2**: add one line to `Cluckwork.Api/Program.cs`'s `UseSerilogRequestLogging` (diagnostic-context enrichment flagging whether `/api/v1/audit` was called with `entityId`) — observability, not a change to the audit surface or a new endpoint, so it stays within the issue's intent even though it touches an API-layer file. This is the one non-SPA line in the ticket.

## Announcement

Every record now remembers its own story. Open a Flock, a Sales Order, an Expense, an Egg Grade, a Daily Entry, or an Egg Lot, and you'll find a "View history" link that takes you straight to everything that ever happened to it — who created it, who changed it, and when, in order, filterable by what kind of change you're looking for. No more digging through the global Audit feed hoping you land on the right row.

## Screens

- Existing 6 list/detail rows (Flocks, Daily Entries via `HistoryPage.tsx`, Sales Orders, Expenses, Egg Grades, and Egg Lots via `StockPage.tsx`'s `lots` list) — each gets a "View history" link/button per row. No new mockup: link sits beside the `ProvenanceCell` column #494 already added (Egg Lots is the one exception — #494 didn't touch `StockPage`, so this is a new row affordance there, still no new mockup since it follows the same link pattern as the other five).
  - **Corrected during Gate 2 code read**: "Daily Entries" provenance lives on `HistoryPage.tsx` (route `/history`), not `DailyEntryPage.tsx`. "Egg Lots" live on `StockPage.tsx` (route `/stock`), not `InventoryPage.tsx` — `InventoryPage` only exposes lot ids inside an adjustment `<select>`, no lot rows to attach a link to. `StockPage` already has a per-lot "History"/"Hide history" toggle for the **inventory movement ledger** (`listEggLotMovements` — a related but distinct trail); the new audit-history link needs a visibly different label so the two aren't confused.
- Existing AuditPage — gains an entity-scoped mode when navigated with `?entityId=<guid>`: heading names the record, the existing action-type filter stays visible as a within-record narrowing control (kept, not hidden — the component already exists and stays useful, e.g. "only submits/locks on this Daily Entry"), list shows only that record's events. No new mockup: variant of an already-shipped page.

## Product stance: list-row links are a stopgap

No record has a detail page today (per issue research). The "right" home for "this record's history" is a detail page; putting the link on the list row instead is a deliberate workaround for that gap, not the end state. When detail pages land, this link moves there — noted here so it isn't rediscovered as a surprise later.
