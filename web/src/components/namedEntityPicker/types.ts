// #1215 file split — the picker's public contract types, moved verbatim out of
// NamedEntityPicker.tsx. Re-exported from that file; nothing here is new API.

// The shapes below mirror `specs/001-searchable-entity-picker/data-model.md`
// (Picker Transport Models, State Transitions) and `contracts/picker-ui.md`
// (Shared Snapshot). US1 lands discovery, paging, commit and retention;
// the US2/US3 selection-transition machinery (exact/default/lifecycle
// generations, unavailable states, Escape/clear, Retry) arrives in T026–T034
// on top of the same state.

// --- Eligibility policy ------------------------------------------------------

/**
 * The flock eligibility policies discovery supports (FR-009), keyed by the
 * three exact lowercase wire values of `GET /flocks?eligibility=`. Customers
 * have no eligibility choice; `DiscoveryState.eligibilityKey` is null there.
 */
export const FLOCK_ELIGIBILITIES = [
  "active",
  "active-and-depleted",
  "all",
] as const;
export type FlockEligibilityKey = (typeof FLOCK_ELIGIBILITIES)[number];

// --- Discovery transport -----------------------------------------------------

/**
 * One server window of a discovery response. The HTTP response is a bare
 * array, so this model is the picker's interpretation of one page: `serverCount`
 * is the raw number of rows the server returned (it drives the offset cursor —
 * never the deduplicated rendered count), and `hasMore` may be true after a
 * full 50-row page even when the next extension turns out empty (one final
 * empty request is how the picker learns paging is complete).
 */
export interface NamedEntityPage<T> {
  /** Stable server order (`ORDER BY Name, Id`); deduplicated by ID when appended. */
  items: T[];
  /** Row count returned by the server for THIS page, before deduplication. */
  serverCount: number;
  /** Sum of server counts across pages — the next `offset` to request. */
  nextOffset: number;
  /** True only when the last server page contained exactly 50 items. */
  hasMore: boolean;
}

/** The phases of one discovery (search + eligibility) generation. */
export type DiscoveryPhase =
  | "closed"
  | "debouncing"
  | "replacing"
  | "ready"
  | "empty"
  | "replacement-error"
  | "extending"
  | "extension-error";

/**
 * Search/result state, owned entirely by the current discovery generation.
 * A new raw query or eligibility change increments `discoveryGeneration` and
 * clears `items`/`error` immediately (old-query rows hide during the typing
 * pause too); only the newest generation may touch this state, including from
 * catch/finally.
 */
export interface DiscoveryState<T> {
  /** The visible, editable input text, exactly as typed. */
  rawQuery: string;
  /** The trimmed query; `null` for blank/whitespace-only (unfiltered search). */
  normalizedQuery: string | null;
  /** The adapter-owned flock eligibility policy; absent for customers. */
  eligibilityKey: FlockEligibilityKey | null;
  /** Rows owned by the current generation only. */
  items: T[];
  /** Offset cursor, advanced by raw server row counts. */
  cursor: number;
  /** Whether an extension (Load more) may be requested. */
  hasMore: boolean;
  phase: DiscoveryPhase;
  /** Replacement or extension error owned by the current generation. */
  error: Error | null;
  /** Monotonic intent token; incremented on every new query/eligibility. */
  discoveryGeneration: number;
}

// --- Selection transition ----------------------------------------------------

/** The phases of the committed-selection lifecycle. */
export type SelectionPhase =
  | "uninitialized"
  | "resolving"
  | "committed"
  | "blank"
  | "unavailable";

/**
 * The picker-owned committed or externally requested identity. `entity` holds
 * the full typed Flock/Customer once committed (it supplies the display name
 * and typed page data independently of which result group is visible);
 * `requestedId` carries an external identity that is still resolving or that
 * could not be resolved (`unavailable` — never silently substituted with the
 * first discovery result).
 */
export interface PickerSelection<T> {
  entity: T | null;
  /** External identity currently resolving or unavailable. */
  requestedId: string | null;
  phase: SelectionPhase;
  /**
   * Carries the request generation at the time of this selection transition.
   * Does NOT invalidate in-flight discovery requests: the controlled sync
   * carries the selection-transition generation at transition time. The
   * controlled sync preserves the current discovery generation rather than
   * bumping it, so a page-level hydration (deep link, default, edit) never
   * drops an in-flight `runReplacement` that the open effect just issued.
   */
  transitionGeneration: number;
}

// --- Snapshot (adapter-to-page contract) -------------------------------------

/**
 * Emitted to the page whenever committed or safety state changes. Pages use
 * `canSubmit` BOTH to disable visible write controls AND to guard their submit
 * handlers — a disabled button alone is not the write-safety boundary.
 */
export interface PickerSnapshot<T> {
  /** The committed entity, or null while uninitialized/resolving/unavailable. */
  committed: T | null;
  /** The current selection lifecycle phase. */
  selectionPhase: SelectionPhase;
  /**
   * True while the visible input differs from the committed label, or an
   * optional blank is being edited. While exploring, the old committed ID
   * must not be submitted.
   */
  exploring: boolean;
  /**
   * Required selection: committed and not exploring/resolving/unavailable.
   * Optional selection: blank or committed, and not exploring.
   */
  canSubmit: boolean;
}
