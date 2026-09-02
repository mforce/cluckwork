# Quickstart Validation: Searchable Paged Entity Picker

This guide validates the finished feature. It assumes implementation tasks generated from this plan have been completed.

## Prerequisites

- .NET 10 SDK.
- Node/npm versions accepted by the repository.
- Docker available for PostgreSQL integration tests and the simulation stack.
- Frontend dependencies installed with `npm ci` in `web/`.
- For real-browser validation, dependencies installed in `tools/simulation/ui/` and a usable Chromium as described in that directory's README.

## 1. Static Contract Checks

From the repository root:

```bash
git diff --check
dotnet build Cluckwork.sln
cd web
npm run typecheck
npm run i18n:scan
npm run build
```

Expected:

- Build and typecheck finish without warnings/errors.
- The i18n scan reports no untranslated hardcoded picker copy; the scan is advisory, so inspect its output.
- en/es/tl catalog parity tests pass in the focused suite below.

## 2. Real-Postgres API and Scope Contract

Docker is required.

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter "FullyQualifiedName~NamedEntityDiscoveryTests|FullyQualifiedName~NamedRowProjectionTests|FullyQualifiedName~FlockScopeTests"
```

Expected coverage:

- blank, trimmed, case-insensitive literal substring search;
- literal `%`, `_`, `\`, and combinations;
- duplicate-name `Name, Id` ordering across offsets;
- eligibility before paging and compatibility parsing;
- unknown/both eligibility parameters return 400;
- tenant and flock-scoped Worker isolation;
- all six row-name/status additions;
- constant grouped-reference query count and single assignment left join;
- flock movement aggregation bounded to returned IDs.

Before claiming the query-count guard, run its planned per-row lookup mutant and confirm the focused test becomes red, then restore the implementation and rerun green.

## 3. Picker and Page Tests

```bash
cd web
npm test -- --run \
  src/components/NamedEntityPicker.test.tsx \
  src/api/listCustomers.test.ts \
  src/routes/DailyEntryPage.test.tsx \
  src/routes/HistoryPage.test.tsx \
  src/routes/FeedPage.test.tsx \
  src/routes/WaterPage.test.tsx \
  src/routes/UsersPage.test.tsx \
  src/routes/SalesPage.test.tsx \
  src/routes/ExpensesPage.test.tsx \
  src/routes/CustomersPage.test.tsx \
  src/routes/Dashboard.test.tsx \
  src/routes/HelpPage.test.tsx \
  src/i18n/catalogParity.test.ts
```

Expected picker proof:

- 250 ms debounce and immediate old-query hiding;
- replacement versus extension errors and retry;
- stale success/error/finally rejection;
- raw server-count offset advancement, dedupe, and final empty page;
- exact/default/lifecycle generation races;
- committed label retention and unavailable identity;
- exploration blocks writes, including outside-click submission;
- keyboard/pointer parity, stable ARIA ownership, live announcements, required/disabled state, and Retry focus.

Expected page proof follows [page-adoption.md](contracts/page-adoption.md), including Daily Entry create hydration without repeated POST, Water Archived edit retention, Users dialog default timing, row-owned names/status, Sales URL navigation, and customer links.

## 4. Manual API Probes

With a local stack and authenticated browser/session, inspect these requests through the browser network tools or an authenticated HTTP client:

```text
GET /api/v1/flocks?search=sim%20z&eligibility=active&limit=50&offset=0
GET /api/v1/flocks?search=%25&eligibility=all&limit=50&offset=0
GET /api/v1/flocks?eligibility=all&includeArchived=false
GET /api/v1/customers?search=page%20two&limit=50&offset=0
GET /api/v1/customers?search=page%20two&limit=50&offset=50
GET /api/v1/flocks/<late-sorting-flock-id>
GET /api/v1/flocks/<missing-or-ineligible-flock-id>
GET /api/v1/customers/<late-sorting-customer-id>
GET /api/v1/customers/<missing-customer-id>
```

Expected:

- Matches are literal and stably ordered.
- The conflicting flock request returns 400.
- Exact GETs retain names outside the visible result group; missing or inaccessible IDs return 404 without identifying data.
- Legacy calls without new parameters remain unchanged.

## 5. Built-SPA Scenario Over #627 Fixture

From the repository root:

```bash
bash tools/simulation/bootstrap.sh
bash tools/simulation/reset.sh
```

Then:

```bash
cd tools/simulation/ui
npm run typecheck
npx playwright test specs/named-entity-picker.spec.ts
bash mutation-check.sh named-entity-picker-paging-broken
```

Expected:

- The real picker reaches and commits `Sim Z Flock Page Two` and `Sim Customer Z Page Two` without any fixture changes.
- The page shows committed names, not identifier fragments.
- Keyboard or pointer interaction follows translated selectors from the SPA catalogs.
- Baseline is green, the paging mutant is red for the expected sentinel assertion, and restored baseline is green.
- Any accessibility-tree or `inert` assertion uses `src/ax.ts`; ordinary roles/names use Playwright locators.

This scenario is part of the existing quick smoke suite and therefore participates in the current pull-request smoke workflow without workflow changes.

## 6. First-Time User Usability Protocol

Use the built SPA over the unchanged #627 fixture. Ask at least one keyboard participant and one pointer participant who did not implement the picker to complete these tasks without coaching:

1. Find and commit `Sim Z Flock Page Two`.
2. Find and commit `Sim Customer Z Page Two`.
3. Recover from one replacement or extension failure using Retry.

For each participant, record whether all three tasks were completed without assistance and whether any wrong entity was committed. SC-008 passes only when every observed pass completes all three tasks independently with zero wrong selections; an automated Playwright run does not substitute for this protocol.

## 7. Full Gates

```bash
dotnet build Cluckwork.sln
dotnet test Cluckwork.sln
cd web
npm run test:coverage
npm run build
npm run verify:sw
cd ..
tools/schema-docs/generate.sh --check
cd tools/simulation/ui
npm test
```

Expected:

- All solution and frontend tests pass.
- Schema check remains clean because no migration exists.
- The existing Playwright smoke suite passes over the unchanged fixture.
- `git status --short` contains no generated schema, fixture, manifest, fingerprint, compose, bootstrap, or CI workflow changes for this feature.

## 8. Caller Audit

Before completion, read—not merely run—the legacy callers:

```bash
rg -n '(/flocks|/customers|listFlocks|listCustomers)' \
  web/src tools/simulation/k6 tools/simulation/ui tests src
```

Confirm these existing forms still behave as before:

- `/flocks`
- `/flocks?includeArchived=true`
- `/flocks?limit=500`
- `/customers`
- `/customers?limit=500`

No k6 or seeder write payload change is expected because this feature adds optional read parameters and additive response fields only.
