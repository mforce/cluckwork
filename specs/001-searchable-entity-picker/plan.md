# Implementation Plan: Searchable Paged Entity Picker

**Branch**: `001-searchable-entity-picker` | **Date**: 2026-08-31 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/001-searchable-entity-picker/spec.md`

## Summary

Extend the existing flock and customer read contracts with literal name search, stable server paging, and explicit flock eligibility while preserving every legacy caller. Add current referenced names/status to affected row responses through scoped bulk projections. Replace the 11 truncated flock/customer selectors across seven pages with one narrow async named-entity picker engine exposed only through typed `FlockPicker` and `CustomerPicker` adapters. The picker owns discovery, request generations, committed selection, keyboard interaction, recovery, and exploration/write blocking; each page continues to own its business lifecycle side effects and defaults. Sales customer filtering becomes URL-owned, Customers and authorized Dashboard names link into it, and one existing PR-smoke Playwright scenario reuses the unchanged #627 fixture.

## Technical Context

**Language/Version**: .NET 10 / C# SDK default; TypeScript 7; React 19.2

**Primary Dependencies**: ASP.NET Core minimal APIs, EF Core 10 with Npgsql 10, FluentValidation 12, React Router 8.3, i18next 26, React Testing Library, Playwright 1.62

**Storage**: Existing PostgreSQL schema through EF Core; no migration, new index, or persisted entity

**Testing**: xUnit with Testcontainers PostgreSQL; Vitest 4 with React Testing Library/user-event; Playwright over the built SPA and simulation stack; Chromium CDP helper only for accessibility-tree or `inert` assertions

**Target Platform**: Linux-hosted API serving a browser SPA; modern desktop and mobile browsers with keyboard and assistive-technology support

**Project Type**: Layered web application with a .NET API and React SPA

**Performance Goals**: Fixed 50-result picker pages; 250 ms search debounce; stable `Name, Id` ordering; one scoped bulk reference lookup per returned row page; flock movement aggregation bounded to returned flock IDs; no per-row name requests

**Constraints**: Preserve existing limit/offset clamps and optional query behavior; tenant/flock-scope filters execute before search/order/page; no `IgnoreQueryFilters`; no high-limit workaround; no new generic catalog/entity-link framework; no schema/index work without measurement; no #627 seed/count/manifest/fingerprint/harness changes; no new CI wiring

**Scale/Scope**: 11 picker instances on Daily Entry, History, Feed, Water, Users, Sales, and Expenses; additive display data on six row contracts; verified against the existing 102-flock/101-customer simulation fixture and arbitrary larger catalogs through paging

## Constitution Check

### Pre-Design Gate

| Principle | Result | Design evidence |
|---|---|---|
| I. Domain Integrity and Layered Boundaries | PASS | The change is read-only: no aggregate mutation, domain state, middleware order, or new write command. API parsing remains at existing read endpoints, Application owns repository contracts/read concepts, Infrastructure owns EF queries, and the SPA consumes HTTP contracts. |
| II. Tenant Isolation and Credential Safety | PASS WITH REQUIRED PROOF | All discovery, exact resolution, bulk reference projection, and assignment joins retain the structural global filters. No bypass is introduced. Real-Postgres tests must causally prove foreign-tenant and flock-scoped Worker exclusion for both picker discovery and row names. |
| III. Fail-Closed Data and Operations | PASS | `InitialCreate`, migrations, base data, boot guards, process roles, and configuration are untouched. Invalid or conflicting eligibility input returns 400; unavailable exact identities remain explicit rather than failing open to a default. |
| IV. Evidence-Backed Change | PASS WITH REQUIRED PROOF | PostgreSQL tests cover search semantics/order/scope; a query-count guard plus adversarial mutation proves grouped lookup; component tests prove async generations and ARIA behavior; every adopting page receives focused coverage; callers and the existing Playwright suite are inspected explicitly. |
| V. Reproducible, Host-Portable Delivery | PASS | No package, lockfile, provider-specific configuration, image, release, or deployment change is planned. |

Additional gates:

- No write contract changes, aggregate mutations, or idempotency changes are introduced.
- User-visible terminology requires synchronized Help, in-app glossary, en/es/tl catalogs, and `specs/product/GLOSSARY.md` updates.
- The representative E2E joins the already-configured pull-request smoke suite without workflow changes; slow/canary/k6 behavior is unchanged.
- Work is on `001-searchable-entity-picker`; no commit or push is authorized by this plan.
- No constitutional exception or complexity waiver is required.

### Post-Design Re-check

PASS. Phase 1 retains the same boundaries: [data-model.md](data-model.md) adds only transient read/UI models; [contracts/http-api.md](contracts/http-api.md) keeps query filters and existing authorization; [contracts/picker-ui.md](contracts/picker-ui.md) separates discovery and selection generations; [contracts/page-adoption.md](contracts/page-adoption.md) preserves page-owned lifecycle rules; and [quickstart.md](quickstart.md) supplies the required boundary evidence. No unresolved clarification or constitutional violation remains.

## Project Structure

### Documentation (this feature)

```text
specs/001-searchable-entity-picker/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── http-api.md
│   ├── page-adoption.md
│   └── picker-ui.md
├── checklists/
│   └── requirements.md
└── tasks.md                 # created later by $speckit-tasks
```

### Source Code (repository root)

```text
src/
├── Cluckwork.Application/Features/
│   ├── Flocks/IFlockRepository.cs
│   ├── Customers/ICustomerRepository.cs
│   └── Users/IUserRoleAssignmentRepository.cs
├── Cluckwork.Infrastructure/Repositories/
│   ├── FlockRepository.cs
│   ├── CustomerRepository.cs
│   ├── UserRoleAssignmentRepository.cs
│   └── BirdMovementRepository.cs
└── Cluckwork.Api/Endpoints/
    ├── Flocks/FlockEndpoints.cs
    ├── Customers/CustomerEndpoints.cs
    ├── DailyEntries/DailyEntryEndpoints.cs
    ├── Inventory/InventoryEndpoints.cs
    ├── Water/WaterUsageEndpoints.cs
    ├── Users/UserEndpoints.cs
    ├── Sales/SaleEndpoints.cs
    └── Expenses/ExpenseEndpoints.cs

web/src/
├── api/
│   ├── cluckwork.ts
│   └── *picker/list contract tests*
├── components/
│   ├── NamedEntityPicker.tsx
│   ├── NamedEntityPicker.test.tsx
│   ├── FlockPicker.tsx
│   └── CustomerPicker.tsx
├── routes/
│   ├── DailyEntryPage.tsx
│   ├── HistoryPage.tsx
│   ├── FeedPage.tsx
│   ├── WaterPage.tsx
│   ├── UsersPage.tsx
│   ├── SalesPage.tsx
│   ├── ExpensesPage.tsx
│   ├── CustomersPage.tsx
│   ├── Dashboard.tsx
│   └── HelpPage.tsx
├── i18n/{en,es,tl}.ts
└── styles.css

tests/Cluckwork.Api.IntegrationTests/
├── NamedEntityDiscoveryTests.cs
├── NamedRowProjectionTests.cs
└── existing feature/scope integration tests

tools/simulation/ui/
├── specs/named-entity-picker.spec.ts
├── src/mutants.ts
└── mutation-check.sh

specs/product/GLOSSARY.md
```

**Structure Decision**: Extend the existing layered read paths and page modules. The only shared frontend abstraction is a feature-local named-entity picker engine with two typed adapters; other catalogs and links remain outside its public surface. Read projections stay in the existing repositories/endpoints rather than introducing new projects, navigations, or a generic reference framework.

## Complexity Tracking

No constitutional violations require justification.
