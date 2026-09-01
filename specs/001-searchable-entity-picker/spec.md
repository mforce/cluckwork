# Feature Specification: Searchable Paged Entity Picker

**Feature Branch**: `001-searchable-entity-picker`

**Created**: 2026-08-31

**Status**: Draft

**Input**: User description: "Cluckwork repository rules in `AGENTS.md` and GitHub issue #512, Add a reusable searchable paged entity picker for flock and customer selectors"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Find and Select Any Eligible Name (Priority: P1)

A farm user can search, browse, and select any eligible flock or customer on every affected workflow, including names that sort beyond the first group of results. The user sees names rather than identifier fragments, and the selected value remains recognizable even when it is not in the currently visible results.

**Why this priority**: The current truncated selectors prevent valid work whenever the needed flock or customer is not in the first name-sorted result group. Restoring complete selection is the core business outcome.

**Independent Test**: Populate more than 50 eligible flocks and customers, place known names after the first 50 in stable sort order, and verify that a user can find and commit those names on each applicable workflow without changing unrelated page behavior.

**Acceptance Scenarios**:

1. **Given** an eligible flock or customer sorts after the first 50 names, **When** the user searches for the name or requests more results, **Then** the user can see and commit that entity.
2. **Given** multiple eligible entities have the same name, **When** the user browses across result groups, **Then** every matching entity appears once in a consistent order and no entity is skipped or repeated.
3. **Given** the user enters leading or trailing spaces or different letter casing, **When** results are found, **Then** matching uses the trimmed text without regard to case.
4. **Given** the user enters `%`, `_`, or the configured escape character, **When** results are found, **Then** those characters are matched literally rather than treated as pattern operators.
5. **Given** a committed entity is outside the current result group, **When** the picker opens or the search changes, **Then** the committed entity remains fully named and selected until the user commits a different choice or clears an optional choice.

---

### User Story 2 - Explore and Commit Without Accidental Writes (Priority: P1)

A user can type, browse, and navigate results without silently changing the committed value or accidentally submitting a stale value. The interface distinguishes exploration from selection and gives the user clear ways to commit, cancel, clear, or recover.

**Why this priority**: A selector that exposes all names but submits an old or unintended identifier can create records against the wrong flock or customer, which is more damaging than a visible inability to select.

**Independent Test**: Start with a committed value, enter a different search, exercise keyboard and pointer navigation, and verify that write actions remain blocked until the user commits, cancels, or clears the exploration.

**Acceptance Scenarios**:

1. **Given** visible search text differs from the committed selection, **When** the user attempts Save, Create, or Assign, **Then** the action does not execute with the old committed value and the user is told that selection is still being explored.
2. **Given** the user is exploring results, **When** they press Escape, **Then** the committed label is restored and the prior committed value remains unchanged.
3. **Given** the user moves through options with Arrow keys, **When** an option becomes active, **Then** the committed value does not change until Enter or a pointer click commits it.
4. **Given** the user reaches the last loaded option with Down Arrow and more results exist, **When** they continue downward, **Then** the next result group is requested without disrupting normal text editing, Home, or End behavior.
5. **Given** an outside interaction cancels exploration, **When** that interaction is also associated with a write action, **Then** it may cancel exploration but cannot submit in the same interaction.

---

### User Story 3 - Recover From Loading and Availability Changes (Priority: P2)

A user sees clear translated feedback while results are loading, when there are no matches, when a request fails, or when an externally supplied selection is unavailable. The user can retry without losing a valid committed selection or repeating a business action that already succeeded.

**Why this priority**: Search and exact-value resolution are asynchronous. Explicit, recoverable states prevent stale results, silent substitutions, and duplicate records during slow or failed requests.

**Independent Test**: Force delayed, failed, and out-of-order result and exact-value requests, then verify that only the newest intent is visible and that Retry recovers the failed operation without replaying a completed create.

**Acceptance Scenarios**:

1. **Given** a new search or eligibility intent is entered, **When** an older request succeeds or fails later, **Then** its rows, loading state, and error do not replace the newest intent's state.
2. **Given** a replacement request fails, **When** the failure is shown, **Then** stale rows from the prior query remain hidden and a keyboard-reachable Retry action is available.
3. **Given** loading an additional result group fails, **When** the failure is shown, **Then** already loaded results remain usable and the user can retry the extension.
4. **Given** an external identifier is missing, inaccessible, or not eligible for the current workflow, **When** it is resolved, **Then** an explicit unavailable state is shown and the first result is not silently substituted.
5. **Given** record creation succeeded but resolving its display name failed, **When** the user retries, **Then** only name resolution is retried and the completed creation is not repeated.

---

### User Story 4 - Read Historical Rows Independently of Picker Results (Priority: P2)

A user reviewing daily entries, feed usage, water usage, worker assignments, sales orders, or expenses always sees the referenced current display name, and relevant flock status, regardless of which picker results happen to be loaded.

**Why this priority**: Historical records must remain understandable even when their referenced flock or customer is archived, inaccessible to new selection, or outside the current result group.

**Independent Test**: Return rows referencing names outside the first 50 selectable results and verify that every row displays its scoped name and required status without identifier fragments or one-request-per-row behavior.

**Acceptance Scenarios**:

1. **Given** a returned row references an entity outside the current picker results, **When** the row is displayed, **Then** it shows the entity's current name rather than an identifier fragment.
2. **Given** a historical row references an Archived or Depleted flock, **When** the row is displayed, **Then** the flock remains named even if it cannot be newly selected in that workflow.
3. **Given** a farm-wide worker assignment or an expense with no flock, **When** it is displayed, **Then** the absence of a flock is represented as the workflow's intended blank value rather than as an error.
4. **Given** a daily-entry row is displayed, **When** editability is evaluated, **Then** it uses the row's own flock status rather than the status of any loaded picker option.

---

### User Story 5 - Navigate Into Customer-Filtered Sales (Priority: P3)

An authorized user can follow a customer name from Customers or Dashboard into Sales and receive a stable, shareable customer filter that survives reload and browser navigation.

**Why this priority**: Direct customer-to-sales navigation saves re-entry and makes the selected filter predictable across links, reloads, and Back/Forward navigation.

**Independent Test**: Follow a customer-name link, reload, edit unrelated query values, clear the customer, and use Back/Forward while verifying that the URL and visible Sales results always agree.

**Acceptance Scenarios**:

1. **Given** an authorized customer name on Customers or Dashboard, **When** the user follows it, **Then** Sales opens with the canonical customer identifier in `customerId` and filters to that customer.
2. **Given** unrelated Sales query values already exist, **When** a customer is selected or cleared, **Then** those unrelated values are preserved while `customerId` is added, changed, or removed.
3. **Given** the user reloads or uses browser Back or Forward, **When** the URL customer identity changes, **Then** the picker, heading, and Sales results follow the URL as their source of truth.
4. **Given** the URL contains a malformed customer identifier, **When** Sales loads, **Then** it is treated as absent and is not used to request filtered results.
5. **Given** the URL contains a well-formed but missing or inaccessible customer identifier, **When** Sales loads, **Then** an explicit unavailable-filter state offers Retry and Clear and is not silently rewritten to All.
6. **Given** the URL customer identity changes, **When** replacement results have not yet arrived, **Then** rows and row-derived headings from the old identity are hidden immediately.

### Edge Cases

- Blank or whitespace-only search behaves as an unfiltered search.
- Search eligibility is applied before ordering and paging so a page cannot be shortened by post-page filtering.
- Result ordering is stable by name and then canonical identifier, including duplicate names and transitions between pages.
- A result set with exactly 50 items, no further items, or an additional empty group does not offer an endless Load more loop.
- A newly entered raw query hides prior-query results immediately, including during the short typing pause before discovery begins.
- Late success, failure, and completion signals from superseded searches cannot change visible rows, loading, or error state.
- Navigation, dialog open/close, edit/reset, exact-value recovery, default selection, and explicit user selection can overlap; only the newest page-transition intent may commit a selection.
- Required pickers cannot be cleared; optional pickers can be cleared to their workflow-specific blank meaning.
- Disabled pickers remain non-interactive while retaining a fully named existing value, including an Archived flock on an existing Water edit.
- The user can recover from both replacement and extension failures using only a keyboard, and focus returns to the picker input after Retry.
- Tenant, flock-scope, and role restrictions continue to exclude inaccessible entities from search, exact resolution, and row display.
- Existing callers that do not request search or explicit eligibility continue to receive their current results and paging limits.

## Requirements *(mandatory)*

### Functional Requirements

#### Discovery, Search, and Compatibility

- **FR-001**: The system MUST provide one consistent named-entity selection experience for flocks and customers, adapted to each workflow's entity type and eligibility rules.
- **FR-002**: The system MUST search flock and customer names using trimmed, case-insensitive literal substring matching.
- **FR-003**: Missing, blank, or whitespace-only search text MUST produce unfiltered eligible results.
- **FR-004**: Percent signs, underscores, and the search escape character MUST be treated as ordinary literal characters.
- **FR-005**: Search and flock eligibility MUST be applied within the current tenant, flock-scope, and authorization boundaries before results are ordered or divided into groups.
- **FR-006**: Results MUST be ordered consistently by name and then canonical identifier before paging.
- **FR-007**: Picker result groups MUST contain at most 50 entities, and the picker MUST allow users to request subsequent groups until all matching results have been reached.
- **FR-008**: The picker MUST wait 250 milliseconds after typing settles before starting replacement discovery, while hiding rows from the prior raw query immediately.
- **FR-009**: Flock discovery MUST support exactly three eligibility policies: Active only, Active and Depleted, and all statuses.
- **FR-010**: When no explicit flock eligibility is supplied, the existing Active and Depleted behavior MUST remain unchanged.
- **FR-011**: The existing archived-inclusion choice MUST continue to mean all statuses only when no explicit eligibility choice is supplied; combining both choices or supplying an unknown eligibility MUST be rejected as an invalid request.
- **FR-012**: Existing result-size and starting-position bounds, and existing callers that omit search and explicit eligibility, MUST remain compatible.
- **FR-013**: Search MUST use the existing data model unless measured evidence demonstrates that an additional search structure is necessary.

#### Selection State and Recovery

- **FR-014**: The picker MUST own result discovery, request ordering, keyboard interaction, exploration state, active option, and committed selection.
- **FR-015**: Only the newest raw search and eligibility intent MUST be allowed to change visible rows, loading state, or errors.
- **FR-016**: A new raw query MUST immediately hide rows belonging to the previous query, including during the typing pause.
- **FR-017**: Only the newest page-transition intent MUST be allowed to commit exact resolution, default selection, dialog lifecycle changes, navigation changes, create recovery, or explicit user selection.
- **FR-018**: A committed value MUST remain independent of visible search text and retain its full display name when absent from the current result group.
- **FR-019**: Missing, inaccessible, or ineligible external identifiers MUST produce an explicit unavailable state and MUST NOT be replaced with the first result.
- **FR-020**: When visible input differs from the committed selection, the picker MUST report an exploring state and prevent Save, Create, or Assign from using the old committed identifier.
- **FR-021**: Exploration MUST end only when the user commits a choice, presses Escape to restore the committed label, or clears an optional picker.
- **FR-022**: An outside interaction that cancels exploration MUST NOT also submit a write in that same interaction.
- **FR-023**: Loading, no-results, replacement failure, extension failure, Retry, and Load more states MUST be visible and translated in every supported locale.
- **FR-024**: Retrying an extension failure MUST retain already loaded results; retrying a replacement failure MUST not reveal superseded results.
- **FR-025**: After a successful create, the system MUST preserve the created identifier and retry only exact display-name resolution; it MUST NOT repeat the create action.

#### Keyboard and Accessibility

- **FR-026**: Each picker MUST follow the WAI-ARIA editable combobox/listbox pattern with a visible label, stable option identifiers, and `aria-activedescendant` identifying the active option.
- **FR-027**: Loading and result-count changes MUST be announced without moving focus away from the input.
- **FR-028**: Retry MUST be adjacent to the failed picker, keyboard reachable, and return focus to the picker input after activation.
- **FR-029**: Disabled and required states MUST be represented consistently in both native interaction and accessibility semantics.
- **FR-030**: Arrow keys MUST move the active option without changing the committed value; Enter and pointer selection MUST commit the active choice; Escape MUST restore the committed label.
- **FR-031**: Down Arrow beyond loaded options MUST request more results when available, while Home, End, and normal text-editing keys retain their native input behavior.

#### Workflow Adoption

- **FR-032**: Daily Entry MUST allow Active and Depleted flocks, default to the first Active flock and then the first Depleted flock, and admit an exactly resolved deep-linked or remembered value instead of substituting another value.
- **FR-033**: History MUST allow all flock statuses, use blank to mean All, keep Archived rows named, and determine editability from the row's flock status.
- **FR-034**: Feed capture MUST allow Active and Depleted flocks and preserve its current Active-then-Depleted default; the Feed filter MUST allow all statuses.
- **FR-035**: Water capture MUST allow Active and Depleted flocks and preserve its current default; the Water filter MUST allow all statuses; a disabled existing edit MUST retain and display an Archived flock.
- **FR-036**: New User assignments MUST allow only Active flocks and default to the first Active flock after the worker dialog successfully opens; existing Depleted and Archived assignments MUST remain named but unavailable for new selection.
- **FR-037**: New Sales orders MUST search all customers and preserve the current first-customer default; the optional Sales customer filter MUST use blank to mean All and follow the URL-backed behavior in FR-045 through FR-050.
- **FR-038**: Expense record and edit workflows MUST allow all flock statuses, use blank to mean None, and retain resolvable Archived selections.

#### Row-Owned Display Data

- **FR-039**: Visible Daily Entry rows MUST carry and display the referenced flock's current name and status independently of picker results.
- **FR-040**: Visible Feed usage and Water usage rows MUST carry and display the referenced flock's current name independently of picker results.
- **FR-041**: Visible User assignments MUST carry and display a nullable flock name, with null representing a farm-wide assignment.
- **FR-042**: Visible Sales orders MUST carry and display the referenced customer's current name independently of picker results.
- **FR-043**: Visible Expenses MUST carry and display a nullable flock name, with null representing no flock.
- **FR-044**: Each returned page of rows MUST resolve its distinct referenced names within existing scope in one grouped lookup, without per-row requests or identifier-fragment fallbacks; flock movement summaries MUST be bounded to the flocks in the returned group; the existing unpaged assignment list MUST remain unpaged and resolve assignments and names together.

#### Customer Links and Sales URL State

- **FR-045**: Customer names on Customers and authorized Dashboard surfaces MUST link to `/sales?customerId=<canonical-id>`.
- **FR-046**: The canonical `customerId` URL value MUST be the source of truth for the Sales customer filter across direct navigation, reload, edits, and browser Back and Forward.
- **FR-047**: Selecting a customer MUST add or replace `customerId` while preserving unrelated query values; clearing the customer MUST remove only `customerId`.
- **FR-048**: A malformed customer identifier MUST be treated as absent and MUST NOT be used to request filtered Sales results.
- **FR-049**: A well-formed but missing or inaccessible customer identifier MUST remain an explicit unavailable-filter state with Retry and Clear and MUST NOT be rewritten to All.
- **FR-050**: When the URL customer identity changes, rows and row-derived headings for the previous identity MUST be hidden before replacement results are requested.

#### Scope and Documentation

- **FR-051**: User help and the in-app glossary in English, Spanish, and Tagalog MUST explain searchable paged pickers, keyboard selection, Load more, Retry, and customer-name links into filtered Sales.
- **FR-052**: If “searchable picker” becomes a named interface concept in final user-facing wording, the product glossary MUST define it.
- **FR-053**: This feature MUST NOT add generic picker, catalog, or entity-link frameworks; same-page Sales-row links; Dashboard or Flocks list ceiling changes; pickers for inventory items, products, categories, grades, or other catalogs; table or ledger paging; new write paths; or unrelated refactors.
- **FR-054**: This feature MUST reuse the existing shared over-cap simulation fixture unchanged and MUST NOT change its seed data, counts, manifest, fingerprint, or simulation-harness configuration.
- **FR-055**: The representative end-to-end scenario MUST join the already-configured pull-request smoke suite without new CI wiring and MUST NOT require duplicate end-to-end scenarios for every adopting page.

### Key Entities

- **Named Entity Option**: A selectable flock or customer represented by a canonical identifier and current display name; a flock also has a status used for eligibility.
- **Picker Intent**: The newest raw search text and eligibility choice that determines which result set may become visible.
- **Picker Result Group**: A stable, ordered subset of matching eligible named entities plus whether more results can be requested.
- **Committed Selection**: The canonical identifier and display label accepted by the user or by an admitted workflow default; it remains independent of exploratory text and visible result groups.
- **External Selection**: A deep-linked, remembered, edited, or newly created identifier that must be resolved exactly and either admitted or shown as unavailable.
- **Exploration State**: The period in which visible input differs from the committed selection and writes using the old value are prohibited.
- **Row Reference Display**: The current referenced name, and flock status where required, carried by a visible business row independently of picker options.
- **Sales Customer Filter**: An optional customer identity whose canonical identifier is represented in the Sales URL and controls filtered results and headings.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In datasets containing more than 50 eligible flocks or customers, 100% of eligible entities—including the last stably ordered entity—can be found and selected on every affected workflow.
- **SC-002**: Across Daily Entry, Feed, Water, Users, Sales, and Expenses, 100% of returned rows with an accessible reference display the current referenced name; zero rows fall back to an identifier fragment because the reference is outside loaded picker results.
- **SC-003**: In delayed and out-of-order request tests, zero superseded successes, failures, or completion signals alter the newest visible results, loading state, errors, or committed selection.
- **SC-004**: Keyboard-only users can search, load additional results, commit, cancel, clear where optional, and recover from both failure types on every picker without pointer input or focus loss.
- **SC-005**: In all six picker-backed write workflows—Daily Entry, Feed, Water, Users, Sales, and Expenses—100% of attempted writes during exploration are blocked until the user commits, cancels, or clears the pending exploration; History's picker remains a read-only list filter and does not block its unrelated adjustment actions.
- **SC-006**: Direct links, reloads, edits, and browser Back/Forward produce matching Sales URL, customer identity, headings, and rows in 100% of navigation scenarios, with no stale rows visible after identity changes.
- **SC-007**: Existing consumers that omit search and explicit eligibility pass all prior compatibility scenarios without changed default inclusion or paging behavior.
- **SC-008**: In representative usability verification, first-time keyboard and pointer users complete a late-sorting flock selection, a late-sorting customer selection, and one error recovery without assistance or selecting the wrong entity.
- **SC-009**: User-facing picker guidance is available in all three supported locales, and every loading, empty, failure, Retry, and Load more state has translated text.

## Assumptions

- Existing tenant, flock-scope, role, and authentication rules remain authoritative and are not expanded by this feature.
- Existing meanings of Active, Depleted, and Archived remain unchanged.
- Current page-specific defaults are intentional and must be preserved exactly as described in the workflow requirements.
- The short typing pause is a fixed picker policy rather than a page-level preference.
- Exact resolution may admit an existing value for display where the workflow allows retention, but it does not grant permission to select an otherwise ineligible value for a new record or assignment.
- Existing row list sizes and the intentionally unpaged assignment list remain unchanged.
- No migration or additional search index is justified without measured evidence.
- Generalized entity links, broad catalog picker adoption, and unrelated paging are separate future work.

### Dependencies

- Issue #627 lands first and owns the shared over-cap simulation fixture, exact counts, validation, manifest fingerprint, and its existing table and ledger coverage.
- This feature reuses #627's late-sorting flock and customer sentinels unchanged for one representative built-application scenario.
- The table and ledger paging work from issue #511 has already landed; there is no remaining technical dependency on it.
- Issue #509's five flock-selector defects are resolved by this feature and must not receive an interim high-limit workaround.
- Existing localization, customer, flock, row-list, and browser accessibility facilities remain available for adoption.
