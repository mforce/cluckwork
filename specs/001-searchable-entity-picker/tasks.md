# Tasks: Searchable Paged Entity Picker

**Input**: Design documents from `specs/001-searchable-entity-picker/`

**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Tests are required by the specification. Within each story, add the failing tests before the corresponding implementation and prove each story at its checkpoint.

**Organization**: Tasks are grouped by user story. US1 and US2 are both P1 and together form the safe MVP: discovery without exploration/write guards is not deployable on write forms.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel with other `[P]` tasks in the same stage because it owns different files and has no unfinished dependency.
- **[Story]**: Maps the task to `US1` through `US5` from `spec.md`.
- Every task names the exact files it changes or the exact validation document it follows.

---

## Phase 1: Setup (Shared Baseline)

**Purpose**: Establish the known-green baseline and preserve the non-CI callers and shared fixture that constrain the implementation.

- [ ] T001 Run the pre-change focused API, frontend, and schema checks in `specs/001-searchable-entity-picker/quickstart.md`, and stop to diagnose any baseline failure before feature edits.
- [ ] T002 [P] Read every `/flocks`, `/customers`, `listFlocks`, and `listCustomers` caller under `web/src/`, `tools/simulation/k6/`, `tools/simulation/ui/`, `tests/`, and `src/`; verify the compatibility and unchanged-fixture constraints recorded in `specs/001-searchable-entity-picker/contracts/http-api.md` and `specs/001-searchable-entity-picker/quickstart.md`.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Define the narrow shared contracts used by every story without adding persistence, migrations, packages, or a generic catalog framework.

**⚠️ CRITICAL**: Complete this phase before starting story implementation.

- [ ] T003 Define the `FlockEligibility` read policy and scoped `FlockReference`/`CustomerReference` projections in `src/Cluckwork.Application/Features/Flocks/FlockEligibility.cs`, `src/Cluckwork.Application/Features/Flocks/IFlockRepository.cs`, and `src/Cluckwork.Application/Features/Customers/ICustomerRepository.cs` according to `specs/001-searchable-entity-picker/data-model.md`.
- [ ] T004 [P] Add the shared picker transport, selection-transition, and `PickerSnapshot<T>` TypeScript contracts to `web/src/components/NamedEntityPicker.tsx`, exposing no generic catalog API beyond this component.
- [ ] T005 [P] Add typed flock/customer list and exact-read request/response contracts, including the additive row fields, to `web/src/api/cluckwork.ts` without changing existing caller defaults.

**Checkpoint**: Application and SPA contracts compile; no schema, lockfile, fixture, harness, or workflow file has changed.

---

## Phase 3: User Story 1 - Find and Select Any Eligible Name (Priority: P1)

**Goal**: Users can search, page through, and commit every eligible flock or customer on all 11 adopting selectors while legacy list callers retain their existing behavior.

**Independent Test**: With more than 50 eligible flocks/customers and duplicate names, find and commit late-sorting entries through search and Load more on every adopting workflow; verify literal wildcard matching, stable `Name, Id` paging, and legacy request compatibility.

### Tests for User Story 1

> Write these tests first and confirm they fail for the intended missing behavior.

- [ ] T006 [P] [US1] Add failing real-Postgres discovery tests for blank/trimmed/case-insensitive literal search, escaped `%`/`_`/`\`, duplicate-name paging, eligibility-before-paging, invalid parameter combinations, legacy compatibility, tenant isolation, and Worker flock scope in `tests/Cluckwork.Api.IntegrationTests/NamedEntityDiscoveryTests.cs` and `tests/Cluckwork.Api.IntegrationTests/FlockScopeTests.cs`.
- [ ] T007 [P] [US1] Add failing client contract tests for 50-row flock/customer query serialization, offsets, search, eligibility, and unchanged legacy calls in `web/src/api/listFlocks.test.ts` and `web/src/api/listCustomers.test.ts`.
- [ ] T008 [P] [US1] Add failing component tests for debounce-triggered discovery, stable append/deduplication, raw server-count cursor advancement, final empty-page termination, pointer/Enter commit, and committed-label retention in `web/src/components/NamedEntityPicker.test.tsx`.
- [ ] T009 [P] [US1] Add failing adoption tests for the required eligibility/default/blank policies and late-page selection across `web/src/routes/DailyEntryPage.test.tsx`, `web/src/routes/HistoryPage.test.tsx`, `web/src/routes/FeedPage.test.tsx`, `web/src/routes/WaterPage.test.tsx`, `web/src/routes/UsersPage.test.tsx`, `web/src/routes/SalesPage.test.tsx`, and `web/src/routes/ExpensesPage.test.tsx`.

### Implementation for User Story 1

- [ ] T010 [P] [US1] Parse `search`, the three exact eligibility values, nullable legacy `includeArchived`, and conflicting/unknown validation failures in `src/Cluckwork.Api/Endpoints/Flocks/FlockEndpoints.cs` while retaining existing limit/offset clamps and bare-array responses.
- [ ] T011 [P] [US1] Implement scoped eligibility, trimmed literal three-argument `ILike`, escaped search patterns, stable `Name, Id` order, and server paging in `src/Cluckwork.Infrastructure/Repositories/FlockRepository.cs` and `src/Cluckwork.Application/Features/Flocks/IFlockRepository.cs`.
- [ ] T012 [P] [US1] Implement scoped trimmed literal customer search and stable `Name, Id` paging in `src/Cluckwork.Infrastructure/Repositories/CustomerRepository.cs`, `src/Cluckwork.Application/Features/Customers/ICustomerRepository.cs`, and `src/Cluckwork.Api/Endpoints/Customers/CustomerEndpoints.cs`.
- [ ] T013 [US1] Implement the typed 50-row list helpers and compatibility-preserving query serialization in `web/src/api/cluckwork.ts`, making `web/src/api/listFlocks.test.ts` and `web/src/api/listCustomers.test.ts` pass.
- [ ] T014 [US1] Implement the base debounced discovery, replacement paging, append/deduplication, Load more, active option, and commit behavior in `web/src/components/NamedEntityPicker.tsx` and style it with existing tokens in `web/src/styles.css`.
- [ ] T015 [US1] Implement the fixed-policy typed adapters in `web/src/components/FlockPicker.tsx` and `web/src/components/CustomerPicker.tsx`, returning full typed committed entities and `PickerSnapshot<T>`.
- [ ] T016 [P] [US1] Replace the Daily Entry capture and History filter flock selectors with `FlockPicker` using their specified eligibility/default/blank policies in `web/src/routes/DailyEntryPage.tsx` and `web/src/routes/HistoryPage.tsx`.
- [ ] T017 [P] [US1] Replace the Feed capture and filter flock selectors with `FlockPicker` while leaving the inventory-item selector native in `web/src/routes/FeedPage.tsx`.
- [ ] T018 [P] [US1] Replace the Water capture and filter flock selectors with `FlockPicker` while leaving source/unit selectors native in `web/src/routes/WaterPage.tsx`.
- [ ] T019 [P] [US1] Replace the new-assignment flock selector with the Active-only `FlockPicker` while leaving the role selector native in `web/src/routes/UsersPage.tsx`.
- [ ] T020 [P] [US1] Replace the new-order and optional filter customer selectors with `CustomerPicker` while leaving product/unit/payment/status selectors native in `web/src/routes/SalesPage.tsx`.
- [ ] T021 [P] [US1] Replace the record/edit optional flock selectors with all-status `FlockPicker` while leaving category/month selectors native in `web/src/routes/ExpensesPage.tsx`.
- [ ] T022 [US1] Run the US1-focused commands in sections 2 and 3 of `specs/001-searchable-entity-picker/quickstart.md`; confirm every new test is green and legacy `/flocks` and `/customers` forms remain unchanged.

**Checkpoint**: Every adopting selector can reach and commit late-sorting eligible entities, but do not ship write forms until US2 is complete.

---

## Phase 4: User Story 2 - Explore and Commit Without Accidental Writes (Priority: P1)

**Goal**: Typing and option navigation remain exploratory until an explicit commit, and no write can submit an old committed ID during exploration or an outside-click cancellation.

**Independent Test**: Begin with a committed entity, type another query, navigate with keyboard and pointer, attempt each write action, and prove that only commit/cancel/optional clear re-enables submission.

### Tests for User Story 2

> Write these tests first and confirm they fail for the intended missing behavior.

- [ ] T023 [P] [US2] Add failing component tests for committed-versus-visible state, Arrow-only activation, Enter/click commit, Escape restore, optional clear, required/disabled semantics, native Home/End editing, and outside-write suppression in `web/src/components/NamedEntityPicker.test.tsx`.
- [ ] T024 [P] [US2] Add failing write-guard tests for Daily Entry, Feed, Water, and Users in `web/src/routes/DailyEntryPage.test.tsx`, `web/src/routes/FeedPage.test.tsx`, `web/src/routes/WaterPage.test.tsx`, and `web/src/routes/UsersPage.test.tsx`.
- [ ] T025 [P] [US2] Add failing write-guard tests for Sales and Expenses, including direct submit-handler rejection when the visible button state is bypassed, in `web/src/routes/SalesPage.test.tsx` and `web/src/routes/ExpensesPage.test.tsx`.

### Implementation for User Story 2

- [ ] T026 [US2] Implement independent committed text/entity state, exploration detection, keyboard activation, Escape/clear behavior, outside-write suppression, and derived `canSubmit` in `web/src/components/NamedEntityPicker.tsx`.
- [ ] T027 [P] [US2] Consume `PickerSnapshot.canSubmit` in both control disabled states and submit-handler guards for Daily Entry, Feed, and Water in `web/src/routes/DailyEntryPage.tsx`, `web/src/routes/FeedPage.tsx`, and `web/src/routes/WaterPage.tsx`.
- [ ] T028 [P] [US2] Consume `PickerSnapshot.canSubmit` in both control disabled states and submit-handler guards for Users, Sales, and Expenses in `web/src/routes/UsersPage.tsx`, `web/src/routes/SalesPage.tsx`, and `web/src/routes/ExpensesPage.tsx`.
- [ ] T029 [US2] Run the picker and seven adopting-page test commands in section 3 of `specs/001-searchable-entity-picker/quickstart.md`, including keyboard-only and outside-click write attempts.

**Checkpoint**: US1 + US2 is the minimum safely shippable picker slice; discovery is complete and write forms cannot use stale committed identifiers.

---

## Phase 5: User Story 3 - Recover From Loading and Availability Changes (Priority: P2)

**Goal**: The picker rejects stale async completions, exposes translated replacement/extension recovery, exactly resolves external/default/create identities, and never repeats a successful create.

**Independent Test**: Force out-of-order successes/failures/finally paths and unavailable IDs; verify only the newest discovery/selection transition commits and each Retry repeats only its failed read operation.

### Tests for User Story 3

> Write these tests first and confirm they fail for the intended missing behavior.

- [ ] T030 [P] [US3] Add failing fake-timer/deferred-promise tests for immediate old-query hiding, discovery generation ownership across success/error/finally, replacement versus extension retry, focus restoration, live announcements, and exact/default transition races in `web/src/components/NamedEntityPicker.test.tsx`.
- [ ] T031 [P] [US3] Add failing lifecycle tests for Daily Entry deep-link precedence and GET-only post-create hydration, Water Archived disabled edit/reset, and Users dialog open/close/default generations in `web/src/routes/DailyEntryPage.test.tsx`, `web/src/routes/WaterPage.test.tsx`, and `web/src/routes/UsersPage.test.tsx`.
- [ ] T032 [P] [US3] Add failing exact-resolution and unavailable-state tests for History, Feed, Sales new order, and Expenses edit in `web/src/routes/HistoryPage.test.tsx`, `web/src/routes/FeedPage.test.tsx`, `web/src/routes/SalesPage.test.tsx`, and `web/src/routes/ExpensesPage.test.tsx`.
- [ ] T033 [P] [US3] Add failing exact flock/customer client tests for scoped 404/error propagation and GET-only retry behavior in `web/src/api/listFlocks.test.ts` and `web/src/api/listCustomers.test.ts`.

### Implementation for User Story 3

- [ ] T034 [US3] Implement independent discovery and selection-transition generations, immediate stale-row hiding, replacement/extension error states, owned catch/finally handling, Retry focus, and live-region announcements in `web/src/components/NamedEntityPicker.tsx`.
- [ ] T035 [US3] Implement exact-ID and explicit blank/first/default transitions plus admission rules in `web/src/components/FlockPicker.tsx`, `web/src/components/CustomerPicker.tsx`, and `web/src/api/cluckwork.ts`.
- [ ] T036 [P] [US3] Preserve atomic deep-link/remembered/default precedence, `retarget()` grading disarm, created ID retention, and GET-only hydration retry in `web/src/routes/DailyEntryPage.tsx`.
- [ ] T037 [P] [US3] Preserve exact Archived values while Water edit is disabled and preserve Users dialog-generation/default timing in `web/src/routes/WaterPage.tsx` and `web/src/routes/UsersPage.tsx`.
- [ ] T038 [P] [US3] Implement exact-value retention and explicit unavailable states for History, Feed, and Expenses lifecycles in `web/src/routes/HistoryPage.tsx`, `web/src/routes/FeedPage.tsx`, and `web/src/routes/ExpensesPage.tsx`.
- [ ] T039 [P] [US3] Implement explicit first-customer default and unavailable exact hydration for new Sales orders without introducing URL filter ownership yet in `web/src/routes/SalesPage.tsx`.
- [ ] T040 [US3] Add strict English/Spanish/Tagalog picker state, Load more, Retry, unavailable, and exploration strings in `web/src/i18n/en.ts`, `web/src/i18n/es.ts`, and `web/src/i18n/tl.ts`, and enforce parity in `web/src/i18n/catalogParity.test.ts`.
- [ ] T041 [US3] Run the picker/API/page tests in section 3 of `specs/001-searchable-entity-picker/quickstart.md` with fake timers and deliberately out-of-order completions; confirm a successful create POST is observed exactly once.

**Checkpoint**: Search, selection, lifecycle, and retry state machines are fail-closed under delay, failure, and supersession.

---

## Phase 6: User Story 4 - Read Historical Rows Independently of Picker Results (Priority: P2)

**Goal**: Each affected row carries its own current scoped reference name/status through bounded grouped reads, with no picker lookup, ID fragment, or per-row query behavior.

**Independent Test**: Return rows whose references are outside the first picker page or Archived; verify all six contracts render their own names/status and query count remains constant as row count grows.

### Tests for User Story 4

> Write these tests first and confirm they fail for the intended missing behavior.

- [ ] T042 [P] [US4] Add failing real-Postgres tests for all six additive row contracts, Archived/nullable cases, tenant and Worker scope, constant grouped-reference query count, one assignment left join, and bounded movement aggregation in `tests/Cluckwork.Api.IntegrationTests/NamedRowProjectionTests.cs` and `tests/Cluckwork.Api.IntegrationTests/FlockScopeTests.cs`.
- [ ] T043 [P] [US4] Add failing row-owned display/editability tests in `web/src/routes/DailyEntryPage.test.tsx`, `web/src/routes/HistoryPage.test.tsx`, `web/src/routes/FeedPage.test.tsx`, `web/src/routes/WaterPage.test.tsx`, `web/src/routes/UsersPage.test.tsx`, `web/src/routes/SalesPage.test.tsx`, `web/src/routes/ExpensesPage.test.tsx`, and `web/src/routes/Dashboard.test.tsx`.

### Implementation for User Story 4

- [ ] T044 [US4] Implement scoped bulk flock-reference reads and restrict movement aggregation to returned flock IDs in `src/Cluckwork.Application/Features/Flocks/IFlockRepository.cs`, `src/Cluckwork.Application/Features/Flocks/IBirdMovementRepository.cs`, `src/Cluckwork.Infrastructure/Repositories/FlockRepository.cs`, `src/Cluckwork.Infrastructure/Repositories/BirdMovementRepository.cs`, and `src/Cluckwork.Api/Endpoints/Flocks/FlockEndpoints.cs`.
- [ ] T045 [P] [US4] Add `flockName`/`flockStatus` to Daily Entry list/detail projection through one scoped bulk read in `src/Cluckwork.Api/Endpoints/DailyEntries/DailyEntryEndpoints.cs` and `src/Cluckwork.Infrastructure/Repositories/DailyEntryRepository.cs`.
- [ ] T046 [P] [US4] Add row-owned `flockName` to Feed and Water list projections in `src/Cluckwork.Api/Endpoints/Inventory/InventoryEndpoints.cs`, `src/Cluckwork.Api/Endpoints/Water/WaterUsageEndpoints.cs`, `src/Cluckwork.Infrastructure/Repositories/FeedUsageRepository.cs`, and `src/Cluckwork.Infrastructure/Repositories/WaterUsageRepository.cs`.
- [ ] T047 [P] [US4] Return nullable assignment `flockName` from one scoped left join while keeping the list unpaged in `src/Cluckwork.Application/Features/Users/IUserRoleAssignmentRepository.cs`, `src/Cluckwork.Infrastructure/Repositories/UserRoleAssignmentRepository.cs`, and `src/Cluckwork.Api/Endpoints/Users/UserEndpoints.cs`.
- [ ] T048 [P] [US4] Implement scoped bulk customer references and add `customerName` to Sales list/detail responses in `src/Cluckwork.Application/Features/Customers/ICustomerRepository.cs`, `src/Cluckwork.Infrastructure/Repositories/CustomerRepository.cs`, and `src/Cluckwork.Api/Endpoints/Sales/SaleEndpoints.cs`.
- [ ] T049 [P] [US4] Add nullable `flockName` to Expense list/detail/adjust responses through scoped grouped reads in `src/Cluckwork.Application/Features/Expenses/IExpenseRepositories.cs`, `src/Cluckwork.Infrastructure/Repositories/ExpenseRepositories.cs`, and `src/Cluckwork.Api/Endpoints/Expenses/ExpenseEndpoints.cs`.
- [ ] T050 [US4] Finish the six additive frontend response types and remove any ID-fragment reference fallback from `web/src/api/cluckwork.ts`.
- [ ] T051 [P] [US4] Render row-owned names/status and evaluate History editability from each row's `flockStatus` in `web/src/routes/DailyEntryPage.tsx`, `web/src/routes/HistoryPage.tsx`, `web/src/routes/FeedPage.tsx`, and `web/src/routes/WaterPage.tsx`.
- [ ] T052 [P] [US4] Render nullable/required row-owned names in Users, Sales, Expenses, and Dashboard and stop using picker/catalog results for display labels in `web/src/routes/UsersPage.tsx`, `web/src/routes/SalesPage.tsx`, `web/src/routes/ExpensesPage.tsx`, and `web/src/routes/Dashboard.tsx`.
- [ ] T053 [US4] Temporarily introduce a per-row reference lookup and an unbounded movement aggregation in `src/Cluckwork.Api/Endpoints/DailyEntries/DailyEntryEndpoints.cs` and `src/Cluckwork.Infrastructure/Repositories/BirdMovementRepository.cs`; prove `tests/Cluckwork.Api.IntegrationTests/NamedRowProjectionTests.cs` goes red for each mutant, restore the grouped/bounded implementation, and rerun green.
- [ ] T054 [US4] Run the focused PostgreSQL and page suites in sections 2 and 3 of `specs/001-searchable-entity-picker/quickstart.md` and record no ID-fragment fallback or row-count-dependent query growth.

**Checkpoint**: Historical rows are understandable and scoped independently of picker discovery, and the causal performance guards have survived their mutants.

---

## Phase 7: User Story 5 - Navigate Into Customer-Filtered Sales (Priority: P3)

**Goal**: Customer links enter Sales with a canonical URL-owned customer filter that remains synchronized across direct navigation, reload, edits, and browser history.

**Independent Test**: Follow Customers/Dashboard links, preserve unrelated parameters, select/clear customers, reload, and use Back/Forward; malformed IDs remain absent, unavailable canonical IDs expose Retry/Clear, and old rows hide synchronously.

### Tests for User Story 5

> Write these tests first and confirm they fail for the intended missing behavior.

- [ ] T055 [P] [US5] Add failing Sales tests for canonical GUID validation/normalization, URL source of truth, unrelated-query preservation, malformed absence, unavailable Retry/Clear, Back/Forward, and synchronous old-row/heading hiding in `web/src/routes/SalesPage.test.tsx`.
- [ ] T056 [P] [US5] Add failing authorized customer-name link tests and Dashboard catalog-removal coverage in `web/src/routes/CustomersPage.test.tsx` and `web/src/routes/Dashboard.test.tsx`.

### Implementation for User Story 5

- [ ] T057 [US5] Make canonical `customerId` in `URLSearchParams` the sole Sales filter identity, implement select/clear key preservation, malformed/unavailable handling, Retry, and synchronous row/heading invalidation in `web/src/routes/SalesPage.tsx`.
- [ ] T058 [P] [US5] Link authorized customer names to `/sales?customerId=<canonical-id>` and remove Dashboard's 500-customer naming fetch in `web/src/routes/CustomersPage.tsx` and `web/src/routes/Dashboard.tsx`.
- [ ] T059 [US5] Run `web/src/routes/SalesPage.test.tsx`, `web/src/routes/CustomersPage.test.tsx`, and `web/src/routes/Dashboard.test.tsx`, including simulated `popstate`/Back/Forward transitions and inaccessible customer responses.

**Checkpoint**: Sales URL, picker, headings, and rows always agree, and customer navigation is shareable and history-safe.

---

## Phase 8: Polish & Cross-Cutting Verification

**Purpose**: Complete user guidance, real-browser evidence, caller inspection, mutation proof, and full repository gates without broadening scope.

- [ ] T060 Add failing Help and glossary coverage for search, exploration/commit, keyboard use, Load more, Retry, unavailable identities, and customer links in `web/src/routes/HelpPage.test.tsx` and `web/src/i18n/catalogParity.test.ts`.
- [ ] T061 Update the Help page, in-app glossary, and product glossary in `web/src/routes/HelpPage.tsx`, `web/src/i18n/en.ts`, `web/src/i18n/es.ts`, `web/src/i18n/tl.ts`, and `specs/product/GLOSSARY.md` with synchronized user-facing terminology.
- [ ] T062 Add one translated built-SPA scenario that reaches and commits the unchanged #627 late-sorting flock/customer sentinels and exercises one recovery path in `tools/simulation/ui/specs/named-entity-picker.spec.ts`, using `tools/simulation/ui/src/ax.ts` only for accessibility-tree or `inert` assertions.
- [ ] T063 Register a focused pagination mutant in `tools/simulation/ui/src/mutants.ts` and `tools/simulation/ui/mutation-check.sh`; prove the baseline green, mutant red for the sentinel assertion, and restored baseline green without changing fixture, manifest, bootstrap, compose, or CI workflow files.
- [ ] T064 Re-run the caller audit in section 7 of `specs/001-searchable-entity-picker/quickstart.md` and inspect `web/src/`, `tools/simulation/k6/`, `tools/simulation/ui/`, `tests/`, and `src/` to confirm legacy list forms, write contracts, and non-adopting native selectors remain unchanged.
- [ ] T065 Run every static, focused, built-SPA, mutation, and full-gate command in `specs/001-searchable-entity-picker/quickstart.md`, including `dotnet test Cluckwork.sln`, frontend coverage/build/service-worker verification, schema-doc check, and the existing Playwright smoke suite.
- [ ] T066 Inspect `git diff --check` and `git status --short` and verify there are no migration, schema-doc, package/lockfile, #627 fixture/count/manifest/fingerprint, simulation harness, CI workflow, generic framework, or unrelated refactor changes.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: Starts immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1 and blocks all implementation stories.
- **Phase 3 (US1)**: Depends on Phase 2 and creates the discovery API, engine, adapters, and page adoption base.
- **Phase 4 (US2)**: Depends on the US1 picker/adoption base; together US1 + US2 are the safe MVP.
- **Phase 5 (US3)**: Depends on US1's async engine and US2's committed/exploration contract.
- **Phase 6 (US4)**: Backend projection work can begin after Phase 2 in parallel with US1-US3; frontend row integration is easiest after US1 adoption.
- **Phase 7 (US5)**: Depends on the CustomerPicker/exact-resolution path from US1/US3 and row-owned `customerName` from US4.
- **Phase 8 (Polish)**: Depends on every story selected for delivery; the E2E and full gates require all five stories.

### User Story Dependency Graph

```text
Setup -> Foundation -> US1 -> US2 -> US3
                      |              |
                      +----> US4 ----+----> US5

Safe MVP delivery gate: US1 + US2
Full feature gate: US1 + US2 + US3 + US4 + US5
```

### Within Each User Story

- Add the story's tests and observe the intended failures before implementing it.
- Implement Application contracts before Infrastructure queries, and Infrastructure queries before API projections.
- Implement the shared picker behavior before updating the pages that consume that behavior.
- Keep page-owned lifecycle/default/write behavior in the page; keep discovery, selection, and accessibility mechanics in the picker.
- Complete the story checkpoint before relying on it from a later story.

### Parallel Opportunities

- T002, T004, and T005 can run while the baseline or other contract setup work proceeds because they inspect/change distinct paths.
- In US1, T006-T009 can be authored in parallel; after the failing tests exist, T010-T012 can proceed in parallel; after T015, T016-T021 can proceed in parallel by page.
- In US2, component and route tests T023-T025 can be authored in parallel, then the two disjoint route groups T027-T028 can proceed after T026.
- In US3, T030-T033 can be authored in parallel, and page lifecycle implementations T036-T039 can proceed in parallel after T034-T035.
- US4 backend projection tasks T045-T049 are disjoint after T044 establishes shared reference reads; frontend tasks T051-T052 can then proceed in parallel.
- US4 backend work may proceed alongside US1-US3 frontend work after Foundation, provided changes to shared Application/API client contracts are coordinated.
- In US5, T055-T056 can be authored in parallel and T058 can proceed independently once row-owned Sales customer names exist.

## Parallel Examples

### User Story 1

```text
T006: PostgreSQL discovery/scope tests
T007: SPA API serialization tests
T008: Shared picker paging tests
T009: Seven page-adoption test files

After T015:
T016: Daily Entry + History adoption
T017: Feed adoption
T018: Water adoption
T019: Users adoption
T020: Sales adoption
T021: Expenses adoption
```

### User Story 4

```text
After T044:
T045: Daily Entry row projection
T046: Feed + Water row projections
T047: User assignment left join
T048: Sales customer projection
T049: Expense flock projection
```

## Implementation Strategy

### Safe MVP First

1. Complete Setup and Foundation.
2. Complete US1 and prove every eligible late-sorting name is reachable.
3. Complete US2 and prove every write is blocked during exploration.
4. Stop and validate the US1 + US2 checkpoint before exposing the picker on write forms.

### Incremental Delivery

1. **US1 + US2**: Safe searchable/paged selection and write protection.
2. **US3**: Race-safe exact/default resolution and recoverable async states.
3. **US4**: Row-owned current names/status with bounded grouped reads.
4. **US5**: URL-owned customer-to-Sales navigation.
5. **Polish**: Help/glossary, one PR-smoke scenario, mutants, caller audit, and full gates.

### Scope Guardrails

- Do not add a migration, index, package, lockfile change, generic picker/catalog/entity-link framework, new write route, same-page Sales-row link, or unrelated list/ledger paging.
- Do not change #627's seed data, counts, manifest, fingerprint, bootstrap, compose, or simulation-harness configuration.
- Do not add or edit CI workflow wiring; the representative scenario joins the already-configured PR smoke suite.
- Preserve every page's existing idempotency scope, business lifecycle side effects, and non-adopting native selectors.

## Notes

- `[P]` means file ownership is disjoint at that point in the graph, not that prerequisite tests may be skipped.
- Required references fail explicitly if scoped names cannot be resolved; they never fall back to identifier fragments.
- Exact reads admit an existing value only under the page's retention policy; they do not expand eligibility for new choices.
- A full 50-row last page may cause one final empty request; cursor advancement uses raw server count, not unique rendered count.
- Commit after each task or cohesive task group only when explicitly authorized.
