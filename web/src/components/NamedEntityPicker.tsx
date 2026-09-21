// #512 — the shared named-entity picker: transport/selection/snapshot
// contracts plus the one async discovery+commit engine they describe.
//
// THIS FILE IS THE ENGINE. Pages never import it: they consume the typed
// `FlockPicker` / `CustomerPicker` adapters (FlockPicker.tsx /
// CustomerPicker.tsx), and the engine itself is feature-local. Nothing
// outside this feature should import from this module, and it must never grow
// a generic catalog/entity-link API — the spec (FR-053) forbids a shared
// framework beyond the two typed adapters.
//
// The shapes below mirror `specs/001-searchable-entity-picker/data-model.md`
// (Picker Transport Models, State Transitions) and `contracts/picker-ui.md`
// (Shared Snapshot). US1 lands discovery, paging, commit and retention;
// the US2/US3 selection-transition machinery (exact/default/lifecycle
// generations, unavailable states, Escape/clear, Retry) arrives in T026–T034
// on top of the same state.

import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent, ReactNode } from "react";
import { useTranslation } from "react-i18next";
import Autocomplete from "@mui/material/Autocomplete";
import TextField from "@mui/material/TextField";
import Paper from "@mui/material/Paper";
import InputAdornment from "@mui/material/InputAdornment";
import Box from "@mui/material/Box";
import type { PaperProps } from "@mui/material/Paper";
import type { AutocompleteRenderInputParams } from "@mui/material/Autocomplete";
import { ChevronDown } from "lucide-react";

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

// --- The engine (T014) -------------------------------------------------------

// The adapter policy, fixed by the picker-ui contract: 50-row pages and the
// spec's exact 250 ms typing pause. Pages never configure either.
const PICKER_PAGE_SIZE = 50;
const PICKER_DEBOUNCE_MS = 250;

type NamedEntity = { id: string; name: string };

/** The discovery/selection state the engine owns; one object per render. */
interface EngineState<T extends NamedEntity> {
  discovery: DiscoveryState<T>;
  selection: PickerSelection<T>;
}

function initialState<T extends NamedEntity>(eligibilityKey: FlockEligibilityKey | null): EngineState<T> {
  return {
    discovery: {
      rawQuery: "",
      normalizedQuery: null,
      eligibilityKey,
      items: [],
      cursor: 0,
      hasMore: false,
      phase: "closed",
      error: null,
      discoveryGeneration: 0,
    },
    selection: { entity: null, requestedId: null, phase: "uninitialized", transitionGeneration: 0 },
  };
}

interface EngineProps<T extends NamedEntity> {
  /** Stable control id for the input (the label's htmlFor). */
  id: string;
  /** Visible, translated label text (page-owned translation). */
  label: string;
  /**
   * The page's trigger control (its native selector). Rendered in the CLOSED
   * state only (one field-sized control in the normal form slot). While open,
   * the trigger is absent and the combobox/listbox occupies the same position.
   */
  trigger: ReactNode;
  /**
   * The adapter's discovery fetch: one 50-row window. The engine supplies the
   * offset and the fixed page size; search/eligibility ride the adapter's
   * own typed parameters.
   */
  fetchPage: (search: string | null, offset: number) => Promise<T[]>;
  /**
   * US3 (T035): the adapter's EXACT-identity read. When the page passes a
   * full typed entity via `controlledCommitted` that is not already known to
   * the discovery window, the engine resolves it through THIS read (a GET),
   * never a list lookup — so a row-owned or late-sorting identity that is not
   * in the first 50 results still commits its exact entity. A scoped 404 /
   * transport failure enters the `unavailable` phase (never a first-result
   * substitution). Omitted for adapters that only ever commit from discovery.
   */
  fetchExact?: (id: string) => Promise<T>;
  /**
   * The adapter-owned eligibility policy. When it changes while the picker is
   * open, the engine invalidates old work and re-discovers under the new key.
   * Customers pass null.
   */
  eligibilityKey: FlockEligibilityKey | null;
  required?: boolean;
  disabled?: boolean;
  open?: boolean;
  onSnapshot: (snapshot: PickerSnapshot<T>) => void;
  /** US2: called on Escape to close the picker (page-controlled open state). */
  onEscape?: () => void;
  /** US2: called when the user clicks outside the picker (cancels exploration + close). */
  onOutsideClick?: () => void;
  /**
   * US2: page-controlled committed entity. When the page externally resets
   * its selection (deep link, URL change, dialog reset), it passes a new
   * entity (or null) here. The engine synchronizes its internal committed
   * state to match, so a later Escape or exploration cannot resurrect a
   * stale ID. `null` clears the committed state (optional pickers).
   */
  controlledCommitted?: T | null;
  /** Signals a controlled change (increment to re-trigger the sync effect). */
  controlledGeneration?: number;
  /**
   * US3 (T038): a row-owned identity the page must resolve but does not yet
   * have the full entity for (it is not in the page's capped list). The engine
   * resolves it through `fetchExact` on open / on the controlled sync — a
   * scoped 404 / transport failure enters `unavailable` (never a first-result
   * substitution). Omitted when the page already holds the full entity (use
   * `controlledCommitted` instead).
   */
  requestedId?: string | null;
  /**
   * US2: fires ONLY on genuine user commit (Enter or pointer click on an
   * option). This is the ONLY signal that should close the picker and trigger
   * page business side-effects (retarget, URL update, ID write). Distinct
   * from onSnapshot which fires on every state change for safety state.
   */
  onCommit?: (entity: T) => void;
  /** US2: fires when an optional picker is cleared (commits blank). */
  onClear?: () => void;
  /**
   * #916 — content pinned ABOVE the scrolling results, rendered as a sibling
   * of `<ul role="listbox">` inside the same `slots.paper` wrapper the
   * Load-more/status footer already uses (never inside `slotProps.listbox`,
   * where ARIA only allows `option`/`group`). Omitted by every caller except
   * one that needs a fixed "no filter" choice above the list; `Autocomplete`'s
   * own arrow-key navigation only ever traverses `options`, so this content
   * is Tab-reachable, not arrow-reachable.
   */
  pinnedChoice?: ReactNode;
}

/**
 * The one async picker engine. US1 scope: discovery (debounce, replacement,
 * extension, dedupe, raw-count cursor, final-empty termination), commit by
 * pointer/Enter, Arrow activation, committed-label retention (FR-018),
 * eligibility-change rediscovery, and disabled non-interactivity.
 * Selection-transition generations, unavailable states, Escape/clear, Retry,
 * and the full ARIA write-safety surface are US2/US3 (T026–T034).
 *
 * State discipline: a single `EngineState` object, updated ONLY through
 * functional `setState((prev) => …)` updaters that build on `prev` — never on
 * a ref snapshot of the painted state. The generation token (genRef) is the
 * only cross-await authority: every continuation re-checks it after every
 * await (success AND failure) before touching state.
 */
export function NamedEntityPickerEngine<T extends NamedEntity>({ id, label, trigger, fetchPage, fetchExact, eligibilityKey, required = false, disabled = false, open = false, onSnapshot, onEscape, onOutsideClick, controlledCommitted, controlledGeneration, requestedId, onCommit, onClear, pinnedChoice }: EngineProps<T>) {
  const onCommitRef = useRef(onCommit);
  onCommitRef.current = onCommit;
  const onClearRef = useRef(onClear);
  onClearRef.current = onClear;
  const { t } = useTranslation("namedEntityPicker");
  const [state, setState] = useState<EngineState<T>>(() => initialState(eligibilityKey));
  const stateRef = useRef(state);
  stateRef.current = state;
  // DISCOVERY generation: owns replacement/extension requests and every
  // phase/rows/cursor they commit. Only discovery intents (typing, eligibility
  // change, open/close, Load more, discovery Retry) increment it. A superseded
  // discovery's continuation (success AND failure) re-checks it after the
  // await — but a SELECTION transition never touches it, so committing an
  // option, clearing, Escape, an outside click, a controlled sync or a
  // requestedId/exact-Retry transition can never drop or wedge a discovery
  // that is still the newest discovery intent.
  const discoveryGenRef = useRef(0);
  // SELECTION-transition generation: owns the committed-selection lifecycle
  // (transitionGeneration, controlled sync, requestedId resolution, exact
  // Retry). It never gates discovery requests: discovery completions are
  // checked against discoveryGenRef only.
  const selectionGenRef = useRef(0);
  // The debounce timer is owned by the engine, not by `commit`, so commit can
  // cancel a pending replacement without a forward-reference.
  const debounceRef = useRef<number | null>(null);
  useEffect(() => () => {
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
  }, []);
  // The committed label, retained independently of the editable text (FR-018).
  // Only a commit writes it; typing never does.
  const [committedText, setCommittedText] = useState<string | null>(null);

  const commit = useCallback((entity: T) => {
    // Selection transition ONLY: the discovery window (rows, cursor, phase)
    // is retained for continued browsing, and a pending discovery keeps its
    // own generation — commit must not cancel or wedge it. The debounce is
    // still cancelled separately: a commit is a terminal selection intent for
    // the text being typed.
    selectionGenRef.current += 1;
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    // Commit stores the entity AND sets the visible text to the entity's name.
    // The input value IS discovery.rawQuery; after commit, rawQuery ===
    // committedName so exploring is immediately false. The discovery window
    // (items, cursor, hasMore) is RETAINED for continued browsing.
    setCommittedText(entity.name);
    setState((prev) => ({
      ...prev,
      selection: { entity, requestedId: null, phase: "committed", transitionGeneration: selectionGenRef.current },
      discovery: {
        ...prev.discovery,
        rawQuery: entity.name,
      },
    }));
    // US2: fire the explicit commit signal (genuine user intent only).
    onCommitRef.current?.(entity);
  }, []);

  // The discovery pipeline. Every entry point claims the generation BEFORE it
  // awaits, and every continuation re-checks it — including the failure path,
  // because a superseded request's rejection is as stale as its response.
  const runReplacement = useCallback(async (query: string | null, gen: number) => {
    // Set "replacing" immediately before fetching. The generation check after
    // the await is the sole staleness guard: if discoveryGenRef.current !==
    // gen, this request was superseded and its response (or rejection) is
    // dropped.
    setState((prev) => ({
      ...prev,
      discovery: { ...prev.discovery, phase: "replacing", items: [], error: null, discoveryGeneration: gen },
    }));
    let page: T[];
    try {
      page = await fetchPage(query, 0);
    } catch (err) {
      if (discoveryGenRef.current !== gen) return;
      setState((prev) => ({
        ...prev,
        discovery: { ...prev.discovery, phase: "replacement-error", error: err instanceof Error ? err : new Error(String(err)) },
      }));
      return;
    }
    if (discoveryGenRef.current !== gen) return;
    setState((prev) => ({
      ...prev,
      discovery: {
        ...prev.discovery,
        items: page,
        cursor: page.length,
        hasMore: page.length === PICKER_PAGE_SIZE,
        phase: page.length === 0 ? "empty" : "ready",
        error: null,
        discoveryGeneration: gen,
      },
    }));
  }, [fetchPage]);

  // An extension fetches the window at `cursor` under the current query. The
  // cursor is captured at call time from the PAINTED state (stateRef) — the
  // caller (loadMore) runs only from event handlers, i.e. after the triggering
  // page has painted, so the painted cursor is exactly the next window's offset.
  const runExtension = useCallback(async (gen: number) => {
    const cursor = stateRef.current.discovery.cursor;
    const query = stateRef.current.discovery.normalizedQuery;
    setState((prev) => ({
      ...prev,
      discovery: { ...prev.discovery, phase: "extending", discoveryGeneration: gen },
    }));
    let page: T[];
    try {
      page = await fetchPage(query, cursor);
    } catch (err) {
      if (discoveryGenRef.current !== gen) return;
      setState((prev) => ({
        ...prev,
        discovery: {
          ...prev.discovery,
          phase: "extension-error",
          error: err instanceof Error ? err : new Error(String(err)),
        },
      }));
      return;
    }
    if (discoveryGenRef.current !== gen) return;
    setState((prev) => {
      const seen = new Set(prev.discovery.items.map((x) => x.id));
      const items = [...prev.discovery.items, ...page.filter((x) => !seen.has(x.id))];
      return {
        ...prev,
        discovery: {
          ...prev.discovery,
          items,
          // The cursor advances by the RAW server page length, not the
          // deduplicated appended count. A server that repeats an ID across
          // pages (a race, a re-index) must not shrink the window: the next
          // offset is cursor + page.length, always.
          cursor: cursor + page.length,
          hasMore: page.length === PICKER_PAGE_SIZE,
          phase: "ready",
          error: null,
          discoveryGeneration: gen,
        },
      };
    });
  }, [fetchPage]);

  // Load more: an explicit user intent, so it claims a NEWER generation than
  // any pending replacement. One click = exactly one extension request
  // (data-model.md): a full page keeps `hasMore` true and the button stays
  // offered; a LATER explicit intent may fetch the final empty (or short)
  // page, which is what ends paging. The picker never probes automatically.
  const loadMore = useCallback(async () => {
    if (disabled) return;
    const gen = ++discoveryGenRef.current;
    await runExtension(gen);
  }, [runExtension, disabled]);

  // US3 (T034): Retry re-issues the FAILED operation under the current
  // generation, then restores focus to the input. Replacement errors re-run
  // the replacement (offset 0, current query); extension errors re-run the
  // extension at the PAINTED cursor — which is unchanged while the error is
  // shown, so the retry targets exactly the failed window (same query, same
  // cursor). Focus restoration is the keyboard contract (picker-ui.md).
  const retry = useCallback(async () => {
    if (disabled) return;
    const d = stateRef.current.discovery;
    const inputEl = document.getElementById(id);
    const gen = ++discoveryGenRef.current;
    if (d.phase === "replacement-error") {
      await runReplacement(d.normalizedQuery, gen);
    } else if (d.phase === "extension-error") {
      await runExtension(gen);
    } else {
      return;
    }
    inputEl?.focus();
  }, [disabled, id, runReplacement, runExtension]);

  // US3 (T034/T038): Retry the FAILED exact-identity read. The unavailable
  // identity is remembered (selection.requestedId); the read is re-issued
  // under a fresh generation, and focus returns to the input on success.
  const retryUnavailable = useCallback(async () => {
    // Deliberately NOT gated on `disabled` (US3 remediation): this re-resolves
    // a FIXED external identity via the exact GET, not ordinary discovery or
    // selection — a picker disabled for other reasons (e.g. Water's
    // edit-locked capture picker) must still be able to recover a row-owned
    // id whose exact read failed.
    const reqId = stateRef.current.selection.requestedId;
    const inputEl = document.getElementById(id);
    if (!reqId || !fetchExactRef.current) return;
    const gen = ++selectionGenRef.current;
    try {
      const resolved = await fetchExactRef.current(reqId);
      if (selectionGenRef.current !== gen) return;
      // Sync the discovery window to the resolved identity: the input value IS
      // rawQuery, so committing without it leaves stale text painted and
      // `exploring` true (canSubmit false) after a successful exact read.
      // Only rawQuery/activeId move — rows, cursor and error belong to the
      // discovery generation, which this transition never supersedes.
      setCommittedText(resolved.name);
      setState((prev) => ({
        ...prev,
        selection: { entity: resolved, requestedId: null, phase: "committed", transitionGeneration: gen },
        discovery: { ...prev.discovery, rawQuery: resolved.name },
      }));
    } catch {
      if (selectionGenRef.current !== gen) return;
      setState((prev) => ({
        ...prev,
        selection: { entity: null, requestedId: reqId, phase: "unavailable", transitionGeneration: gen },
      }));
    }
    inputEl?.focus();
  }, [id]);

  // Typing: the raw text hides the old rows IMMEDIATELY (FR-016), then the
  // exact 250 ms pause (FR-008) owns whether a replacement goes out. The
  // debounce timer is the newest-intent check: each keystroke cancels the
  // previous one, so only the final text ever requests.
  //
  // Takes the raw string directly (not a ChangeEvent): `Autocomplete`'s own
  // `onInputChange(event, value, reason)` is the only caller (#826 — the
  // combobox is `Autocomplete`'s, not a hand-rolled `<input onChange>`), and
  // it already hands over the string.
  const onQueryChange = useCallback((raw: string) => {
    if (disabled) return;
    const trimmed = raw.trim();
    const gen = ++discoveryGenRef.current;
    if (debounceRef.current !== null) window.clearTimeout(debounceRef.current);
    // Typing replaces `items`, so the highlighted option is gone with them.
    highlightedIdRef.current = null;
    setState((prev) => ({
      ...prev,
      discovery: {
        ...prev.discovery,
        rawQuery: raw,
        normalizedQuery: trimmed === "" ? null : trimmed,
        items: [],
        error: null,
        cursor: 0,
        hasMore: false,
        phase: "debouncing",
        discoveryGeneration: gen,
      },
    }));
    debounceRef.current = window.setTimeout(() => {
      debounceRef.current = null;
      void runReplacement(trimmed === "" ? null : trimmed, gen);
    }, PICKER_DEBOUNCE_MS);
  }, [runReplacement, disabled]);

  // Eligibility change: while open, hide old rows immediately, invalidate old
  // work (bump the generation so in-flight requests become stale), and
  // re-discover under the new eligibility after the fixed 250 ms debounce.
  // While closed, synchronize prevEligibilityRef and the state's eligibilityKey
  // WITHOUT requesting — the next open will issue exactly one request under the
  // current eligibility (the open effect checks phase === "closed", which is
  // preserved, so no stale immediate + debounced double-fetch).
  //
  // DECLARED BEFORE the open effect: React runs effects in declaration order,
  // so when eligibility changes and open=true in the same commit, this effect
  // runs first and sets `pendingEligibilityKeyRef` (the open effect reads it
  // and skips). This prevents the open effect from launching a stale request
  // under the old key. After the debounce fires, the pending ref is cleared so
  // a subsequent reopen is not incorrectly skipped.
  const prevEligibilityRef = useRef(eligibilityKey);
  const pendingEligibilityKeyRef = useRef<FlockEligibilityKey | null>(null);
  useEffect(() => {
    if (prevEligibilityRef.current === eligibilityKey) return;
    prevEligibilityRef.current = eligibilityKey;
    // Claim a DISCOVERY generation for this intent so in-flight discovery
    // work becomes stale and the state's discoveryGeneration is truthful.
    const gen = ++discoveryGenRef.current;
    if (!open) {
      // Closed: sync the key in state and reset the discovery window (items,
      // cursor, hasMore, phase → "closed") so that the next open issues exactly
      // one request under the CURRENT key.
      if (debounceRef.current !== null) {
        window.clearTimeout(debounceRef.current);
        debounceRef.current = null;
      }
      setState((prev) => ({
        ...prev,
        discovery: {
          ...prev.discovery,
          eligibilityKey,
          items: [],
          cursor: 0,
          hasMore: false,
          phase: "closed",
          discoveryGeneration: gen,
        },
      }));
      return;
    }
    // Open: hide rows, invalidate, and re-discover after the debounce.
    // Signal to the open effect (same commit) that the eligibility key just
    // changed — it should skip firing this commit.
    pendingEligibilityKeyRef.current = eligibilityKey;
    const query = stateRef.current.discovery.normalizedQuery;
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    setState((prev) => ({
      ...prev,
      discovery: {
        ...prev.discovery,
        eligibilityKey,
        items: [],
        error: null,
        cursor: 0,
        hasMore: false,
        phase: "debouncing",
        discoveryGeneration: gen,
      },
    }));
    debounceRef.current = window.setTimeout(() => {
      debounceRef.current = null;
      // Clear the pending signal: the debounce has fired, so the open effect
      // should not skip any FUTURE reopen (where the phase may be "closed").
      pendingEligibilityKeyRef.current = null;
      void runReplacement(query, gen);
    }, PICKER_DEBOUNCE_MS);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [eligibilityKey, open]);

  // Open (or first discovery): fetch the unfiltered first page. The eligibility
  // key is read from stateRef (not a closure) so that a closed-then-reopened
  // picker with a changed eligibility key issues the request under the CURRENT
  // key, not a stale one. When the key changed while closed, the eligibility
  // effect (declared above) has already synchronously set the phase to
  // "closed" (resetting the window), so this effect's "closed" check passes and
  // the fetch uses the updated key from state.
  //
  // When eligibility changes and open=true in the SAME commit, the eligibility
  // effect runs first (declaration order) and sets pendingEligibilityKeyRef.
  // This effect reads that ref and skips — the debounced replacement (scheduled
  // by the eligibility effect) handles the request under the new key.
  useEffect(() => {
    if (!open) {
      // `<Autocomplete>` remounts on every open with a clean highlight, but
      // this ref outlives it. Left stale, a reopen onto a retained window
      // (FR-018) whose end had been reached would treat the first ArrowDown
      // as `atEnd` and load a page instead of moving the highlight.
      highlightedIdRef.current = null;
      if (debounceRef.current !== null) {
        window.clearTimeout(debounceRef.current);
        debounceRef.current = null;
      }
      // US2 fix: if the picker closes before the eligibility debounce fires,
      // clear the pending signal so a future reopen is not suppressed. Also
      // reset the phase to "closed" if it was "debouncing" (close-before-
      // debounce must leave a reopenable state, not a wedge).
      pendingEligibilityKeyRef.current = null;
      if (stateRef.current.discovery.phase === "debouncing" || stateRef.current.discovery.phase === "replacing") {
        discoveryGenRef.current += 1;
        setState((prev) => ({
          ...prev,
          discovery: { ...prev.discovery, phase: "closed", items: [], hasMore: false, cursor: 0 },
        }));
      }
      return;
    }
    // If the eligibility effect just fired (same commit), it scheduled a
    // debounced replacement under the new key. Skip to avoid a duplicate
    // immediate request under the old key.
    if (pendingEligibilityKeyRef.current !== null) return;
    const state = stateRef.current;
    if (state.discovery.phase !== "closed") return;
    const gen = ++discoveryGenRef.current;
    void runReplacement(state.discovery.normalizedQuery, gen);
  }, [open, runReplacement]);

  // #735 — an open from the trigger hands the keyboard to the search input
  // and selects the committed name, so the first keystroke starts a search
  // rather than appending to "Sim House A". Declared AFTER the open effect so
  // it never runs before the input exists in the same commit. The Retry paths
  // above still call focus() themselves: those run from a button that has
  // already taken focus, so this effect (keyed on `open`) does not fire.
  //
  // Select-all stays (typing must replace the name, #735), but a selection
  // wider than the field scrolls the browser to its active end, showing the
  // TAIL of a long name. `"backward"` puts the active end at index 0, and
  // `scrollLeft = 0` forces the head into view, which the direction alone
  // did not reliably do in Chromium.
  useEffect(() => {
    if (!open || disabled) return;
    const inputEl = document.getElementById(id);
    if (inputEl instanceof HTMLInputElement) {
      inputEl.focus();
      inputEl.setSelectionRange(0, inputEl.value.length, "backward");
      inputEl.scrollLeft = 0;
    }
  }, [open, disabled, id]);

  // #826 — `Autocomplete` now owns arrow/Enter navigation and highlight
  // tracking (the engine no longer hand-rolls a `role=combobox`/`listbox`
  // pair). `handleHomeEndKeys={false}` on the element below keeps Home/End
  // native (FR-031). Two things `Autocomplete` does NOT know about stay here:
  // FR-032 (Down Arrow at the loaded end with `hasMore` requests the next
  // page) and the Escape cancellation contract (US2: restore committed/blank
  // text, retain the discovery window, call the page's `onEscape`).
  const highlightedIdRef = useRef<string | null>(null);

  // US2: cancel exploration and restore committed/blank text — shared by
  // Escape (`Autocomplete`'s `onClose` with reason "escape", below). The
  // discovery window (items, cursor, hasMore, phase) is retained, never
  // wiped, and the discovery generation is untouched: an in-flight request
  // settles into the retained window under its own generation.
  const cancelExploration = useCallback(() => {
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    // `highlightedIdRef` is set only by `onHighlightChange`; clear it here so
    // a reopen's first ArrowDown cannot read a stale `atEnd` (FR-032).
    highlightedIdRef.current = null;
    const selection = stateRef.current.selection;
    // A fixed requested-ID read belongs to the page's selection intent and
    // must still be allowed to settle.
    if (selection.phase !== "resolving" || selection.requestedId === null) {
      selectionGenRef.current += 1;
    }
    const committed = selection.entity;
    setState((prev) => ({
      ...prev,
      discovery: { ...prev.discovery, rawQuery: committed?.name ?? "" },
    }));
    setCommittedText(committed?.name ?? null);
  }, []);

  // FR-032: Down Arrow at the loaded end with `hasMore` requests the next
  // page instead of moving the highlight (there is nowhere further to move
  // it). Wired onto `Autocomplete`'s own `onKeyDown` prop, which
  // `useAutocomplete.js`'s `getRootProps` calls BEFORE its own switch, so
  // `highlightedIdRef` still holds the option the PREVIOUS keypress left
  // highlighted — the same "already at the end" check the hand-rolled
  // `onArrow` used to make against `activeId`.
  //
  // Escape is ALSO handled here rather than through `Autocomplete`'s own
  // `onClose` callback: `useAutocomplete.js`'s own Escape case calls
  // `event.stopPropagation()` after closing the popup, which — because
  // `FlockPicker`/`CustomerPicker` render inside dialogs whose own
  // Escape-to-close listens on an ancestor — swallowed a SINGLE Escape press
  // that used to close both the exploration AND the dialog at once
  // (SalesPage's new-order picker test, #826 round 1). Setting
  // `defaultMuiPrevented` tells `getRootProps`'s `handleKeyDown` (which calls
  // our handler FIRST, then checks that flag before its own switch) to skip
  // its Escape case entirely, so only OUR `preventDefault` runs and the event
  // keeps bubbling — restoring the pre-#826 behavior exactly.
  const handleRootKeyDown = useCallback((e: KeyboardEvent<HTMLDivElement> & { defaultMuiPrevented?: boolean }) => {
    if (disabled) return;
    if (e.key === "Escape") {
      e.preventDefault();
      e.defaultMuiPrevented = true;
      cancelExploration();
      onEscapeRef.current?.();
      return;
    }
    if (e.key !== "ArrowDown") return;
    const d = stateRef.current.discovery;
    if (d.items.length === 0) return;
    const atEnd = highlightedIdRef.current === d.items[d.items.length - 1].id;
    if (atEnd && d.hasMore && d.phase === "ready") {
      // Also suppress `Autocomplete`'s own ArrowDown handling for THIS
      // press: `options` has not grown yet (the extension is async), so its
      // `changeHighlightedIndex` would move against the STALE, still-short
      // list — and because `disableListWrap` defaults to `false`, moving
      // past the last valid index WRAPS to the first option instead of
      // clamping. Left alone, every boundary press would silently snap the
      // highlight back to the top of a growing list instead of holding
      // still for the extension to land — found via
      // `named-entity-picker.spec.ts`'s real-browser keyboard-paging test,
      // not reasoned about. The next ArrowDown (after `options` grows) again
      // gets no special handling and moves forward normally.
      e.preventDefault();
      e.defaultMuiPrevented = true;
      void loadMore();
    }
  }, [disabled, loadMore, cancelExploration]);

  // Exploration (FR-020): the visible text differs from the committed label.
  // A committed picker with an untouched field is not exploring.
  const committedName = state.selection.entity?.name ?? null;
  const exploring = committedName === null
    ? state.discovery.rawQuery.trim() !== ""
    : state.discovery.rawQuery !== committedName;
  // US2: canSubmit is true when the picker is safe to write with. For
  // required pickers: a committed entity is present. For optional pickers:
  // no exploration in progress (blank/uninitialized/committed all qualify).
  const canSubmit = !exploring && (state.selection.phase === "committed"
    || ((state.selection.phase === "blank" || state.selection.phase === "uninitialized") && !required && !requestedId));

  // Snapshot emission: only when committed/selectionPhase/exploring/canSubmit
  // actually change. A parent that calls setSnapshot with the received object
  // must not loop merely because the callback identity is fresh on every
  // render — the engine holds the latest callback in a ref and compares the
  // four snapshot fields against the last emitted values.
  const onSnapshotRef = useRef(onSnapshot);
  onSnapshotRef.current = onSnapshot;
  const lastSnapshotRef = useRef<{ committed: T | null; selectionPhase: SelectionPhase; exploring: boolean; canSubmit: boolean } | null>(null);
  useEffect(() => {
    const last = lastSnapshotRef.current;
    if (last && last.committed === state.selection.entity
      && last.selectionPhase === state.selection.phase
      && last.exploring === exploring
      && last.canSubmit === canSubmit) return;
    lastSnapshotRef.current = { committed: state.selection.entity, selectionPhase: state.selection.phase, exploring, canSubmit };
    onSnapshotRef.current({ committed: state.selection.entity, selectionPhase: state.selection.phase, exploring, canSubmit });
  });

  // US2: Escape handler — the page provides the close callback. The engine
  // holds it in a ref so onKey's closure stays stable.
  const onEscapeRef = useRef<(() => void) | null>(null);
  onEscapeRef.current = onEscape ?? null;

  // US2: controlled committed state. When the page externally resets its
  // selection (deep link, URL change, dialog reset, create-then-hydrate),
  // it passes a new entity or null via `controlledCommitted` and bumps
  // `controlledGeneration`. The engine synchronizes its internal committed
  // state so that a later Escape or exploration cannot resurrect a stale ID.
  //
  // INITIAL VALUE: 0, not -1. The page's first controlled sync (mount-time
  // default, deep link, remembered) passes `controlledGeneration={1}` (or
  // higher). Starting at -1 would skip the FIRST sync when the page passes
  // 0 (a legitimate "no change yet" value) — but more importantly, the page
  // often passes 1 as its first bump, and the engine's `=== lastControlledGenRef.current`
  // check would then skip it if lastControlledGenRef started at 0 (a common
  // off-by-one). Starting at -1 makes the first sync (gen 0 or 1) always fire.
  // The page's own gen counter starts at 0 and bumps to 1 on its first
  // external reset, so the engine's -1 start guarantees the page's gen-1
  // sync is always observed.
  const lastControlledGenRef = useRef(-1);
  // US3 (T035): exact-identity resolution. The controlled sync may hand the
  // engine a full typed entity the DISCOVERY window never contained (a row-
  // owned or late-sorting identity). The engine commits it directly AND, when
  // an exact read is available, validates it through that read — a scoped 404
  // or transport failure enters `unavailable` (never a first-result
  // substitution). The read is generation-owned like every other request: a
  // superseded transition's resolution is dropped.
  const fetchExactRef = useRef(fetchExact);
  fetchExactRef.current = fetchExact;
  useEffect(() => {
    if (controlledGeneration === undefined || controlledGeneration === lastControlledGenRef.current) return;
    lastControlledGenRef.current = controlledGeneration;
    const entity = controlledCommitted ?? null;
    // A SELECTION transition: never bumps the discovery generation (an
    // in-flight replacement keeps its own generation and still settles) and
    // never wipes the discovery window.
    selectionGenRef.current += 1;
    setCommittedText(entity?.name ?? null);
    setState((prev) => {
      // If the picker is already showing results for this same entity, preserve
      // the discovery window (items, cursor, hasMore) — only sync the selection.
      // This avoids clearing a loaded list when the page re-hydrates the same
      // flock (e.g. the controlled-gen effect fires on mount after the picker
      // already discovered).
      const sameEntity = prev.selection.entity?.id === entity?.id;
      return {
        ...prev,
        selection: { entity, requestedId: null, phase: entity ? "committed" : "blank", transitionGeneration: prev.selection.transitionGeneration },
        discovery: sameEntity
          ? prev.discovery
          : { ...prev.discovery, rawQuery: entity?.name ?? "", items: [], hasMore: false, cursor: 0 },
      };
    });
    // Exact-identity validation: only when an exact read is provided AND the
    // entity is NOT already known (in the discovery window OR already the
    // committed selection from a genuine commit). A list-known or already-
    // committed entity is admitted as-is — no spurious GET on every page mount
    // or on every commit round-trip. The read is generation-owned; a failure
    // enters `unavailable` (committed stays null, canSubmit false) — never the
    // first result.
    // A full typed entity (with a real name) is admitted as-is: pages commit
    // the full entity from a genuine pick or from a page-owned source, and
    // re-validating every such commit with a GET would be a spurious round-
    // trip. Row-owned / late-sorting identity resolution (exact GET) is handled
    // by the `requestedId` effect below (T038), which fires only when the page
    // explicitly names an ID it does not have the full entity for.
    // The controlledCommitted path admits the entity as-is (US1/US2 behavior).
    // Row-owned / late-sorting identity resolution (exact GET) is handled by
    // the `requestedId` effect below (T038), which fires only when the page
    // explicitly names an ID it does not have the full entity for.
  }, [controlledCommitted, controlledGeneration]);

  // US3 (T038): a page-named row-owned ID the engine does not yet have the
  // full entity for (not in the discovery window, not the current committed
  // selection). Resolved through the exact read; a failure enters
  // `unavailable` (never a first-result substitution). Generation-owned.
  const previousRequestedIdRef = useRef(requestedId ?? null);
  useEffect(() => {
    const reqId = requestedId ?? null;
    const previousRequestedId = previousRequestedIdRef.current;
    previousRequestedIdRef.current = reqId;
    if (!reqId) {
      if (previousRequestedId === null) return;
      const gen = ++selectionGenRef.current;
      setCommittedText(null);
      setState((prev) => ({
        ...prev,
        selection: { entity: null, requestedId: null, phase: "blank", transitionGeneration: gen },
        discovery: { ...prev.discovery, rawQuery: "" },
      }));
      return;
    }
    if (!fetchExactRef.current) return;
    // Already committed to this exact id, or it is in the window: admit as-is.
    const inWindow = stateRef.current.discovery.items.some((x) => x.id === reqId);
    const alreadyCommitted = stateRef.current.selection.entity?.id === reqId;
    if (inWindow || alreadyCommitted) return;
    // Debounce by the controlled generation so a rapid open/close doesn't
    // double-fire; the read is owned by the SELECTION-transition generation
    // (it never gates discovery requests).
    const gen = ++selectionGenRef.current;
    setCommittedText(null);
    setState((prev) => ({
      ...prev,
      selection: { entity: null, requestedId: reqId, phase: "resolving", transitionGeneration: gen },
    }));
    void fetchExactRef.current(reqId).then((resolved) => {
      if (selectionGenRef.current !== gen) return;
      // Sync the discovery window to the resolved identity (same contract as
      // retryUnavailable): without it, the committed picker still reports
      // exploring=true and withholds a safe write.
      setCommittedText(resolved.name);
      setState((prev) => ({
        ...prev,
        selection: { entity: resolved, requestedId: null, phase: "committed", transitionGeneration: gen },
        discovery: { ...prev.discovery, rawQuery: resolved.name },
      }));
    }).catch(() => {
      if (selectionGenRef.current !== gen) return;
      setCommittedText(null);
      setState((prev) => ({
        ...prev,
        selection: { entity: null, requestedId: reqId, phase: "unavailable", transitionGeneration: gen },
      }));
    });
  }, [requestedId, controlledGeneration]);

  // US2: outside-click cancellation. When the picker is open and the user
  // clicks outside the picker container: cancel exploration (restore committed
  // or blank text), cancel debounce, and close. The page's write-handler guard
  // (canSubmit) independently suppresses any same-interaction write.
  const containerRef = useRef<HTMLDivElement>(null);
  const onOutsideClickRef = useRef(onOutsideClick);
  onOutsideClickRef.current = onOutsideClick;
  useEffect(() => {
    if (!open) return;
    function handler(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        // Restore committed/blank text before closing (finding 5: reopen must
        // not show an abandoned query).
        // Restore the committed/blank text and invoke the callback, but
        // PRESERVE the discovery window (items, cursor, hasMore, phase) and
        // do NOT bump the discovery generation: the page's onOutsideClick may
        // keep the picker open, and an always-open picker must retain its
        // options and remain usable. The debounce is still cancelled
        // separately (a stale typed replacement must not fire after restore).
        if (debounceRef.current !== null) {
          window.clearTimeout(debounceRef.current);
          debounceRef.current = null;
        }
        // Same stale-ref hazard as `cancelExploration`.
        highlightedIdRef.current = null;
        const selection = stateRef.current.selection;
        // Outside-click closes exploration, not the page-owned requested-ID
        // intent. Keep that exact read live while the picker is closed.
        if (selection.phase !== "resolving" || selection.requestedId === null) {
          selectionGenRef.current += 1;
        }
        const committed = selection.entity;
        setState((prev) => ({
          ...prev,
          discovery: {
            ...prev.discovery,
            rawQuery: committed?.name ?? "",
          },
        }));
        setCommittedText(committed?.name ?? null);
        onOutsideClickRef.current?.();
      }
    }
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const d = state.discovery;
  // The clone (see `value` below) is memoised by id/name: `useAutocomplete`'s
  // `syncHighlightedIndex` closes over `value` by reference and re-runs when
  // it changes, so a fresh clone every render re-synced the highlight to the
  // committed row on unrelated renders, including one mid page-load.
  const selectedEntity = state.selection.entity;
  const clonedSelectedValue = useMemo(
    () => (selectedEntity ? { ...selectedEntity } : null),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [selectedEntity?.id, selectedEntity?.name],
  );
  const showLoading = d.phase === "replacing" || d.phase === "extending" || d.phase === "debouncing";
  // The input value IS the discovery's raw text — no committed-text fallback.
  // After a commit the field shows the committed name (rawQuery was set to it);
  // typing explores and the committed entity survives in `selection`.
  const displayText = d.rawQuery;
  // The retention label (FR-018): the committed name, shown as a sibling of
  // the input while the visible text differs from it. Never inside the listbox.
  const showCommitted = committedText !== null && d.rawQuery !== committedText;

  // US3 (T034): the stable aria-live region's announcement text — one localized
  // string per state, mirroring what a screen reader should hear. Visually
  // hidden (CSS); the transient inline spans remain for sighted users.
  const liveMessage =
    d.phase === "replacing" || d.phase === "extending" || d.phase === "debouncing"
      ? t("loading")
      : d.phase === "replacement-error"
        ? t("searchFailed")
      : d.phase === "empty"
        ? t("noResults")
        : state.selection.phase === "unavailable"
          ? t("unavailable")
          : d.phase === "ready" || d.phase === "extension-error"
            ? t("results", { count: d.items.length })
            : "";

  // US2: optional clear — commits blank. A selection transition only: the
  // discovery window is retained and its generation is untouched.
  const clearSelection = useCallback(() => {
    selectionGenRef.current += 1;
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    setCommittedText(null);
    setState((prev) => ({
      ...prev,
      selection: { entity: null, requestedId: null, phase: "blank", transitionGeneration: selectionGenRef.current },
      discovery: { ...prev.discovery, rawQuery: "" },
    }));
    onClearRef.current?.();
  }, []);

  // #826 — the status/alert/Load-more/Clear footer renders INSIDE
  // `Autocomplete`'s floating Paper, as a sibling of the `<ul role="listbox">`
  // it supplies as `children` — never through `slotProps.listbox`, which
  // would put a `<button>` where ARIA only allows `option`/`group`.
  //
  // `PickerPaper` itself must keep a STABLE identity across renders:
  // `Autocomplete` mounts it via `as={PaperSlot}`, so a fresh function value
  // every render would remount the whole popup subtree on every keystroke —
  // including the stable `aria-live` region below, whose contract (US3 T034)
  // is that it is the SAME node across loading/results/error transitions.
  // `footerRef` carries the latest values the footer reads; the `useRef`
  // lazy-init below creates the component exactly once per picker instance.
  const footerData = {
    t, showLoading, d, disabled, required,
    committedEntity: state.selection.entity,
    unavailable: state.selection.phase === "unavailable",
    retry, loadMore, retryUnavailable, clearSelection, liveMessage,
    pinnedChoice,
  };
  const footerRef = useRef(footerData);
  footerRef.current = footerData;
  const pickerPaperRef = useRef<((props: PaperProps) => React.ReactElement) | null>(null);
  if (pickerPaperRef.current === null) {
    pickerPaperRef.current = function PickerPaper({ children, ...paperProps }: PaperProps) {
      const f = footerRef.current;
      return (
        <Paper {...paperProps}>
          {f.pinnedChoice}
          {children}
          <div className="named-picker-meta">
            {/* US3 (T034): a STABLE mounted aria-live region — the SAME node
                across loading/results/error transitions — so assistive tech
                announces the picker's state without the visible spans being
                re-created (and without moving focus off the input). It is
                deliberately NOT role="status": the transient loading span
                below owns that role for the visible UI, and a second
                [role=status] would make getByRole("status") ambiguous. Its
                text mirrors the state for screen readers. */}
            <span className="named-picker-live" aria-live="polite" aria-atomic="true" aria-busy={f.showLoading || undefined}>
              {f.liveMessage}
            </span>
            {f.showLoading && <span className="named-picker-status" role="status">{f.t("loading")}</span>}
            {f.d.phase === "empty" && <span className="named-picker-status" role="status">{f.t("noResults")}</span>}
            {f.d.phase === "replacement-error" && (
              <span className="named-picker-status" role="alert">{f.t("searchFailed")}</span>
            )}
            {f.d.phase === "replacement-error" && (
              <button type="button" className="named-picker-retry link" onClick={() => void f.retry()} disabled={f.disabled}>
                {f.t("retry")}
              </button>
            )}
            {f.d.phase === "extension-error" && (
              <span className="named-picker-status" role="alert">{f.t("loadMoreFailed")}</span>
            )}
            {f.d.phase === "extension-error" && (
              <button type="button" className="named-picker-retry link" onClick={() => void f.retry()} disabled={f.disabled}>
                {f.t("retry")}
              </button>
            )}
            {f.d.hasMore && (
              <button type="button" className="named-picker-loadmore link" onClick={() => void f.loadMore()} disabled={f.showLoading || f.disabled}>
                {f.t("loadMore")}
              </button>
            )}
            {/* US3 (T034/T038): an unavailable exact identity (scoped 404 /
                transport failure on the exact GET) renders the translated
                unavailable label and a keyboard-reachable Retry — never a raw
                ID or a first-result substitution. The Retry re-runs the exact
                read; focus returns to the input on success. */}
            {f.unavailable && (
              <span className="named-picker-status" role="alert">{f.t("unavailable")} — {f.t("unavailableExplanation")}</span>
            )}
            {/* Deliberately NOT gated on `disabled` (US3 remediation): Retry
                re-resolves a FIXED identity via the exact GET, not ordinary
                discovery/selection, which stays disabled above. */}
            {f.unavailable && (
              <button type="button" className="named-picker-retry link" onClick={() => void f.retryUnavailable()}>
                {f.t("retry")}
              </button>
            )}
            {!f.required && f.committedEntity && !f.disabled && (
              <button type="button" className="named-picker-clear link" onClick={f.clearSelection}>
                {f.t("clear")}
              </button>
            )}
          </div>
        </Paper>
      );
    };
  }

  // US2: closed state renders exactly ONE trigger in the normal form slot.
  // The combobox/listbox are absent. Open state: no trigger, just the
  // searchable combobox in the same position.
  if (!open) {
    // Closed state (owner redesign, 2026-09-17): the committed value reads
    // as the SAME outlined MUI field as the open search — label in the
    // border, an end adornment signaling "this reopens a search" — not a
    // plain button (a #896-review finding: the old trigger read as "a plain
    // dark box with no affordance that it reopens a search"). Only the
    // CHROME moves from a hand-rolled `<button>`/`<span>` to a read-only
    // `TextField`: click, Enter and Space all open the search exactly as the
    // trigger's own `onClick` did before.
    //
    // The `trigger` prop still supplies BOTH the interaction contract (its
    // `onClick` opens the picker; its own `disabled` combines with the
    // engine's) and the DISPLAYED text. The text stays trigger-owned rather
    // than switching to the engine's own `committedText`: each caller
    // already computes "committed name or placeholder" itself, and that
    // placeholder differs screen to screen ("No flocks yet", "All", a
    // loading/unavailable fallback on Sales' filter) — `committedText` alone
    // has no placeholder to fall back to.
    const hasTrigger = React.isValidElement(trigger);
    const triggerProps = hasTrigger
      ? (trigger as React.ReactElement<{
          onClick?: (e: React.SyntheticEvent) => void;
          disabled?: boolean;
          children?: ReactNode;
        }>).props
      : undefined;
    const triggerDisabled = disabled || !!triggerProps?.disabled;
    const activate = (e: React.SyntheticEvent) => {
      if (triggerDisabled) return;
      triggerProps?.onClick?.(e);
    };
    const displayValue = typeof triggerProps?.children === "string" ? triggerProps.children : "";
    return (
      <div ref={containerRef} className={`named-picker${disabled ? " disabled" : ""}`}>
        {hasTrigger ? (
          <TextField
            id={id}
            label={label}
            required={required}
            disabled={triggerDisabled}
            value={displayValue}
            onClick={activate}
            // MUI's default ("medium") horizontal padding, on a field this
            // narrow inside a two-up phone row (`.form-grid .named-picker`'s
            // 8rem floor), left almost no room for the value text — a real
            // capture at 390px showed a committed "Capture Test Flock"
            // rendered as just "C…". `size="small"` reclaims that padding.
            size="small"
            // #916 review — `size="small"`'s own wrapper height (~37px) sits
            // under the 44px tap-target floor every other control on this
            // screen meets. Verified against all five callers: none sets a
            // conflicting height, so this only ever grows the field.
            sx={{ "& .MuiInputBase-root": { minHeight: 44 } }}
            slotProps={{
              htmlInput: {
                readOnly: true,
                "aria-haspopup": "listbox",
                "aria-expanded": false,
                "aria-required": required || undefined,
                onKeyDown: (e: React.KeyboardEvent<HTMLInputElement>) => {
                  if (e.key !== "Enter" && e.key !== " ") return;
                  e.preventDefault();
                  activate(e);
                },
              },
              input: {
                endAdornment: (
                  <InputAdornment position="end">
                    {/* #864's own Daily Entry mockup (daily-entry-390.png)
                        draws this affordance as a chevron, not a magnifier —
                        checked against it before picking between the two
                        icons the owner's instruction named as acceptable. */}
                    <ChevronDown size={18} strokeWidth={1.5} aria-hidden focusable={false} />
                  </InputAdornment>
                ),
              },
            }}
          />
        ) : (
          // No trigger element was provided (optional on the typed adapters):
          // there is no control to activate and no value to show as a field,
          // so render only the visible label text.
          <span className="named-picker-label">{label}</span>
        )}
        {/* US3 remediation — the engine used to render unavailable/Retry ONLY
            in the open branch above, so a page whose picker never opens
            (History/Feed row-owned ids resolved without a combobox) or is
            `disabled` (Water's edit-locked capture picker) had NO recovery
            affordance when the exact GET failed: the trigger just showed its
            fallback text with no way back in. Adjacent to the trigger, same
            translated strings, same GET-only Retry — deliberately NOT gated
            on `disabled` (see retryUnavailable's own comment). */}
        {state.selection.phase === "unavailable" && (
          <span className="named-picker-status" role="alert">{t("unavailable")} — {t("unavailableExplanation")}</span>
        )}
        {state.selection.phase === "unavailable" && (
          <button type="button" className="named-picker-retry link" onClick={() => void retryUnavailable()}>
            {t("retry")}
          </button>
        )}
      </div>
    );
  }

  return (
    <div ref={containerRef} className={`named-picker${disabled ? " disabled" : ""}`}>
      {/* Owner redesign, 2026-09-17: the floating label lives on the
          `TextField` (`renderInput` below) now, not a separate `<label>` —
          `Autocomplete`/`TextField` wire `id`/`InputLabel` themselves, so
          `getByLabelText(label)` still resolves to the real input. */}
      {showCommitted && (
        <div className="named-picker-committed">{committedText}</div>
      )}
      {/* Explicit generics: the committed value is genuinely `T | null`
          (blank/uninitialized are real states). MUI's own `disableClearable`
          PROP ties `DisableClearable` to `true` at the type level too
          (narrowing `value`/`onChange` to exclude `null`), which is why the
          clear icon is suppressed via `slots.clearIndicator` below instead —
          that leaves this generic, and the value type, alone. */}
      <Autocomplete<T, false, false, false>
        id={id}
        open
        // Set here, not on the TextField in `renderInput`: `Autocomplete`
        // forwards `size` into `params.size`, which `{...params}` already
        // spreads onto the TextField below — the same 390px-width overflow
        // fix as the closed-state field's own `size="small"`.
        size="small"
        disabled={disabled}
        options={d.items}
        loading={showLoading}
        inputValue={displayText}
        // A shallow clone, not the entity itself: `useAutocomplete.js`'s
        // `handleValue` bails out (never calls `onChange`) when the newly
        // selected option is `===` the current `value` — reference equality,
        // not `isOptionEqualToValue`. A picker whose committed entity and
        // discovery window happen to hold the SAME object reference (e.g. a
        // page-level default customer, FR-037, fetched once and handed to
        // both `controlledCommitted` and the picker's own discovery request)
        // would then silently no-op on re-clicking that already-committed
        // option — a real regression, caught by SalesPage's new-order dialog
        // test, not merely a test mock artifact: a caching or memoizing data
        // layer could produce the same shared reference in production. The
        // clone guarantees `value !== newValue`, so `onChange` always fires;
        // `isOptionEqualToValue` (by id, below) still drives `aria-selected`.
        // MEMOIZED (not recreated every render) — see `clonedSelectedValue`'s
        // own comment above for why an unmemoized clone here broke Load
        // More's highlight retention.
        value={clonedSelectedValue}
        onChange={(_event, newValue, reason) => {
          // Commit by pointer/Enter (FR-030) — the ONLY two ways MUI's own
          // handling reaches `reason === "selectOption"`. Guarded on
          // `disabled` here (not just on the closed native input) because
          // `Autocomplete`'s own key handling is not itself gated on it.
          if (disabled) return;
          if (reason === "selectOption" && newValue) commit(newValue);
        }}
        onInputChange={(_event, newInputValue, reason) => {
          // Only genuine typing re-discovers. `Autocomplete` also fires this
          // with reason "reset" right after a commit (syncing its internal
          // input value to the selected label) and "clear" from its own
          // clear icon (disabled below) — `commit`/`clearSelection` already
          // own `rawQuery` for those, so re-running discovery for them would
          // wipe the just-loaded results.
          if (reason === "input") onQueryChange(newInputValue);
        }}
        // No `onClose`: Escape is fully handled by `handleRootKeyDown`
        // (`onKeyDown` below), which runs before — and here, instead of —
        // `Autocomplete`'s own Escape case. Outside-click keeps its own
        // long-standing `mousedown` listener (above): `Autocomplete` also
        // treats an outside click as a "blur" close, and wiring both would
        // double-fire `onOutsideClick`.
        onHighlightChange={(_event, option) => {
          highlightedIdRef.current = option ? option.id : null;
        }}
        onKeyDown={handleRootKeyDown}
        filterOptions={(options) => options}
        getOptionLabel={(option) => option.name}
        getOptionKey={(option) => option.id}
        isOptionEqualToValue={(option, value) => option.id === value.id}
        // One line per option with an ellipsis; the popper's own floor is set
        // below.
        renderOption={({ key, ...optionProps }, option) => (
          <li key={key} {...optionProps}>
            <Box component="span" sx={{ display: "block", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
              {option.name}
            </Box>
          </li>
        )}
        // US2/US3 own commit, retry and clear (in the footer below);
        // `Autocomplete`'s built-in clear/popup icons would duplicate that
        // UI, so both are nulled via `slots` (not `disableClearable` — see
        // the generics comment above). `handleHomeEndKeys={false}` keeps
        // Home/End native input behavior (FR-031) — `Autocomplete` jumps to
        // the first/last option on those keys by default. `disableListWrap`
        // stops ArrowDown at the TRUE final option (hasMore already false)
        // from silently jumping back to the first row — found the same way
        // as the `handleRootKeyDown` comment above, via
        // `named-entity-picker.spec.ts`'s keyboard-paging test, which presses
        // ArrowDown at the true end and asserts the highlight stays.
        handleHomeEndKeys={false}
        disableListWrap
        // The popup stays a DOM descendant of `containerRef` (no portal to
        // `document.body`): the engine's own outside-click listener (above)
        // tests `containerRef.current.contains(event.target)`, which a
        // portalled popper would defeat — a click on an option would read as
        // "outside" and wrongly cancel exploration.
        disablePortal
        clearText={t("clear")}
        closeText={t("close")}
        openText={t("open")}
        loadingText={t("loading")}
        noOptionsText={t("noResults")}
        // `Autocomplete` sizes the popper to the anchor's width, which in a
        // narrow phone row is too little to read a name against; `min-width`
        // beats that inline width, and `bottom-start` keeps it left-aligned
        // with the field.
        slotProps={{
          popper: {
            placement: "bottom-start",
            sx: { minWidth: "min(20rem, calc(100vw - 32px))" },
          },
        }}
        slots={{
          paper: pickerPaperRef.current,
          clearIndicator: () => null,
          popupIndicator: () => null,
          // The picker renders its OWN loading/no-results text in the footer
          // (it must also cover "loading while retained rows are still
          // shown", e.g. Load more in flight — a case `Autocomplete`'s own
          // status slot can't reach: it hides once options are non-empty).
          // Suppressing that slot avoids a second, usually-empty
          // `role="status"` node — it otherwise mounts whenever the popup is
          // open, which would make `getByRole("status")` ambiguous.
          status: () => null,
        }}
        renderInput={(params: AutocompleteRenderInputParams) => (
          <TextField
            {...params}
            label={label}
            required={required}
            placeholder={committedText ?? undefined}
            // #916 review — same 44px floor as the closed-state trigger field
            // above; the open search field is the one every mockup measured.
            sx={{ "& .MuiInputBase-root": { minHeight: 44 } }}
            slotProps={{
              ...params.slotProps,
              htmlInput: {
                ...params.slotProps.htmlInput,
                // The literal attribute: `required` above gives the native
                // HTML attribute (and MUI's own asterisk) but not this — no
                // picker label carries its own "*" today (contrast
                // `useConfirm.tsx`'s `reasonLabel`, which does and so skips
                // the `required` prop to avoid a doubled asterisk).
                "aria-required": required || undefined,
              },
            }}
          />
        )}
      />
    </div>
  );
}
