# Phase 0 Research: Searchable Paged Entity Picker

All technical-context questions are resolved. No `NEEDS CLARIFICATION` items remain.

## 1. Extend Existing List Contracts

**Decision**: Add optional `search` to `GET /api/v1/flocks` and `GET /api/v1/customers`; add optional `eligibility` only to flocks; retain the bare-array response and existing `limit`/`offset` clamps.

**Rationale**: Both endpoints already provide stable `Name, Id` paging and exact-by-id routes. Extending them preserves existing callers and avoids duplicate picker-only endpoints. A bare array lets the adapters infer `hasMore` from a full 50-row page; a final empty request after an exact-full last page is acceptable. The no-repeat/no-skip guarantee applies while the ordered matching result set is unchanged between requests; concurrent inserts, deletes, or renames do not receive snapshot semantics from offset paging.

**Alternatives considered**:

- New picker endpoints: rejected because they duplicate authorization, tenant filtering, response types, and exact reads.
- A total-count/envelope response: rejected because it expands the compatibility surface and is not needed for incremental discovery.
- A temporary `limit=500`/larger ceiling: rejected because it merely moves the truncation defect.

## 2. Literal Case-Insensitive Search

**Decision**: Trim search once at the boundary; treat null/blank as no predicate; otherwise escape the escape character first, then `%` and `_`, wrap the result in `%...%`, and apply the three-argument Npgsql `EF.Functions.ILike` overload before ordering and paging.

**Rationale**: Npgsql translates `ILike(match, pattern, escape)` to server-side case-insensitive `ILIKE ... ESCAPE ...`, which exactly represents the approved literal-substring contract. Escaping before adding the surrounding wildcards prevents user input from changing the match language. [Npgsql documents the translation and escape overload](https://www.npgsql.org/efcore/mapping/translations.html).

**Alternatives considered**:

- `ToLower().Contains(...)`: rejected because collation/case behavior is less explicit and wildcard literal handling is not visible in the query contract.
- Client-side filtering: rejected because it breaks eligibility-before-paging, leaks incomplete pages, and scales with the full catalog.
- A new search index or stored normalized column: rejected until measurement shows a need; leading-wildcard substring search does not benefit from the current name b-tree.

## 3. Flock Eligibility and Compatibility

**Decision**: Introduce an Application read concept with exactly `active`, `active-and-depleted`, and `all` wire mappings. Omitted eligibility means Active + Depleted. Bind legacy `includeArchived` as nullable so presence is observable: `includeArchived=true` aliases `all` only when eligibility is absent; supplying both parameters or an unknown eligibility returns a validation 400.

**Rationale**: An enum-shaped concept prevents boolean combinations from spreading through repositories while manual wire parsing preserves exact lowercase/hyphen values and controlled errors. Nullable legacy binding is necessary because the contract rejects both keys even if `includeArchived=false`.

**Alternatives considered**:

- Keep only `includeArchived`: rejected because it cannot express Active-only assignment discovery.
- Multiple status booleans: rejected because invalid combinations multiply and page callers would own policy.
- Infer whether a non-nullable `false` was supplied: rejected because model binding loses parameter presence.

## 4. Exact Resolution and Defaults

**Decision**: Add SPA wrappers for existing `GET /flocks/{id}` and `GET /customers/{id}`. Resolve every external/deep-linked/remembered/edit/create identifier exactly before applying a default. Defaults are explicit page transitions, not side effects of whatever result page loaded first.

**Rationale**: Exact routes already enforce tenant, flock-scope, and role policy. Separating exact resolution from discovery keeps committed values named outside the current page and prevents missing or ineligible identifiers from silently becoming the first result.

**Alternatives considered**:

- Search loaded pages for the identifier: rejected because the selected entity may be outside them.
- Default first and replace later: rejected because it can submit the wrong identifier during the race.
- Add a batch exact endpoint for single pickers: rejected because each transition resolves at most one external value.

## 5. Row-Owned Current Display Data

**Decision**: Add current names/status to the six affected row contracts. Resolve distinct flock/customer references once per returned page using scoped bulk repository projections; bound flock movement aggregation to returned flock IDs. Keep assignments unpaged and return assignment plus nullable flock name from one scoped left join.

**Rationale**: Historical rows then remain understandable without depending on picker discovery. Bulk projections avoid N+1 reads and retain existing aggregates without adding navigation properties or stored name snapshots.

**Alternatives considered**:

- SPA lookup through picker options: rejected because paging recreates the defect.
- Per-row exact GETs: rejected because request count grows with row count.
- Persist names/status on historical rows: rejected because the requirement is the referenced entity's current display data and no migration is justified.
- Add domain navigation properties: rejected because display joins are read concerns and would add unnecessary aggregate coupling.

## 6. One Narrow Picker Engine, Two Typed Adapters

**Decision**: Build `NamedEntityPicker` as the single async state/interaction engine and expose it only through `FlockPicker` and `CustomerPicker`. Adapters fix the 50-row page and approximately 250 ms debounce and map typed list/exact calls; callers supply label, required/optional/disabled state, eligibility where applicable, and an explicit transition intent.

**Rationale**: Eleven selectors need identical discovery/race/a11y behavior, while typed adapters prevent a generic catalog framework from becoming public architecture. Daily Entry still receives the full committed `Flock` needed for `farmId` and `houseId`.

**Alternatives considered**:

- Duplicate page-local async selectors: rejected because stale-request and keyboard fixes would diverge across 11 instances.
- Reuse `usePagedList` unchanged: rejected because a picker needs separate raw-query debounce, committed value, active option, exact resolution, and exploration state.
- A universal catalog/entity-link system: rejected as explicit scope expansion.

## 7. Two Independent Generations

**Decision**: Maintain a discovery generation and a selection-transition generation. Discovery increments synchronously on raw query/eligibility change and owns rows/loading/errors through success, failure, and finally. Selection transition increments for URL navigation, dialog open/close, edit/reset, exact/default resolution, post-create hydration, and explicit commit. Every asynchronous continuation checks its generation before committing.

**Rationale**: One request counter cannot answer both “which result window is current?” and “which external/default/user selection is current?”. Immediate generation changes also hide stale rows before the debounce or React effects start.

**Alternatives considered**:

- AbortController alone: rejected because cancellation is advisory and late catch/finally handlers still require ownership checks.
- A single generation: rejected because discovery can be superseded without invalidating a committed selection, and lifecycle transitions can race independently of search.
- Effect-only stale hiding: rejected because old rows remain visible for at least one render after URL/raw identity changes.

## 8. Accessibility and Keyboard Model

**Decision**: Follow the WAI-ARIA editable combobox with listbox popup pattern: DOM focus remains on the labeled input; `aria-controls` associates the popup; stable option IDs drive `aria-activedescendant`; Arrow keys move the active option; Enter/click commits; Escape restores; native text editing/Home/End remain unhandled. Use a stable polite live region and an adjacent focus-restoring Retry button.

**Rationale**: The [WAI-ARIA Authoring Practices combobox pattern](https://www.w3.org/WAI/ARIA/apg/patterns/combobox/) explicitly preserves DOM focus on the combobox and warns implementers not to interfere with browser text-editing keys. This matches the issue's keyboard and assistive-technology contract.

**Alternatives considered**:

- Move DOM focus into each option: rejected because it breaks editable input behavior and the required `aria-activedescendant` model.
- Native `<select>`: rejected because it cannot perform async literal search/paging/retry.
- A custom dialog/portal: rejected because no modal interaction is required and existing dialog/inert infrastructure remains separate.

## 9. Page-Owned Lifecycle and Sales URL State

**Decision**: Keep business side effects in their pages: Daily Entry retains atomic deep-link precedence and `retarget()` disarming; Water retains exact Archived values while disabled; Users retains dialog generation and defaults only after successful open; Expenses retains None; Sales moves `customerId` to canonical URL state, validates canonical GUID shape, preserves unrelated keys, and synchronously keys/hides rows and headings on identity changes.

**Rationale**: The picker should own selection mechanics, not page business rules. Existing page race guards and idempotency scopes are load-bearing. URL ownership makes Sales direct links, reloads, and Back/Forward deterministic.

**Alternatives considered**:

- Move page lifecycle into the shared picker: rejected because it would couple unrelated workflows and create the forbidden generic framework.
- Keep a second local Sales filter state: rejected because URL and visible results can drift.
- Rely only on `usePagedList.reloading`: rejected because it becomes true in an effect and cannot provide synchronous stale-row hiding.

## 10. Verification, Fixture, CI, and Documentation

**Decision**: Use real-Postgres discovery/scope/query-count tests, shared component race/a11y tests, focused tests for all adopting pages and links, and one built-SPA scenario over #627's existing sentinels. Add a focused E2E mutant proving pagination coverage. The scenario joins the already-configured PR smoke suite without workflow changes. Update Help, the in-app glossary, en/es/tl catalogs, and the product glossary.

**Rationale**: Each boundary proves a different failure class. #627 already certifies exactly 102 flocks and 101 customers, so new fixture work would duplicate ownership. Repository history shows PR smoke CI predates issue #512; the user confirmed current CI policy should prevail. Constitution IV requires causal evidence and caller inspection, while workflow gate 4 requires all user-visible glossary surfaces.

**Alternatives considered**:

- Seven duplicate E2Es: rejected because component/page tests cover variations more directly and cheaply.
- Manual-only picker E2E: rejected because it conflicts with the current owner-approved quick-smoke PR gate and introduces special configuration.
- Fixture/count/fingerprint changes: rejected because #627 owns and already supplies the needed late-sorting sentinels.
- Skip the product glossary unless wording says “searchable picker”: rejected because the constitution requires user-visible concept changes to update it.
